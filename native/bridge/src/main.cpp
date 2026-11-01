#include "json.hpp"

#include "sed/assets/jkl.hpp"
#include "sed/assets/sed.hpp"
#include "sed/text.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <iostream>
#include <new>
#include <optional>
#include <span>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

namespace bridge {
constexpr std::size_t maxInputBytes = 16777216;
constexpr std::size_t maxDiagnosticBytes = 256;
constexpr std::size_t maxDiagnostics = 32;
constexpr std::size_t maxItems = 32;
constexpr std::size_t maxJklLines = 131072;
constexpr std::size_t maxJklLineBytes = 65536;
constexpr std::size_t maxJklTokens = 1048576;
constexpr std::size_t maxJklSurfaceReferences = 131072;
constexpr std::size_t maxJklSurfaceExpansionTokens = 1048576;
constexpr std::size_t maxJklExpansionBytes = 67108864;

struct Failure final : std::runtime_error {
    Failure(int exit, std::string_view errorCode, std::string_view message)
        : std::runtime_error(std::string(message.substr(0, maxDiagnosticBytes))),
          exitCode(exit), code(errorCode) {}
    int exitCode;
    std::string_view code;
};

struct Request {
    std::string_view operation = "unknown";
    std::string_view requestId;
    sed::ProjectType kind = sed::ProjectType::JKDF2;
    std::size_t inputBytes = 0;
};

bool validRequestId(std::string_view value)
{
    return value.size() == 32 && std::all_of(value.begin(), value.end(), [](char c) {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
    });
}

void parseArguments(int argc, char** argv, Request& request)
{
    // Recover only recognized operation/id tokens in their fixed positions.
    // Never reflect an arbitrary argument into the response envelope.
    if (argc > 3) {
        const std::string_view operation = argv[3];
        if (operation == "capabilities" || operation == "inspect-jkl"
            || operation == "inspect-native-sed") request.operation = operation;
    }
    if (argc > 7 && std::string_view(argv[6]) == "--request-id" && validRequestId(argv[7]))
        request.requestId = argv[7];
    const auto invalid = [] { throw Failure{2, "invalid_request", "Expected the fixed protocol-1 argument contract."}; };
    if (argc < 4 || std::string_view(argv[1]) != "--protocol"
        || std::string_view(argv[2]) != "1" || request.operation == "unknown") invalid();
    if (request.operation == "capabilities") {
        if (argc != 4) invalid();
        return;
    }
    if (argc != 8 || std::string_view(argv[4]) != "--project-kind"
        || std::string_view(argv[6]) != "--request-id" || request.requestId.empty()) invalid();
    const std::string_view kind = argv[5];
    if (kind == "jk") request.kind = sed::ProjectType::JKDF2;
    else if (kind == "mots") request.kind = sed::ProjectType::MOTS;
    else if (kind == "ijim") request.kind = sed::ProjectType::IJIM;
    else invalid();
}

void common(Json& json, const Request& request, std::string_view status)
{
    json.append("{\"schemaVersion\":1,\"name\":\"sed-cpp-bridge\",\"version\":");
    json.string(SED_BRIDGE_VERSION);
    json.append(",\"upstreamCommit\":"); json.string(SED_BRIDGE_UPSTREAM_COMMIT);
    json.append(",\"sourceManifestSha256\":"); json.string(SED_BRIDGE_SOURCE_MANIFEST_SHA256);
    json.append(",\"operation\":"); json.string(request.operation);
    json.append(",\"requestId\":");
    if (request.requestId.empty()) json.append("null"); else json.string(request.requestId);
    json.append(",\"status\":"); json.string(status);
    json.append(",\"readOnly\":true,\"inputBytes\":"); json.number(request.inputBytes);
}

std::string errorResponse(const Request& request, int exit, std::string_view code, std::string_view message)
{
    Json json;
    common(json, request, exit >= 5 ? "failed" : "rejected");
    json.append(",\"error\":{\"code\":"); json.string(code);
    json.append(",\"message\":"); json.string(message.substr(0, maxDiagnosticBytes));
    json.append("}}");
    return json.finish();
}

std::string capabilities(const Request& request)
{
    Json json;
    common(json, request, "ok");
    json.append(",\"capabilities\":[\"inspect-native-sed\",\"inspect-jkl\"],"
                "\"projectKinds\":[\"jk\",\"mots\",\"ijim\"],\"writesFiles\":false,\"limits\":{");
    const auto limit = [&](std::string_view name, std::size_t value, bool comma = true) {
        json.string(name); json.append(":"); json.number(value); if (comma) json.append(",");
    };
    limit("maxInputBytes", maxInputBytes); limit("maxOutputBytes", 65536);
    limit("maxDiagnostics", maxDiagnostics); limit("maxDiagnosticBytes", maxDiagnosticBytes);
    limit("maxOptionalSections", maxItems); limit("maxIntegrityItems", maxItems);
    limit("maxJklLines", maxJklLines); limit("maxJklLineBytes", maxJklLineBytes);
    limit("maxJklTokens", maxJklTokens); limit("maxJklSurfaceReferences", maxJklSurfaceReferences);
    limit("maxJklSurfaceExpansionTokens", maxJklSurfaceExpansionTokens);
    limit("maxJklExpansionBytes", maxJklExpansionBytes, false);
    json.append("}}");
    return json.finish();
}

std::string readInput(Request& request)
{
    std::string bytes;
    std::array<char, 8192> block{};
    while (true) {
        const auto remaining = maxInputBytes + 1 - bytes.size();
        const auto wanted = std::min(remaining, block.size());
        std::cin.read(block.data(), static_cast<std::streamsize>(wanted));
        const auto count = static_cast<std::size_t>(std::cin.gcount());
        bytes.append(block.data(), count);
        request.inputBytes = bytes.size();
        if (bytes.size() > maxInputBytes)
            throw Failure{2, "input_limit", "Input exceeds the 16 MiB admission limit."};
        if (std::cin.bad()) throw Failure{5, "io_error", "Input stream failed."};
        if (std::cin.eof()) return bytes;
        if (std::cin.fail()) throw Failure{5, "io_error", "Input stream did not reach EOF cleanly."};
    }
}

void admitJkl(std::string_view bytes)
{
    if (bytes.empty() || bytes.find('\0') != std::string_view::npos)
        throw Failure{3, "parse_error", "JKL must contain text without NUL bytes."};
    std::size_t physicalLines = 0, meaningfulLines = 0, tokenCount = 0;
    std::size_t largestTokens = 0, largestLine = 0, surfaceReferences = 0;
    bool inHeader = false, versionSeen = false;
    for (std::size_t start = 0; start < bytes.size();) {
        const auto end = bytes.find('\n', start);
        const auto length = (end == std::string_view::npos ? bytes.size() : end) - start;
        if (++physicalLines > maxJklLines || length > maxJklLineBytes)
            throw Failure{2, "input_limit", "JKL line admission limit exceeded."};
        std::string line(bytes.substr(start, length));
        start += length + (end != std::string_view::npos ? 1 : 0);
        sed::text::stripHashComment(line);
        const auto trimmed = sed::text::trimView(line);
        if (trimmed.empty()) continue;
        const auto words = sed::text::splitWords(trimmed);
        ++meaningfulLines;
        largestLine = std::max(largestLine, trimmed.size());
        largestTokens = std::max(largestTokens, words.size());
        if (words.size() > maxJklTokens - tokenCount)
            throw Failure{2, "input_limit", "JKL token admission limit exceeded."};
        tokenCount += words.size();
        if (sed::text::iequalsAscii(trimmed, "END")) inHeader = false;
        else if (sed::text::istartsWithAscii(trimmed, "SECTION:"))
            inHeader = words.size() >= 2 && sed::text::iequalsAscii(words[1], "HEADER");
        else if (inHeader && sed::text::iequalsAscii(words[0], "VERSION")) {
            const auto version = words.size() >= 2 ? sed::text::parseInt<std::int32_t>(words[1]) : std::nullopt;
            if (!version || (*version != 1 && *version != 3))
                throw Failure{3, "parse_error", "JKL HEADER VERSION must be 1 or 3."};
            versionSeen = true;
        }
        // Over-approximation: count SURFACES rows in every section. This can
        // reject a valid unusual file, but cannot miss a sector expansion.
        if (sed::text::iequalsAscii(words[0], "SURFACES")) {
            const auto count = words.size() >= 3 ? sed::text::parseInt<std::int64_t>(words[2]) : std::nullopt;
            if (!count || *count < 0)
                throw Failure{3, "parse_error", "JKL SURFACES count is malformed."};
            if (static_cast<std::uint64_t>(*count) > maxJklSurfaceReferences - surfaceReferences)
                throw Failure{2, "input_limit", "JKL surface reference admission limit exceeded."};
            surfaceReferences += static_cast<std::size_t>(*count);
        }
    }
    if (!versionSeen) throw Failure{3, "parse_error", "JKL requires an explicit HEADER VERSION."};
    // Division avoids overflowing the conservative allocation upper bounds.
    if ((surfaceReferences != 0 && largestTokens > maxJklSurfaceExpansionTokens / surfaceReferences)
        || largestLine > maxJklExpansionBytes / (meaningfulLines + surfaceReferences))
        throw Failure{2, "input_limit", "JKL repeated geometry/string expansion admission limit exceeded."};
}

std::string_view projectKind(sed::ProjectType kind)
{
    switch (kind) {
    case sed::ProjectType::JKDF2: return "jk";
    case sed::ProjectType::MOTS: return "mots";
    case sed::ProjectType::IJIM: return "ijim";
    }
    throw Failure{6, "internal_error", "The decoder returned an unsupported project kind."};
}

std::string_view severity(sed::DiagnosticSeverity value)
{
    switch (value) {
    case sed::DiagnosticSeverity::verbose: return "verbose";
    case sed::DiagnosticSeverity::trace: return "trace";
    case sed::DiagnosticSeverity::debug: return "debug";
    case sed::DiagnosticSeverity::info: return "info";
    case sed::DiagnosticSeverity::warning: return "warning";
    case sed::DiagnosticSeverity::error: return "error";
    case sed::DiagnosticSeverity::fatal: return "fatal";
    }
    return "error";
}

struct Diagnostic { sed::DiagnosticSeverity severity; std::string message; };

std::string inspection(const Request& request, const sed::Level& level,
                       const sed::assets::SedFile* native,
                       const std::vector<Diagnostic>& diagnostics, bool diagnosticsTruncated)
{
    if (level.kind != request.kind)
        throw Failure{3, "project_kind_mismatch", "Decoded project kind differs from the explicit requested kind."};
    Json json;
    common(json, request, "ok");
    json.append(",\"projectKind\":"); json.string(projectKind(level.kind));
    json.append(",\"uvCoordinateSpace\":");
    json.string(level.uvCoordinateSpace == sed::UvCoordinateSpace::TexturePixels ? "texture-pixels" : "normalized");
    std::uint64_t surfaces = 0, vertices = 0;
    for (const auto& sector : level.sectors) {
        surfaces += sector.surfaces.size(); vertices += sector.vertices.size();
    }
    json.append(",\"summary\":{");
    const auto count = [&](std::string_view name, std::uint64_t value, bool comma = true) {
        json.string(name); json.append(":"); json.number(value); if (comma) json.append(",");
    };
    count("sectors", level.sectors.size()); count("surfaces", surfaces); count("vertices", vertices);
    count("things", level.things.size()); count("templates", level.templates.size()); count("cogs", level.cogs.size());
    count("lights", level.lights.size()); count("materials", level.materials.size());
    count("colormaps", level.colormaps.size()); count("architecturalLightingObjects", level.motsArchLighting.size(), false);
    json.append("},\"resourceValidation\":{\"status\":\"not-performed\",\"complete\":false,"
                "\"cogHydration\":\"deferred\",\"uvNormalization\":\"not-performed\"},\"diagnostics\":[");
    for (std::size_t index = 0; index < diagnostics.size(); ++index) {
        if (index) json.append(",");
        json.append("{\"severity\":"); json.string(severity(diagnostics[index].severity));
        json.append(",\"message\":"); json.string(diagnostics[index].message); json.append("}");
    }
    json.append("],\"diagnosticsTruncated\":"); json.boolean(diagnosticsTruncated);
    const bool repaired = native && (native->hadCorruption || native->repairedChunks != 0
        || !native->repairedSections.empty() || !native->unreadableRanges.empty());
    json.append(",\"integrity\":{\"status\":");
    json.string(!native ? "not-applicable" : repaired ? "repaired" : "clean");
    json.append(",\"hadCorruption\":"); json.boolean(native && native->hadCorruption);
    json.append(",\"repairedChunks\":"); json.number(native ? native->repairedChunks : 0);
    json.append(",\"repairedSections\":[");
    bool integrityTruncated = native && (native->repairedSections.size() > maxItems || native->unreadableRanges.size() > maxItems);
    if (native) for (std::size_t index = 0; index < std::min(native->repairedSections.size(), maxItems); ++index) {
        if (index) json.append(",");
        const auto& section = native->repairedSections[index];
        integrityTruncated = integrityTruncated || section.size() > maxDiagnosticBytes;
        json.string(std::string_view(section).substr(0, maxDiagnosticBytes));
    }
    json.append("],\"unreadableRanges\":[");
    if (native) for (std::size_t index = 0; index < std::min(native->unreadableRanges.size(), maxItems); ++index) {
        if (index) json.append(",");
        json.append("{\"offset\":"); json.number(native->unreadableRanges[index].offset);
        json.append(",\"length\":"); json.number(native->unreadableRanges[index].length); json.append("}");
    }
    json.append("],\"truncated\":"); json.boolean(integrityTruncated);
    json.append("},\"optionalSections\":[");
    if (native) for (std::size_t index = 0; index < std::min(native->optionalSections.size(), maxItems); ++index) {
        if (index) json.append(",");
        const auto& section = native->optionalSections[index];
        json.append("{\"type\":"); json.byteString({section.type.data(), section.type.size()});
        json.append(",\"payloadBytes\":"); json.number(section.payload.size()); json.append("}");
    }
    json.append("],\"optionalSectionsTruncated\":"); json.boolean(native && native->optionalSections.size() > maxItems);
    json.append("}");
    return json.finish();
}

std::string inspect(const Request& request, const std::string& bytes)
{
    if (request.operation == "inspect-native-sed") {
        const auto raw = std::as_bytes(std::span{bytes.data(), bytes.size()});
        // Strict public loader: no salvage/defaulting, path access, or save.
        const auto native = sed::assets::loadSedFileBytes(raw);
        return inspection(request, native.level, &native, {}, false);
    }
    admitJkl(bytes);
    std::vector<Diagnostic> diagnostics;
    bool truncated = false;
    sed::assets::JklLoadOptions options;
    options.projectType = request.kind;
    options.resources = nullptr;
    options.onDiagnostic = [&](sed::DiagnosticSeverity level, std::string_view message) {
        if (diagnostics.size() >= maxDiagnostics) { truncated = true; return; }
        truncated = truncated || message.size() > maxDiagnosticBytes;
        diagnostics.push_back({level, std::string(message.substr(0, maxDiagnosticBytes))});
    };
    std::istringstream stream(bytes);
    const auto level = sed::assets::loadFromJKL(stream, options);
    return inspection(request, level, nullptr, diagnostics, truncated);
}
} // namespace bridge

int main(int argc, char** argv)
{
    bridge::Request request;
    std::string response;
    int result = 0;
    try {
#ifdef _WIN32
        if (_setmode(_fileno(stdin), _O_BINARY) == -1 || _setmode(_fileno(stdout), _O_BINARY) == -1)
            throw bridge::Failure{5, "io_error", "Cannot configure binary standard streams."};
#endif
        bridge::parseArguments(argc, argv, request);
        if (request.operation == "capabilities") response = bridge::capabilities(request);
        else {
            const auto bytes = bridge::readInput(request);
            try { response = bridge::inspect(request, bytes); }
            catch (const bridge::Failure&) { throw; }
            catch (const bridge::OutputLimit&) { throw; }
            catch (const std::bad_alloc&) { throw; }
            catch (const std::exception& error) { throw bridge::Failure{3, "parse_error", error.what()}; }
        }
    } catch (const bridge::Failure& error) {
        result = error.exitCode;
        response = bridge::errorResponse(request, result, error.code, error.what());
    } catch (const bridge::OutputLimit& error) {
        result = 6; response = bridge::errorResponse(request, result, "output_limit", error.what());
    } catch (const std::bad_alloc&) {
        result = 6; response = bridge::errorResponse(request, result, "internal_error", "Allocation failed during inspection.");
    } catch (const std::exception&) {
        result = 6; response = bridge::errorResponse(request, result, "internal_error", "Unexpected worker failure.");
    } catch (...) {
        result = 6; response = bridge::errorResponse(request, result, "internal_error", "Unknown worker failure.");
    }
    std::cout.write(response.data(), static_cast<std::streamsize>(response.size()));
    std::cout.flush();
    return std::cout ? result : 5;
}

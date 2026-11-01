#pragma once

#include <cstddef>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <string_view>

namespace bridge {

class OutputLimit final : public std::runtime_error {
public:
    OutputLimit() : std::runtime_error("JSON output exceeds the protocol byte limit.") {}
};

// Bounded, complete-response construction. Never stream an incomplete success
// response to the host. The caller emits finish() only after all checks pass.
class Json {
public:
    explicit Json(std::size_t maximumBytes = 65536) : maximumBytes_(maximumBytes) {}

    void append(std::string_view value)
    {
        if (maximumBytes_ == 0 || value.size() >= maximumBytes_
            || bytes_.size() > maximumBytes_ - 1 - value.size()) throw OutputLimit{};
        bytes_.append(value);
    }

    void number(std::uint64_t value) { append(std::to_string(value)); }
    void boolean(bool value) { append(value ? "true" : "false"); }

    // FourCCs are byte identifiers, not Unicode. Each source byte becomes one
    // character, preserving the fixed four-byte identity without raw controls.
    void byteString(std::string_view value)
    {
        append("\"");
        for (unsigned char byte : value) {
            if (byte >= 0x80) escapedByte(byte);
            else ascii(byte);
        }
        append("\"");
    }

    void string(std::string_view value)
    {
        append("\"");
        for (std::size_t offset = 0; offset < value.size();) {
            const auto byte = static_cast<unsigned char>(value[offset]);
            if (byte < 0x80) { ascii(byte); ++offset; continue; }
            const std::size_t length = utf8Length(value.substr(offset));
            if (length == 0) { escapedByte(byte); ++offset; }
            else { append(value.substr(offset, length)); offset += length; }
        }
        append("\"");
    }

    [[nodiscard]] std::string finish() const { return bytes_ + '\n'; }

private:
    void escapedByte(unsigned char byte)
    {
        constexpr char hex[] = "0123456789abcdef";
        char escape[] = {'\\', 'u', '0', '0', hex[byte >> 4], hex[byte & 15]};
        append(std::string_view{escape, sizeof escape});
    }

    void ascii(unsigned char byte)
    {
        if (byte < 0x20) escapedByte(byte);
        else if (byte == '"') append("\\\"");
        else if (byte == '\\') append("\\\\");
        else { const char character = static_cast<char>(byte); append({&character, 1}); }
    }

    static std::size_t utf8Length(std::string_view value)
    {
        const auto first = static_cast<unsigned char>(value[0]);
        const std::size_t length = first >= 0xc2 && first <= 0xdf ? 2
            : first >= 0xe0 && first <= 0xef ? 3
            : first >= 0xf0 && first <= 0xf4 ? 4 : 0;
        if (length == 0 || value.size() < length) return 0;
        for (std::size_t index = 1; index < length; ++index) {
            const auto next = static_cast<unsigned char>(value[index]);
            if (next < 0x80 || next > 0xbf) return 0;
        }
        const auto second = static_cast<unsigned char>(value[1]);
        if ((first == 0xe0 && second < 0xa0) || (first == 0xed && second >= 0xa0)
            || (first == 0xf0 && second < 0x90) || (first == 0xf4 && second >= 0x90)) return 0;
        return length;
    }

    std::size_t maximumBytes_;
    std::string bytes_;
};
} // namespace bridge

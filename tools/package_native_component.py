#!/usr/bin/env python3
"""Package an explicitly qualified native inspection preview, offline.

Never runs Git, a compiler or the worker. Qualifications are reviewed operator
attestations, not signatures or independent proof that tests were executed.
"""
import argparse
import gzip
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import tarfile
import zipfile

UPSTREAM_COMMIT = "b82255998fb733c8e8c87104eb7c17f27b48376f"
LOCK_SHA256 = "d1f1473d7644e65dc3ec95b2cbdb0743732ef9fd8e45d010a064b7d45ddaf098"
UPSTREAM_SHA256 = "ef4ddb3ef0cee00fd7ddf8da17ff7a6fb058f11c5337b6e3ede366b8b66723f2"
UPSTREAM_BYTES = 8407040
UPSTREAM_PREFIX = "SED-core-b822559/"
MAX_SOURCE = 64 * 1024 * 1024
MAX_EXPANDED = 256 * 1024 * 1024
MAX_RUNTIME = 64 * 1024 * 1024
MAX_JSON = 1024 * 1024
WINDOWS_RUNTIME = {"sed-cpp-bridge.exe", "libc++.dll", "libunwind.dll"}
WRAPPER_REQUIRED = {".gitattributes", "CMakeLists.txt", "README.md", "protocol-v1.md",
                    "upstream.lock.json", "verify_upstream.py", "src/main.cpp", "src/json.hpp",
                    "tests/test_worker.py", "tests/test_upstream_lock.py", "tests/json_tests.cpp"}


class PackageError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise PackageError(message)


def exact_keys(value, keys, context):
    require(isinstance(value, dict) and set(value) == set(keys), f"Invalid keys in {context}")


def integer(value, minimum=0, maximum=2**53):
    return type(value) is int and minimum <= value <= maximum


def digest(data):
    return hashlib.sha256(data).hexdigest()


def hash_string(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def public_text(value, maximum=2048):
    return (isinstance(value, str) and 0 < len(value) <= maximum
            and not any(ord(c) < 32 and c not in "\n\t" for c in value)
            and "/" not in value and "\\" not in value
            and not re.search(r"\b[A-Za-z]:", value))


def canonical_json(value):
    return (json.dumps(value, ensure_ascii=True, sort_keys=True, indent=2, allow_nan=False) + "\n").encode()


def load_json_bytes(data):
    require(isinstance(data, bytes) and len(data) <= MAX_JSON, "JSON exceeds size bound")

    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, f"Duplicate JSON key: {key}")
            result[key] = value
        return result

    def nonfinite(value):
        raise PackageError("Non-finite JSON number")

    try:
        return json.loads(data.decode("utf-8", errors="strict"), object_pairs_hook=unique, parse_constant=nonfinite)
    except (ValueError, UnicodeError) as exc:
        raise PackageError(f"Invalid JSON: {exc}") from exc


def checked_path(path, *, output=False):
    require(isinstance(path, (str, Path)) and bool(str(path)), "Missing artifact path")
    candidate = Path(path)
    require(candidate.is_absolute() and ".." not in candidate.parts, "An absolute non-traversing path is required")
    for item in (candidate, *candidate.parents):
        try:
            info = item.lstat()
        except FileNotFoundError:
            require(output and item == candidate, f"Missing path: {item}")
            continue
        require(not stat.S_ISLNK(info.st_mode) and not getattr(info, "st_file_attributes", 0) & 0x400,
                f"Symlink or reparse path is not allowed: {item}")
        if output:
            require(not (item / ".git").exists(), "Output must be physically outside a Git checkout")
    return candidate


def read_artifact(record, maximum):
    require(isinstance(record, dict), "Artifact record must be an object")
    require(integer(record.get("bytes"), 0, maximum) and hash_string(record.get("sha256")), "Invalid artifact identity")
    path = checked_path(record.get("path"))
    before = path.stat()
    require(stat.S_ISREG(before.st_mode) and before.st_size == record["bytes"], "Artifact size or type mismatch")
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    with os.fdopen(os.open(path, flags), "rb") as stream:
        opened = os.fstat(stream.fileno())
        require((opened.st_dev, opened.st_ino, opened.st_size) == (before.st_dev, before.st_ino, before.st_size),
                "Artifact changed before read")
        data = stream.read(record["bytes"] + 1)
        after = os.fstat(stream.fileno())
    require((opened.st_size, opened.st_mtime_ns) == (after.st_size, after.st_mtime_ns), "Artifact changed during read")
    require(len(data) == record["bytes"] and digest(data) == record["sha256"], "Artifact bytes or SHA-256 mismatch")
    return data


def read_request(path):
    path = checked_path(path)
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    with os.fdopen(os.open(path, flags), "rb") as stream:
        before = os.fstat(stream.fileno())
        require(stat.S_ISREG(before.st_mode) and before.st_size <= MAX_JSON, "Request exceeds size bound or is not regular")
        # The read itself is bounded even if the file grows after fstat.
        data = stream.read(MAX_JSON + 1)
        after = os.fstat(stream.fileno())
    require(len(data) <= MAX_JSON and (before.st_size, before.st_mtime_ns) == (after.st_size, after.st_mtime_ns),
            "Request grew or changed during read")
    return load_json_bytes(data)


def safe_member(name):
    require(isinstance(name, str) and 0 < len(name) <= 512, "Invalid archive member name")
    require("\\" not in name and ":" not in name and not name.startswith("/"), "Unsafe archive member name")
    parts = name.split("/")
    require(all(p and p not in (".", "..") and not p.endswith((".", " "))
                and not any(ord(c) < 32 for c in p)
                and not re.fullmatch(r"(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", p)
                for p in parts), "Unsafe archive path component")
    require(str(PurePosixPath(name)) == name, "Noncanonical archive path")
    return name


def tar_members(data, maximum=MAX_EXPANDED, max_entries=20000):
    require(isinstance(data, bytes) and len(data) <= MAX_SOURCE, "Source archive input too large")
    files, entries, total = {}, {}, 0
    try:
        # tarfile reads PAX metadata before yielding members. Bound the complete
        # decoded tar first so oversized metadata cannot bypass member limits.
        raw_limit = maximum + min(max_entries * 1024, 16 * 1024 * 1024) + 10240
        if data.startswith(b"\x1f\x8b"):
            with gzip.GzipFile(fileobj=io.BytesIO(data)) as compressed:
                raw = compressed.read(raw_limit + 1)
        else:
            raw = data
        require(len(raw) <= raw_limit, "Decoded tar exceeds payload and metadata bound")
        with tarfile.open(fileobj=io.BytesIO(raw), mode="r:") as archive:
            for index, member in enumerate(archive):
                require(index < max_entries, "Too many archive entries")
                name = safe_member(member.name.rstrip("/") if member.isdir() else member.name)
                key = name.casefold()
                require(key not in entries, "Duplicate or case-colliding archive entry")
                entries[key] = member.isdir()
                require(member.isdir() or member.isfile(), "Links and special archive entries are forbidden")
                require(not member.sparse and integer(member.size, 0, maximum), "Sparse or oversized archive entry")
                total += member.size
                require(total <= maximum, "Expanded source exceeds size bound")
                if member.isfile():
                    stream = archive.extractfile(member)
                    require(stream is not None, "Unreadable archive entry")
                    with stream:
                        contents = stream.read(member.size + 1)
                    require(len(contents) == member.size, "Truncated archive entry")
                    files[name] = contents
            for name in entries:
                parts = name.split("/")
                require(all(entries.get("/".join(parts[:i]), True) for i in range(1, len(parts))), "File used as an archive directory")
            require(len(raw) - archive.offset >= 1024 and not any(raw[archive.offset:]),
                    "Missing tar terminator or unexpected trailing payload")
            return files, archive.pax_headers.get("comment")
    except (tarfile.TarError, OSError, EOFError) as exc:
        raise PackageError(f"Invalid source tar: {exc}") from exc


def verify_file_set(members, records, prefix="", *, exact=True):
    require(isinstance(records, list) and 0 < len(records) <= 20000, "Invalid source file receipt")
    expected = set()
    for record in records:
        exact_keys(record, ("path", "bytes", "sha256"), "source file")
        name = prefix + safe_member(record["path"])
        require(name not in expected and name in members, "Missing or duplicate source input")
        expected.add(name)
        require(integer(record["bytes"]) and hash_string(record["sha256"]), "Invalid source identity")
        require(len(members[name]) == record["bytes"] and digest(members[name]) == record["sha256"], f"Source bytes differ: {name}")
    if exact:
        require({name for name in members if name.startswith(prefix)} == expected, "Source input set differs")


def verify_wrapper(members, receipt):
    exact_keys(receipt, ("schemaVersion", "state", "files"), "bridge source receipt")
    require(type(receipt["schemaVersion"]) is int and receipt["schemaVersion"] == 1 and public_text(receipt["state"]),
            "Invalid bridge source receipt")
    require(isinstance(receipt["files"], list) and all(isinstance(f, dict) for f in receipt["files"])
            and {f.get("path") for f in receipt["files"]} == WRAPPER_REQUIRED, "Wrapper input names differ from maintained bridge")
    verify_file_set(members, receipt["files"], "native/bridge/")


def verify_upstream_file_set(members, records):
    """Normalize only the pinned archive's documented root, never arbitrary roots."""
    require(bool(members) and all(name.startswith(UPSTREAM_PREFIX) for name in members),
            "Upstream archive contains an incorrect or outside-prefix source file")
    normalized = {name[len(UPSTREAM_PREFIX):]: data for name, data in members.items()}
    verify_file_set(normalized, records)
    return normalized


def verify_build_witness(members, witness, qualification):
    if isinstance(witness, dict):
        exact_keys(witness, ("schemaVersion", "state", "files"), "original build witness")
        require(type(witness["schemaVersion"]) is int and witness["schemaVersion"] == 1
                and public_text(witness["state"]), "Invalid original build witness")
        require(qualification["binaryBuildSourceState"] == witness["state"], "Original binary build source state was relabelled")
        records, witness_format = witness["files"], "schema-1-object"
    else:
        records, witness_format = witness, "original-row-array"
    require(isinstance(records, list) and all(isinstance(item, dict) for item in records), "Invalid original build file list")
    names = {item.get("path") for item in records}
    core_inputs = WRAPPER_REQUIRED - {"README.md", ".gitattributes"}
    require(core_inputs <= names <= WRAPPER_REQUIRED, "Build witness omits code inputs or adds unknown files")
    verify_file_set(members, records, "native/bridge/", exact=False)
    return {"format": witness_format, "fileCount": len(records), "releaseOnlyFiles": sorted(WRAPPER_REQUIRED - names)}


def validate_runtime_destinations(records, platform):
    require(platform in ("win-x64", "linux-x64") and isinstance(records, list), "Invalid target platform")
    wanted = WINDOWS_RUNTIME if platform == "win-x64" else {"sed-cpp-bridge"}
    names = [record.get("destination") for record in records if isinstance(record, dict)]
    require(len(names) == len(records) == len(wanted) and set(names) == wanted,
            "Runtime input set must match the fixed platform allowlist exactly")


def runtime_identities(records):
    require(isinstance(records, list) and 0 < len(records) <= 3, "Invalid runtime identities")
    result = []
    for item in records:
        require(isinstance(item, dict) and set(item) in ({"destination", "bytes", "sha256"},
                                                       {"destination", "bytes", "sha256", "path"}), "Invalid runtime identity fields")
        safe_member(item["destination"])
        require(integer(item["bytes"], 1, MAX_RUNTIME) and hash_string(item["sha256"]), "Invalid runtime identity")
        result.append({k: item[k] for k in ("destination", "bytes", "sha256")})
    require(len({x["destination"].casefold() for x in result}) == len(result), "Duplicate runtime identity")
    return sorted(result, key=lambda x: x["destination"])


def validate_qualification(value, request):
    exact_keys(value, ("schemaVersion", "scope", "version", "platform", "sourceCommit", "sourceClean",
                       "sourceArchiveSha256", "binaryBuildSourceState", "sourceAlignment", "upstreamCommit",
                       "upstreamManifestSha256", "runtimeFiles", "environment", "bridgeTests", "clientIntegration",
                       "upstreamCoreBaseline"), "qualification")
    require(type(value["schemaVersion"]) is int and value["schemaVersion"] == 1
            and value["scope"] == "read-only-inspection-preview" and value["sourceClean"] is True,
            "Qualification must bind a clean final source archive and preview scope")
    for key in ("version", "platform", "sourceCommit"):
        require(value[key] == request[key], f"Qualification {key} mismatch")
    require(value["sourceArchiveSha256"] == request["sourceArchive"]["sha256"], "Final source archive mismatch")
    require(value["upstreamCommit"] == UPSTREAM_COMMIT and value["upstreamManifestSha256"] == LOCK_SHA256,
            "Qualification upstream identity mismatch")
    require(public_text(value["binaryBuildSourceState"]) and public_text(value["environment"]), "Missing or private qualification description")
    alignment = value["sourceAlignment"]
    exact_keys(alignment, ("method", "bridgeSourceInputsSha256", "buildSourceWitnessSha256", "upstreamArchiveSha256"), "source alignment")
    require(alignment == {"method": "exact-wrapper-input-set-and-upstream-lock",
                          "bridgeSourceInputsSha256": request["bridgeSourceInputs"]["sha256"],
                          "buildSourceWitnessSha256": request["buildSourceWitness"]["sha256"],
                          "upstreamArchiveSha256": request["upstreamArchive"]["sha256"]}, "Source alignment mismatch")
    require(isinstance(value["runtimeFiles"], list), "Invalid qualification runtime identities")
    for identity in value["runtimeFiles"]:
        exact_keys(identity, ("destination", "bytes", "sha256"), "public qualified runtime")
    require(runtime_identities(value["runtimeFiles"]) == runtime_identities(request["runtimeFiles"]), "Qualified runtime bytes differ")
    tests = value["bridgeTests"]
    exact_keys(tests, ("status", "protocolPassed", "sourceLockPassed", "sourceLockSkipped", "jsonPassed"), "bridge tests")
    require(tests["status"] == "passed" and all(integer(tests[x]) for x in tests if x != "status")
            and tests["protocolPassed"] == 12 and tests["jsonPassed"] == 1
            and (tests["sourceLockPassed"], tests["sourceLockSkipped"]) in
            ([(6, 0)] if value["platform"] == "linux-x64" else [(5, 1), (6, 0)]), "Required bridge tests did not pass")
    integration = value["clientIntegration"]
    exact_keys(integration, ("passed", "failed"), "client integration")
    require(type(integration["passed"]) is int and type(integration["failed"]) is int
            and integration == {"passed": 8, "failed": 0}, "Required real client integration did not pass")
    baseline = value["upstreamCoreBaseline"]
    exact_keys(baseline, ("total", "passed", "failed", "skipped", "failures"), "upstream baseline")
    require(all(integer(baseline[k]) for k in ("total", "passed", "failed", "skipped"))
            and baseline["total"] == 51 and baseline["skipped"] == 0
            and baseline["passed"] + baseline["failed"] == 51, "Invalid core baseline counts")
    require((baseline["passed"], baseline["failed"]) in
            ([(51, 0)] if value["platform"] == "linux-x64" else [(50, 1), (51, 0)]), "Unexpected core baseline")
    require(isinstance(baseline["failures"], list) and len(baseline["failures"]) == baseline["failed"], "Every core failure must be disclosed")
    for failure in baseline["failures"]:
        exact_keys(failure, ("test", "detail"), "baseline failure")
        require(failure["test"] == "sed_core_tests" and public_text(failure["detail"]), "Invalid baseline failure")


def deterministic_zip(files):
    stream = io.BytesIO()
    write_zip(stream, files)
    return stream.getvalue()


def write_zip(stream, files):
    require(len(files) <= 100, "Too many package entries")
    seen = set()
    with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_STORED, allowZip64=False) as archive:
        for name, (data, executable) in sorted(files.items()):
            safe_member(name)
            require(name.casefold() not in seen and isinstance(data, bytes), "Duplicate or invalid package entry")
            seen.add(name.casefold())
            info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | (0o755 if executable else 0o644)) << 16
            info.compress_type = zipfile.ZIP_STORED
            archive.writestr(info, data)


def runtime_requirements(record, worker_hash):
    """Project reviewed ELF metadata to a small public receipt without local paths."""
    source = load_json_bytes(read_artifact(record, MAX_JSON))
    require(source.get("workerSha256") == worker_hash and source.get("workerUnchanged") is True
            and source.get("elfClass") == "ELF64 little-endian" and source.get("machine") == "x86-64 (EM_X86_64=62)"
            and source.get("elfInterpreter") == "/lib64/ld-linux-x86-64.so.2" and source.get("runpathOrRpath") == [],
            "Invalid Linux runtime metadata")
    libraries, versions = source.get("directNeededLibraries"), source.get("requiredSymbolVersionsByLibrary")
    require(isinstance(libraries, list) and len(libraries) == 4
            and set(libraries) == {"libstdc++.so.6", "libm.so.6", "libgcc_s.so.1", "libc.so.6"}
            and isinstance(versions, dict) and set(versions) == set(libraries), "Unexpected Linux runtime libraries")
    for values in versions.values():
        require(isinstance(values, list) and 0 < len(values) <= 128
                and all(isinstance(v, str) and re.fullmatch(r"(?:GLIBC|GLIBCXX|CXXABI|GCC)_[0-9.]+", v) for v in values),
                "Invalid runtime symbol versions")
    return {"schemaVersion": 1, "workerSha256": worker_hash, "evidenceSha256": record["sha256"],
            "verification": "static-ELF-metadata", "elfInterpreter": source["elfInterpreter"],
            "directNeededLibraries": sorted(libraries),
            "requiredSymbolVersionsByLibrary": {key: sorted(value) for key, value in versions.items()}, "rpathOrRunpath": [],
            "limitation": "The target runtime must provide all listed ABI symbols. These direct requirements do not establish a distribution support floor or transitive runtime compatibility. Host dynamic-loader resolution was not verified."}


def assemble(request):
    exact_keys(request, ("schemaVersion", "version", "platform", "sourceCommit", "sourceArchive", "upstreamArchive",
                         "bridgeSourceInputs", "buildSourceWitness", "qualification", "runtimeFiles", "dependencyNotices", "runtimeRequirements"), "request")
    require(type(request["schemaVersion"]) is int and request["schemaVersion"] == 1, "Unsupported request version")
    require(isinstance(request["version"], str) and re.fullmatch(r"[0-9][0-9A-Za-z.+-]{0,79}", request["version"]), "Invalid release version")
    require(isinstance(request["sourceCommit"], str) and re.fullmatch(r"[0-9a-f]{40}", request["sourceCommit"]), "Invalid source commit")
    validate_runtime_destinations(request["runtimeFiles"], request["platform"])
    for key in ("sourceArchive", "upstreamArchive", "bridgeSourceInputs", "buildSourceWitness", "qualification"):
        exact_keys(request[key], ("path", "bytes", "sha256"), key)
    source_data = read_artifact(request["sourceArchive"], MAX_SOURCE)
    members, commit = tar_members(source_data)
    require(commit == request["sourceCommit"], "Git source archive commit metadata differs")
    require("LICENSE" in members and "NOTICE.md" in members, "Final source lacks project license or notices")
    receipt = load_json_bytes(read_artifact(request["bridgeSourceInputs"], MAX_JSON))
    verify_wrapper(members, receipt)
    lock_data = members["native/bridge/upstream.lock.json"]
    require(digest(lock_data) == LOCK_SHA256, "Upstream lock is not the pinned manifest")
    lock = load_json_bytes(lock_data)
    require(lock.get("upstreamCommit") == UPSTREAM_COMMIT and len(lock.get("files", [])) == 213, "Invalid pinned upstream lock")
    require(request["upstreamArchive"]["sha256"] == UPSTREAM_SHA256
            and request["upstreamArchive"]["bytes"] == UPSTREAM_BYTES, "Corresponding upstream archive is not pinned")
    upstream_data = read_artifact(request["upstreamArchive"], UPSTREAM_BYTES)
    upstream, upstream_commit = tar_members(upstream_data, maximum=16 * 1024 * 1024, max_entries=1024)
    require(upstream_commit == UPSTREAM_COMMIT, "Upstream Git archive commit metadata differs")
    upstream = verify_upstream_file_set(upstream, lock["files"])
    qualification = load_json_bytes(read_artifact(request["qualification"], MAX_JSON))
    validate_qualification(qualification, request)
    witness_data = read_artifact(request["buildSourceWitness"], MAX_JSON)
    witness = verify_build_witness(members, load_json_bytes(witness_data), qualification)
    files = {}
    for record in request["runtimeFiles"]:
        exact_keys(record, ("path", "bytes", "sha256", "destination"), "runtime file")
        files[record["destination"]] = (read_artifact(record, MAX_RUNTIME), request["platform"] == "linux-x64")
    worker = "sed-cpp-bridge.exe" if request["platform"] == "win-x64" else "sed-cpp-bridge"
    worker_hash = digest(files[worker][0])
    notices = request["dependencyNotices"]
    require(isinstance(notices, list) and len(notices) == (1 if request["platform"] == "win-x64" else 0), "Unexpected dependency notices")
    public_notices = []
    for record in notices:
        exact_keys(record, ("path", "bytes", "sha256", "destination", "covers"), "dependency notice")
        require(record["destination"] == "licenses/runtime/llvm-mingw-LICENSE.txt"
                and record["covers"] == ["libc++.dll", "libunwind.dll"], "Runtime license coverage mismatch")
        require(record["sha256"] == "8d85c1057d742e597985c7d4e6320b015a9139385cff4cbae06ffc0ebe89afee", "Unreviewed runtime license")
        files[record["destination"]] = (read_artifact(record, MAX_JSON), False)
        public_notices.append({k: record[k] for k in ("destination", "bytes", "sha256", "covers")})
    if request["platform"] == "linux-x64":
        exact_keys(request["runtimeRequirements"], ("path", "bytes", "sha256"), "runtime requirements")
        files["provenance/runtime-requirements.json"] = (canonical_json(runtime_requirements(request["runtimeRequirements"], worker_hash)), False)
    else:
        require(request["runtimeRequirements"] is None, "Windows runtimeRequirements must be null")
    suffix = ".tar.gz" if source_data.startswith(b"\x1f\x8b") else ".tar"
    files.update({
        "worker-config.json": (canonical_json({"schemaVersion": 1, "executableSha256": worker_hash,
                                               "sourceManifestSha256": LOCK_SHA256, "timeoutSeconds": 20}), False),
        "source/sed-mots-source" + suffix: (source_data, False),
        "source/SED-upstream-core-b822559.tar": (upstream_data, False),
        "licenses/SED-MotS-LICENSE.txt": (members["LICENSE"], False),
        "licenses/SED-MotS-NOTICE.md": (members["NOTICE.md"], False),
        "licenses/upstream-LICENSE.txt": (upstream["LICENSE"], False),
        "licenses/upstream-THIRD_PARTY_NOTICES.md": (upstream["THIRD_PARTY_NOTICES.md"], False),
        "provenance/bridge-source-inputs.json": (canonical_json(receipt), False),
        "provenance/build-source-witness.json": (witness_data, False),
        "provenance/qualification.json": (canonical_json(qualification), False),
        "provenance/upstream.lock.json": (lock_data, False),
        "BUILDING.md": (members["native/bridge/README.md"], False),
        "protocol-v1.md": (members["native/bridge/protocol-v1.md"], False),
        "README.md": (readme(request, qualification, witness).encode(), False),
    })
    manifest = {"schemaVersion": 1, "component": "sed-cpp-bridge", "scope": "read-only-inspection-preview",
                "version": request["version"], "platform": request["platform"], "sourceCommit": request["sourceCommit"],
                "sourceClean": True, "binaryBuildSourceState": qualification["binaryBuildSourceState"],
                "sourceAlignment": qualification["sourceAlignment"], "buildSourceWitness": witness,
                "upstreamCommit": UPSTREAM_COMMIT, "upstreamManifestSha256": LOCK_SHA256,
                "sourceArchiveSha256": request["sourceArchive"]["sha256"], "qualificationInputSha256": request["qualification"]["sha256"],
                "runtimeNotices": public_notices, "files": [
                    {"path": name, "bytes": len(data), "sha256": digest(data), "executable": executable}
                    for name, (data, executable) in sorted(files.items())]}
    files["provenance/package-manifest.json"] = (canonical_json(manifest), False)
    return files


def readme(request, qualification, witness):
    baseline = qualification["upstreamCoreBaseline"]
    tests = qualification["bridgeTests"]
    failures = "\n".join(f"- {x['test']}: {x['detail']}" for x in baseline["failures"]) or "No failures were recorded in this baseline."
    return f"""# SED-MotS optional native inspection preview {request['version']}

Target: {request['platform']}. Provides read-only inspect-jkl and inspect-native-sed observations. The .NET editor remains the authoritative document owner. This does not qualify native editing, writing, GUI replacement, gameplay compatibility or multiplayer behavior.

Keep worker-config.json beside the fixed-name worker and, on Windows, both supplied DLLs. With the matching main SED-MotS release, run `sed-agent native-inspect INPUT jk|mots|ijim jkl|native-sed ABSOLUTE_CONFIG_PATH`. Give the absolute path to this component's worker-config.json. On Linux ensure executable permission (some ZIP extractors do not preserve it). The runtime must provide every ABI symbol listed in provenance/runtime-requirements.json and compatible transitive dependencies; no general distribution support claim is made.

The worker has process and I/O bounds, not a security sandbox or operating-system memory limit. The host confirms exit of its direct worker child. Resource validation, COG hydration and UV normalization are not performed. Native observations do not become authoritative editor documents.

Recorded bridge tests: 12 protocol passed, {tests['sourceLockPassed']} source-lock passed ({tests['sourceLockSkipped']} skipped), JSON passed. Actual .NET client-to-worker integration: 8/8. Full upstream core baseline: {baseline['passed']}/{baseline['total']} passed, {baseline['failed']} failed, {baseline['skipped']} skipped.
{failures}

This remains an inspection preview when a platform's core baseline passes. Qualification is a reviewed operator attestation; hashes provide identity and consistency, not a signature or independent proof of test execution.

Final source commit: {request['sourceCommit']}. Final source archive was attested clean. Original binary build source state: {qualification['binaryBuildSourceState']}. The original build witness contains {witness['fileCount']} matched files; its format is {witness['format']}. Files covered only by the separate release inventory: {', '.join(witness['releaseOnlyFiles']) or 'none'}. The original witness bytes are preserved in provenance/build-source-witness.json. Every code, protocol, test and lock input must be witnessed; only README.md and .gitattributes may be absent from the original witness.

The separate complete eleven-file release wrapper set was compared byte-for-byte to the final Git source archive, and all pinned upstream inputs to the corresponding upstream archive. This alignment does not relabel the original build as clean, enlarge the original build witness or establish reproducible binary output.

Complete corresponding source is under source/; project, upstream and applicable runtime notices are under licenses/. BUILDING.md and protocol-v1.md come from the exact matched source archive. No game assets are included. provenance/package-manifest.json identifies every payload member except itself.
"""


def publish(files, output):
    """Exclusive creation, portable to exFAT. Never replace another package."""
    path = checked_path(output, output=True)
    require(path.suffix == ".zip" and not path.exists(), "Output must be a new .zip file")
    # A crash can leave a partial file. Successful command completion is required;
    # this intentionally does not pretend to provide atomic publication on exFAT.
    with path.open("x+b") as stream:
        try:
            write_zip(stream, files)
            stream.flush()
            os.fsync(stream.fileno())
            owned = os.fstat(stream.fileno())
            value = hashlib.sha256()
            stream.seek(0)
            remaining = owned.st_size
            while remaining:
                chunk = stream.read(min(1024 * 1024, remaining))
                require(bool(chunk), "Owned output became truncated while hashing")
                value.update(chunk)
                remaining -= len(chunk)
            require(stream.read(1) == b"", "Owned output grew while hashing")
            after = os.fstat(stream.fileno())
            require((owned.st_size, owned.st_mtime_ns) == (after.st_size, after.st_mtime_ns), "Owned output changed while hashing")
        except (OSError, ValueError, zipfile.LargeZipFile) as exc:
            # Do not unlink by path: another actor could replace that name after
            # our handle closes. Preserve evidence; do not claim a valid ZIP.
            raise PackageError(f"Package write failed; possibly partial output preserved: {path}: {exc}") from exc
    return {"bytes": owned.st_size, "sha256": value.hexdigest()}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        request = read_request(args.request)
        result = publish(assemble(request), args.output)
        print(json.dumps({"status": "packaged", **result}, sort_keys=True))
        return 0
    except (PackageError, OSError, KeyError, TypeError, zipfile.BadZipFile) as exc:
        print(f"Native component packaging refused: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())

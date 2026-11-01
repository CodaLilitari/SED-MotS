#!/usr/bin/env python3
"""Verify a pinned external source tree without Git, fetching or source mutation."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import stat
import sys

COMMIT = "b82255998fb733c8e8c87104eb7c17f27b48376f"


class VerificationError(ValueError):
    pass


def safe_relative(value):
    if not isinstance(value, str) or not value or any(c in value for c in "\\:\0\r\n"):
        raise VerificationError("Invalid manifest path")
    relative = PurePosixPath(value)
    if relative.is_absolute() or any(part in ("", ".", "..") for part in value.rstrip("/").split("/")):
        raise VerificationError("Unsafe manifest path")
    return relative


def reject_links(path):
    for ancestor in (path, *path.parents):
        try:
            info = ancestor.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise VerificationError("Source paths must not pass through links/reparse points")


def digest_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(128 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def verify(root, lock):
    root = Path(root).absolute()
    reject_links(root)
    if not root.is_dir():
        raise VerificationError("Upstream source directory is missing")
    lock_bytes = Path(lock).read_bytes()
    if len(lock_bytes) > 4 * 1024 * 1024:
        raise VerificationError("Source lock exceeds 4 MiB")
    manifest = json.loads(lock_bytes)
    if manifest.get("schemaVersion") != 1 or manifest.get("upstreamCommit") != COMMIT:
        raise VerificationError("Unsupported source lock schema or upstream commit")
    expected = {}
    case_names = set()
    for item in manifest.get("files", []):
        name = safe_relative(item["path"]).as_posix()
        if name in expected or name.casefold() in case_names:
            raise VerificationError("Duplicate/case-colliding source lock entry")
        if (type(item.get("bytes")) is not int or item["bytes"] < 0
                or not isinstance(item.get("sha256"), str)
                or len(item["sha256"]) != 64
                or any(c not in "0123456789abcdef" for c in item["sha256"])):
            raise VerificationError("Invalid source lock size/hash")
        expected[name] = item
        case_names.add(name.casefold())
    if not expected or len(expected) > 4096:
        raise VerificationError("Source lock must contain 1..4096 inputs")
    actual = set()
    coverage = manifest.get("coverage", [])
    if not isinstance(coverage, list) or not coverage:
        raise VerificationError("Source lock has no coverage")
    for entry in coverage:
        relative = safe_relative(entry)
        path = root / relative
        reject_links(path)
        if entry.endswith("/"):
            if not path.is_dir():
                raise VerificationError("Missing covered source directory")
            for directory, directories, files in os.walk(path, followlinks=False):
                for name in directories:
                    reject_links(Path(directory) / name)
                for name in files:
                    child = Path(directory) / name
                    reject_links(child)
                    if not child.is_file():
                        raise VerificationError("Non-file source input")
                    actual.add(child.relative_to(root).as_posix())
        else:
            if not path.is_file():
                raise VerificationError("Missing covered source file")
            actual.add(relative.as_posix())
    if actual != expected.keys():
        raise VerificationError("Covered source inputs differ from lock (missing or extra file)")
    for name, item in expected.items():
        path = root / name
        reject_links(path)
        if path.stat().st_size != item["bytes"] or digest_file(path) != item["sha256"]:
            raise VerificationError("Source input differs from pinned bytes: " + name)
    return {
        "schemaVersion": 1, "upstreamCommit": COMMIT,
        "sourceManifestSha256": hashlib.sha256(lock_bytes).hexdigest(),
        "verifiedFiles": len(expected),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--lock", type=Path, required=True)
    parser.add_argument("--receipt", type=Path)
    args = parser.parse_args()
    try:
        result = verify(args.root, args.lock)
        text = json.dumps(result, sort_keys=True) + "\n"
        if args.receipt:
            reject_links(args.receipt.absolute())
            if args.receipt.resolve().is_relative_to(args.root.resolve()):
                raise VerificationError("Verification receipt must be outside upstream source")
            args.receipt.write_text(text, encoding="utf-8")
        sys.stdout.write(text)
        return 0
    except (VerificationError, OSError, ValueError, KeyError, TypeError) as error:
        sys.stderr.write(("Upstream verification failed: " + str(error))[:1024] + "\n")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())

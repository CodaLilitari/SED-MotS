"""Original tiny fixtures only; no Git, compiler, real runtime or real source archive."""
import copy
import gzip
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest import mock
import zipfile

SPEC = importlib.util.spec_from_file_location('package_native_component', Path(__file__).resolve().parents[1] / 'package_native_component.py')
pack = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(pack)
COMMIT = '0123456789abcdef0123456789abcdef01234567'
UPSTREAM = 'b82255998fb733c8e8c87104eb7c17f27b48376f'
LOCK_HASH = 'd1f1473d7644e65dc3ec95b2cbdb0743732ef9fd8e45d010a064b7d45ddaf098'
UPSTREAM_HASH = 'ef4ddb3ef0cee00fd7ddf8da17ff7a6fb058f11c5337b6e3ede366b8b66723f2'
UPSTREAM_PREFIX = 'SED-core-b822559/'
HASH = 'a' * 64


def tar_bytes(entries, links=None, commit=COMMIT):
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode='w', format=tarfile.PAX_FORMAT, pax_headers={'comment': commit}) as archive:
        for name, data in entries:
            info = tarfile.TarInfo(name)
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))
        for name, target in links or []:
            info = tarfile.TarInfo(name)
            info.type = tarfile.SYMTYPE
            info.linkname = target
            archive.addfile(info)
    return stream.getvalue()


class NativeComponentPackageTests(unittest.TestCase):
    def setUp(self):
        parent = os.environ.get('SED_NATIVE_PACKAGE_TEST_TEMP')
        self.temp = tempfile.TemporaryDirectory(prefix='sed-native-package-', dir=parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def test_artifact_requires_exact_captured_bytes_and_hash(self):
        path = self.root / 'input.bin'
        path.write_bytes(b'original fixture')
        record = {'path': str(path), 'bytes': 16, 'sha256': hashlib.sha256(b'original fixture').hexdigest()}
        self.assertEqual(pack.read_artifact(record, 100), b'original fixture')
        path.write_bytes(b'changed fixture!')
        with self.assertRaises(pack.PackageError):
            pack.read_artifact(record, 100)

    def test_size_bound_checked_before_read(self):
        path = self.root / 'large.bin'
        path.write_bytes(b'x' * 101)
        with self.assertRaises(pack.PackageError):
            pack.read_artifact({'path': str(path), 'bytes': 101, 'sha256': HASH}, 100)

    def test_duplicate_json_keys_are_rejected(self):
        for data in (b'{"schemaVersion":1,"schemaVersion":1}', b'{"x":{"a":1,"a":2}}', b'{}{}'):
            with self.subTest(data=data), self.assertRaises(pack.PackageError):
                pack.load_json_bytes(data)

    def test_tar_is_read_without_extracting_and_retains_commit_metadata(self):
        result, commit = pack.tar_members(tar_bytes([('LICENSE', b'original license fixture'), ('native/bridge/src/main.cpp', b'int main(){}')]))
        self.assertEqual(commit, COMMIT)
        self.assertEqual(result['native/bridge/src/main.cpp'], b'int main(){}')
        self.assertEqual(list(self.root.iterdir()), [])

    def test_tar_traversal_case_collisions_links_and_expansion_are_refused(self):
        cases = [tar_bytes([('../outside', b'x')]), tar_bytes([('A', b'x'), ('a', b'y')]),
                 tar_bytes([], [('link', 'elsewhere')]), tar_bytes([('a', b'x' * 101)])]
        for data in cases:
            with self.subTest(length=len(data)), self.assertRaises(pack.PackageError):
                pack.tar_members(data, maximum=100)

    def test_wrapper_matches_exact_input_set_and_detects_newline_transformation(self):
        names = ['.gitattributes', 'CMakeLists.txt', 'README.md', 'protocol-v1.md', 'upstream.lock.json',
                 'verify_upstream.py', 'src/main.cpp', 'src/json.hpp', 'tests/test_worker.py',
                 'tests/test_upstream_lock.py', 'tests/json_tests.cpp']
        members = {'native/bridge/' + name: (name + '\n').encode() for name in names}
        receipt = {'schemaVersion': 1, 'state': 'dirty working source; original build state preserved', 'files':
                   [{'path': name, 'bytes': len(members['native/bridge/' + name]),
                     'sha256': hashlib.sha256(members['native/bridge/' + name]).hexdigest()} for name in names]}
        pack.verify_wrapper(members, receipt)
        changed = dict(members)
        changed['native/bridge/src/main.cpp'] = b'src/main.cpp\r\n'
        with self.assertRaises(pack.PackageError):
            pack.verify_wrapper(changed, receipt)
        changed = dict(members)
        changed['native/bridge/unrecorded.cpp'] = b'new source'
        with self.assertRaises(pack.PackageError):
            pack.verify_wrapper(changed, receipt)

    def test_runtime_allowlist_cannot_admit_negative_configs_or_extra_executables(self):
        accepted = [{'destination': 'sed-cpp-bridge.exe'}, {'destination': 'libc++.dll'}, {'destination': 'libunwind.dll'}]
        pack.validate_runtime_destinations(accepted, 'win-x64')
        for name in ('worker-config-client-duplicate.json', 'other.exe', '../lib.dll', 'CON.dll', 'sub/lib.dll', 'LIBC++.dll'):
            with self.subTest(name=name), self.assertRaises(pack.PackageError):
                pack.validate_runtime_destinations(accepted + [{'destination': name}], 'win-x64')
        with self.assertRaises(pack.PackageError):
            pack.validate_runtime_destinations([{'destination': 'sed-cpp-bridge.exe'}], 'linux-x64')

    def qualification(self, platform='win-x64'):
        runtime = [{'destination': 'sed-cpp-bridge.exe' if platform == 'win-x64' else 'sed-cpp-bridge', 'bytes': 128, 'sha256': HASH}]
        request = {'version': '0.4.0-mots.18-consolidated.1', 'platform': platform, 'sourceCommit': COMMIT,
                   'sourceArchive': {'sha256': HASH}, 'bridgeSourceInputs': {'sha256': HASH},
                   'buildSourceWitness': {'sha256': HASH},
                   'upstreamArchive': {'sha256': UPSTREAM_HASH}, 'runtimeFiles': runtime}
        value = {'schemaVersion': 1, 'scope': 'read-only-inspection-preview', 'version': request['version'],
                 'platform': platform, 'sourceCommit': COMMIT, 'sourceClean': True, 'sourceArchiveSha256': HASH,
                 'binaryBuildSourceState': 'dirty working source; later compared to final archive',
                 'sourceAlignment': {'method': 'exact-wrapper-input-set-and-upstream-lock', 'bridgeSourceInputsSha256': HASH,
                                     'buildSourceWitnessSha256': HASH, 'upstreamArchiveSha256': UPSTREAM_HASH},
                 'upstreamCommit': UPSTREAM, 'upstreamManifestSha256': LOCK_HASH, 'runtimeFiles': runtime,
                 'environment': 'Original synthetic qualification fixture',
                 'bridgeTests': {'status': 'passed', 'protocolPassed': 12, 'sourceLockPassed': 5 if platform == 'win-x64' else 6,
                                 'sourceLockSkipped': 1 if platform == 'win-x64' else 0, 'jsonPassed': 1},
                 'clientIntegration': {'passed': 8, 'failed': 0},
                 'upstreamCoreBaseline': {'total': 51, 'passed': 50 if platform == 'win-x64' else 51,
                                          'failed': 1 if platform == 'win-x64' else 0, 'skipped': 0,
                                          'failures': [{'test': 'sed_core_tests', 'detail': 'Recorded geometry ordering baseline failure.'}] if platform == 'win-x64' else []}}
        return request, value

    def test_preview_accepts_disclosed_windows_failure_and_future_truthful_pass(self):
        request, value = self.qualification()
        pack.validate_qualification(value, request)
        future = copy.deepcopy(value)
        future['upstreamCoreBaseline'].update(passed=51, failed=0, failures=[])
        pack.validate_qualification(future, request)
        linux_request, linux_value = self.qualification('linux-x64')
        pack.validate_qualification(linux_value, linux_request)

    def test_dirty_final_source_or_relabelled_unbound_qualification_is_refused(self):
        request, value = self.qualification()
        for change in (lambda v: v.update(sourceClean=False), lambda v: v.update(sourceArchiveSha256='b' * 64),
                       lambda v: v['upstreamCoreBaseline'].update(failures=[]),
                       lambda v: v['clientIntegration'].update(failed=1),
                       lambda v: v['sourceAlignment'].update(buildSourceWitnessSha256='d' * 64),
                       lambda v: v['sourceAlignment'].update(bridgeSourceInputsSha256='c' * 64)):
            bad = copy.deepcopy(value)
            change(bad)
            with self.assertRaises(pack.PackageError):
                pack.validate_qualification(bad, request)

    def test_zip_is_deterministic_and_preserves_linux_executable_mode(self):
        files = {'README.md': (b'preview\n', False), 'sed-cpp-bridge': (b'original fixture', True)}
        first = pack.deterministic_zip(files)
        second = pack.deterministic_zip(dict(reversed(list(files.items()))))
        self.assertEqual(first, second)
        with zipfile.ZipFile(io.BytesIO(first)) as archive:
            self.assertEqual(archive.namelist(), ['README.md', 'sed-cpp-bridge'])
            self.assertEqual(archive.getinfo('sed-cpp-bridge').external_attr >> 16 & 0o777, 0o755)
            self.assertEqual(archive.getinfo('README.md').date_time, (1980, 1, 1, 0, 0, 0))

    def artifact(self, name, data):
        path = self.root / name
        path.write_bytes(data)
        return {'path': str(path), 'bytes': len(data), 'sha256': pack.digest(data)}

    def original_complete_fixture(self):
        # Synthetic source, never upstream code or runtime. Pins are changed only
        # inside this in-process test context; the command has no override option.
        upstream = {'LICENSE': b'Original synthetic upstream license\n',
                    'THIRD_PARTY_NOTICES.md': b'Original synthetic upstream notice\n',
                    'CMakeLists.txt': b'# Original fixture\n'}
        upstream.update({f'src/core/fixture{i}.hpp': b'// Original fixture\n' for i in range(210)})
        lock = {'schemaVersion': 1, 'upstreamCommit': UPSTREAM,
                'files': [{'path': name, 'bytes': len(data), 'sha256': pack.digest(data)}
                          for name, data in sorted(upstream.items())]}
        lock_data = pack.canonical_json(lock)
        upstream_data = tar_bytes([(UPSTREAM_PREFIX + name, data) for name, data in sorted(upstream.items())], commit=UPSTREAM)
        source = {'native/bridge/' + name: ('Original fixture: ' + name + '\n').encode()
                  for name in pack.WRAPPER_REQUIRED}
        source.update({'LICENSE': b'Original project license\n', 'NOTICE.md': b'Original project notices\n'})
        source['native/bridge/upstream.lock.json'] = lock_data
        receipt = {'schemaVersion': 1, 'state': 'dirty working source; synthetic fixture',
                   'files': [{'path': name, 'bytes': len(source['native/bridge/' + name]),
                              'sha256': pack.digest(source['native/bridge/' + name])} for name in sorted(pack.WRAPPER_REQUIRED)]}
        request, qualification = self.qualification('linux-x64')
        request.update(schemaVersion=1, sourceArchive=self.artifact('source.tar', tar_bytes(sorted(source.items()))),
                       upstreamArchive=self.artifact('upstream.tar', upstream_data),
                       bridgeSourceInputs=self.artifact('bridge-inputs.json', pack.canonical_json(receipt)),
                       dependencyNotices=[])
        request['buildSourceWitness'] = dict(request['bridgeSourceInputs'])
        worker = {**self.artifact('worker', b'Original non-executable synthetic worker bytes'), 'destination': 'sed-cpp-bridge'}
        request['runtimeFiles'] = [worker]
        runtime = {'workerSha256': worker['sha256'], 'workerUnchanged': True, 'elfClass': 'ELF64 little-endian',
                   'machine': 'x86-64 (EM_X86_64=62)', 'elfInterpreter': '/lib64/ld-linux-x86-64.so.2', 'runpathOrRpath': [],
                   'directNeededLibraries': ['libstdc++.so.6', 'libm.so.6', 'libgcc_s.so.1', 'libc.so.6'],
                   'requiredSymbolVersionsByLibrary': {'libstdc++.so.6': ['GLIBCXX_3.4.32', 'CXXABI_1.3.15'],
                                                       'libm.so.6': ['GLIBC_2.2.5'], 'libc.so.6': ['GLIBC_2.34'],
                                                       'libgcc_s.so.1': ['GCC_3.0']},
                   'workerPath': str(self.root / 'private-worker-path'), 'readerInvocation': ['private-local-log']}
        request['runtimeRequirements'] = self.artifact('requirements.json', pack.canonical_json(runtime))
        qualification.update(sourceArchiveSha256=request['sourceArchive']['sha256'],
                             binaryBuildSourceState=receipt['state'], upstreamManifestSha256=pack.digest(lock_data),
                             runtimeFiles=pack.runtime_identities([worker]))
        qualification['sourceAlignment'].update(bridgeSourceInputsSha256=request['bridgeSourceInputs']['sha256'],
                                                 buildSourceWitnessSha256=request['buildSourceWitness']['sha256'],
                                                 upstreamArchiveSha256=request['upstreamArchive']['sha256'])
        request['qualification'] = self.artifact('qualification.json', pack.canonical_json(qualification))
        patcher = mock.patch.multiple(pack, LOCK_SHA256=pack.digest(lock_data), UPSTREAM_SHA256=pack.digest(upstream_data),
                                      UPSTREAM_BYTES=len(upstream_data))
        return request, qualification, patcher

    def test_complete_original_fixture_binds_source_and_sanitizes_public_provenance(self):
        request, qualification, pins = self.original_complete_fixture()
        with pins:
            files = pack.assemble(request)
            first = pack.deterministic_zip(files)
            second = pack.deterministic_zip(pack.assemble(request))
        self.assertEqual(first, second)
        config = json.loads(files['worker-config.json'][0])
        self.assertEqual(config['executableSha256'], request['runtimeFiles'][0]['sha256'])
        self.assertEqual(config['sourceManifestSha256'], qualification['upstreamManifestSha256'])
        manifest = json.loads(files['provenance/package-manifest.json'][0])
        self.assertEqual(len(manifest['files']), len(files) - 1)
        for item in manifest['files']:
            self.assertEqual(pack.digest(files[item['path']][0]), item['sha256'])
        public = b'\n'.join(data for name, (data, _) in files.items() if name.startswith('provenance/'))
        self.assertNotIn(str(self.root).encode(), public)
        self.assertNotIn(b'private-worker-path', public)
        self.assertEqual(json.loads(files['provenance/qualification.json'][0])['binaryBuildSourceState'], qualification['binaryBuildSourceState'])

    def test_complete_fixture_refuses_changed_binary_and_relabelled_build_state(self):
        request, qualification, pins = self.original_complete_fixture()
        worker = Path(request['runtimeFiles'][0]['path'])
        original = worker.read_bytes()
        worker.write_bytes(b'X' * len(original))
        with pins, self.assertRaises(pack.PackageError):
            pack.assemble(request)
        worker.write_bytes(original)
        qualification['binaryBuildSourceState'] = 'clean build invented after the fact'
        request['qualification'] = self.artifact('qualification.json', pack.canonical_json(qualification))
        with pins, self.assertRaises(pack.PackageError):
            pack.assemble(request)

    def test_unqualified_production_pins_cannot_accept_synthetic_source(self):
        request, _, _ = self.original_complete_fixture()
        with self.assertRaises(pack.PackageError):
            pack.assemble(request)

    def test_exact_pinned_upstream_prefix_is_normalized_before_file_and_notice_lookup(self):
        source = {'LICENSE': b'original fixture license', 'src/core/fixture.cpp': b'original fixture source'}
        rows = [{'path': name, 'bytes': len(data), 'sha256': pack.digest(data)} for name, data in source.items()]
        raw = tar_bytes([(UPSTREAM_PREFIX + name, data) for name, data in source.items()], commit=UPSTREAM)
        members, commit = pack.tar_members(raw)
        self.assertEqual(commit, UPSTREAM)
        self.assertEqual(pack.verify_upstream_file_set(members, rows), source)
        self.assertIn(UPSTREAM_PREFIX + 'LICENSE', members)

    def test_wrong_missing_or_mixed_upstream_prefix_is_refused(self):
        data = b'original fixture'
        rows = [{'path': 'LICENSE', 'bytes': len(data), 'sha256': pack.digest(data)}]
        for members in ({'LICENSE': data}, {'wrong-root/LICENSE': data},
                        {'sed-core-b822559/LICENSE': data}, {'SED-core-b822559-extra/LICENSE': data},
                        {UPSTREAM_PREFIX + 'LICENSE': data, 'outside.cpp': data},
                        {UPSTREAM_PREFIX + 'LICENSE': data, UPSTREAM_PREFIX + 'extra.cpp': data}):
            with self.subTest(members=list(members)), self.assertRaises(pack.PackageError):
                pack.verify_upstream_file_set(members, rows)

    def test_publication_preserves_existing_output_and_refuses_git_ancestor(self):
        output = self.root / 'component.zip'
        files = {'original.txt': (b'original tiny fixture', False)}
        result = pack.publish(files, output)
        self.assertEqual(result['sha256'], pack.digest(output.read_bytes()))
        old = output.read_bytes()
        with self.assertRaises(pack.PackageError):
            pack.publish({'replacement.txt': (b'unwanted replacement', False)}, output)
        self.assertEqual(output.read_bytes(), old)
        (self.root / '.git').mkdir()
        with self.assertRaises(pack.PackageError):
            pack.publish(files, self.root / 'forbidden.zip')
        self.assertFalse((self.root / 'forbidden.zip').exists())

    def test_publication_hashes_owned_handle_without_reopening_path(self):
        output = self.root / 'owned.zip'
        expected = pack.deterministic_zip({'original.txt': (b'original fixture', False)})
        real_open = pack.Path.open
        modes = []
        def observed_open(path, mode='r', *args, **kwargs):
            if path == output:
                modes.append(mode)
                if mode != 'x+b':
                    # Model a different file becoming visible at a later reopen.
                    return io.BytesIO(b'foreign replacement')
            return real_open(path, mode, *args, **kwargs)
        with mock.patch.object(pack.Path, 'open', observed_open):
            result = pack.publish({'original.txt': (b'original fixture', False)}, output)
        self.assertEqual(modes, ['x+b'])
        self.assertEqual(result, {'bytes': len(expected), 'sha256': pack.digest(expected)})

    def test_nine_row_witness_preserves_original_bytes_and_reports_release_only_files(self):
        request, qualification, pins = self.original_complete_fixture()
        release = json.loads(Path(request['bridgeSourceInputs']['path']).read_bytes())
        rows = [item for item in release['files'] if item['path'] not in ('README.md', '.gitattributes')]
        original = (json.dumps(rows, indent=3) + '\r\n').encode()
        request['buildSourceWitness'] = self.artifact('windows-nine-witness.json', original)
        qualification['sourceAlignment']['buildSourceWitnessSha256'] = pack.digest(original)
        qualification['binaryBuildSourceState'] = 'working-source build; original nine-file witness'
        request['qualification'] = self.artifact('qualification.json', pack.canonical_json(qualification))
        with pins:
            files = pack.assemble(request)
        self.assertEqual(files['provenance/build-source-witness.json'][0], original)
        manifest = json.loads(files['provenance/package-manifest.json'][0])
        self.assertEqual(manifest['buildSourceWitness'], {'format': 'original-row-array', 'fileCount': 9,
                                                        'releaseOnlyFiles': ['.gitattributes', 'README.md']})
        self.assertEqual(manifest['binaryBuildSourceState'], qualification['binaryBuildSourceState'])

    def test_build_witness_refuses_missing_changed_duplicate_or_unknown_code_inputs(self):
        request, qualification, _ = self.original_complete_fixture()
        members, _ = pack.tar_members(Path(request['sourceArchive']['path']).read_bytes())
        rows = json.loads(Path(request['bridgeSourceInputs']['path']).read_bytes())['files']
        core = [item for item in rows if item['path'] not in ('README.md', '.gitattributes')]
        for change in (lambda v: v.pop(), lambda v: v[0].update(sha256='0' * 64),
                       lambda v: v.append(dict(v[0])), lambda v: v[0].update(path='unexpected.cpp')):
            bad = copy.deepcopy(core)
            change(bad)
            with self.assertRaises(pack.PackageError):
                pack.verify_build_witness(members, bad, qualification)

    def test_failed_write_preserves_partial_and_never_unlinks_by_path(self):
        output = self.root / 'partial.zip'
        preserved = self.root / 'preserved.bin'
        preserved.write_bytes(b'keep')
        def failed_write(stream, files):
            stream.write(b'partial fixture')
            raise OSError('synthetic write fault')
        with mock.patch.object(pack, 'write_zip', side_effect=failed_write), mock.patch.object(pack.Path, 'unlink') as unlink:
            with self.assertRaisesRegex(pack.PackageError, 'partial output preserved'):
                pack.publish({'a': (b'fixture', False)}, output)
        unlink.assert_not_called()
        self.assertEqual(output.read_bytes(), b'partial fixture')
        self.assertEqual(preserved.read_bytes(), b'keep')

    def test_archive_file_directory_conflict_and_duplicate_case_are_refused(self):
        for entries in ([('a', b'fixture'), ('a/b', b'child')], [('same', b'1'), ('same', b'2')],
                        [('x/CON.txt', b'fixture')], [('a/./b', b'fixture')]):
            with self.subTest(entries=entries), self.assertRaises(pack.PackageError):
                pack.tar_members(tar_bytes(entries))

    def test_compressed_tar_metadata_and_trailing_payload_are_bounded(self):
        # An oversized PAX entry is processed before tarfile yields its member.
        # Tiny compressed fixture, bounded here before its metadata is parsed.
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode='w') as archive:
            header = tarfile.TarInfo('pax')
            header.type = tarfile.XHDTYPE
            header.size = 40000
            archive.addfile(header, io.BytesIO(b'0' * header.size))
        with self.assertRaises(pack.PackageError):
            pack.tar_members(gzip.compress(stream.getvalue()), maximum=100, max_entries=2)
        valid = tar_bytes([('LICENSE', b'original fixture')])
        self.assertEqual(pack.tar_members(gzip.compress(valid))[0]['LICENSE'], b'original fixture')
        with self.assertRaises(pack.PackageError):
            pack.tar_members(valid + b'unexpected appended bytes')

    def test_qualification_rejects_private_paths_unknown_keys_and_boolean_counts(self):
        request, value = self.qualification()
        for change in (lambda v: v.update(environment=r'C:\Users\private\evidence'),
                       lambda v: v.update(environment=r'\\server\private\evidence'),
                       lambda v: v.update(environment='review on /opt/private/evidence'),
                       lambda v: v.update(privateLog='secret'),
                       lambda v: v['runtimeFiles'][0].update(path=r'C:\Users\private\worker.exe'),
                       lambda v: v['bridgeTests'].update(jsonPassed=True),
                       lambda v: v['upstreamCoreBaseline'].update(failed=True)):
            bad = copy.deepcopy(value)
            change(bad)
            with self.assertRaises(pack.PackageError):
                pack.validate_qualification(bad, request)

    def test_request_reads_are_bounded_even_if_file_grows_after_fstat(self):
        path = self.root / 'request.json'
        path.write_bytes(b'{}')
        real_fstat = os.fstat
        calls = []
        def changed_after_fstat(fd):
            result = real_fstat(fd)
            calls.append(fd)
            if len(calls) == 1:
                with path.open('ab') as stream:
                    stream.write(b' ' * 200)
            return result
        with mock.patch.object(pack, 'MAX_JSON', 100), mock.patch.object(pack.os, 'fstat', side_effect=changed_after_fstat):
            with self.assertRaises(pack.PackageError):
                pack.read_request(path)
        path.write_bytes(b'{"schemaVersion":1}')
        self.assertEqual(pack.read_request(path), {'schemaVersion': 1})


if __name__ == '__main__':
    unittest.main()

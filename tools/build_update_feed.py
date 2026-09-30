"""Create release metadata from an already verified Windows package; never upload.

Run Verify-Distribution.ps1 first. Output is a new file; published versions and
package bytes are immutable. Copy this same feed to the portfolio and GitLab.
"""
import argparse
import hashlib
import json
import pathlib
import re
import zipfile


def build(package, output):
    package = pathlib.Path(package).resolve()
    output = pathlib.Path(output).resolve()
    if output.exists():
        raise ValueError('Choose a new feed output; existing release metadata is preserved')
    if not package.is_file() or not 0 < package.stat().st_size <= 1024 ** 3:
        raise ValueError('Expected a Windows package smaller than 1 GiB')
    with zipfile.ZipFile(package) as archive:
        roots = {p.split('/')[0] for p in archive.namelist()}
        if len(roots) != 1:
            raise ValueError('Expected exactly one package root')
        root = roots.pop()
        provenance = json.loads(archive.read(root + '/PROVENANCE.json').decode('utf-8-sig'))
        version = provenance['version']
        if not re.fullmatch(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-mots\.(0|[1-9]\d*)-dev)?', version):
            raise ValueError('Unsupported SED version')
        expected = 'SED-MotS-' + version + '-win-x64'
        if root != expected or package.name != expected + '.zip':
            raise ValueError('Package filename/root/version disagree')
        if provenance['target'] != 'win-x64' or provenance['selfContained'] is not True:
            raise ValueError('Expected a self-contained Windows package')
        if not re.fullmatch(r'[0-9a-f]{40}', provenance['commit']):
            raise ValueError('Missing exact source commit')
        for path in ('Sed.App.exe', 'Sed.App.dll', 'coreclr.dll', 'agent/sed-agent.exe', 'mcp/sed-mcp.exe'):
            if archive.getinfo(root + '/' + path).file_size <= 0:
                raise ValueError('Missing executable: ' + path)
    with package.open('rb') as stream:
        sha = hashlib.file_digest(stream, 'sha256').hexdigest()
    feed = {'schemaVersion': 1, 'releases': [{
        'version': version, 'channel': 'preview' if '-mots.' in version else 'stable',
        'platform': 'win-x64',
        'packageUrl': f'https://gitlab.com/api/v4/projects/86965025/packages/generic/sed-mots/{version}/{package.name}',
        'sha256': sha, 'sizeBytes': package.stat().st_size, 'entryDirectory': root,
    }]}
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(feed, stream, indent=2)
        stream.write('\n')
    return {'feed': str(output), 'packageSha256': sha, 'sourceCommit': provenance['commit'], 'published': False}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    print(json.dumps(build(args.package, args.output), indent=2))

"""Verify runtime-only release assets and reject mixed plugin/runtime output."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def check_sums(folder, filename, expected):
    entries = {}
    for line in (folder / filename).read_text().splitlines():
        checksum, name = line.split('  ', 1)
        assert name not in entries
        assert checksum == digest(folder / name), name
        entries[name] = checksum
    assert set(entries) == expected


def test_rizin():
    version = json.loads((ROOT / 'bundle.lock.json').read_text())['bundleVersion']
    scratch = ROOT / 'build' / f'runtime-packaging-test-{uuid.uuid4().hex}'
    runtime, output = scratch / 'custom-runtime-directory', scratch / 'release'
    (runtime / 'bin').mkdir(parents=True)
    (runtime / 'bin/rizin.exe').write_bytes(b'packaging fixture; never executed')
    (runtime / 'bundle-manifest.json').write_text(json.dumps({'bundleVersion': version}))
    command = [sys.executable, str(ROOT / 'scripts/package_bundle.py'), '--runtime', str(runtime),
               '--version', version, '--output', str(output)]
    subprocess.run(command, check=True)
    name = f'rizin-windows-x64-bundle-v{version}.zip'
    assert {p.name for p in output.iterdir()} == {name, 'bundle-manifest.json', 'SHA256SUMS'}
    with zipfile.ZipFile(output / name) as archive:
        assert set(archive.namelist()) == {'rizin/bin/rizin.exe', 'rizin/bundle-manifest.json'}
    check_sums(output, 'SHA256SUMS', {name, 'bundle-manifest.json'})
    before = {p.name: digest(p) for p in output.iterdir()}
    obsolete = output / 'obsolete-plugin.zip'
    obsolete.write_bytes(b'old plugin output')
    rejected = subprocess.run(command, capture_output=True, text=True)
    assert rejected.returncode and 'clean runtime release directory' in rejected.stderr
    assert all(digest(output / name) == checksum for name, checksum in before.items())
    obsolete.unlink()
    for marker in ('skills', '.codex-plugin', '.claude-plugin'):
        (runtime / marker).mkdir()
        rejected = subprocess.run(command, capture_output=True, text=True)
        assert rejected.returncode and 'Runtime contains plugin files' in rejected.stderr
        (runtime / marker).rmdir()
    print('PASS: Rizin runtime-only archive, checksums, mixed-output rejection, and plugin-content rejection')


def test_hyperv(folder):
    runtime = json.loads((ROOT / 'plugins/hyper-v-control/runtime.json').read_text())
    tag = runtime['tag']
    expected = {runtime['asset'], f'{tag}-NOTICES.txt', f'{tag}-release.json', f'{tag}-SHA256SUMS.txt'}
    assert {p.name for p in folder.iterdir()} == expected
    inventory = json.loads((folder / f'{tag}-release.json').read_text())
    assert inventory['version'] == runtime['version']
    assert {a['kind'] for a in inventory['assets']} == {'executable', 'notices'}
    assert {a['name'] for a in inventory['assets']} == {runtime['asset'], f'{tag}-NOTICES.txt'}
    for asset in inventory['assets']:
        assert digest(folder / asset['name']) == asset['sha256']
        assert (folder / asset['name']).stat().st_size == asset['size']
    assert digest(folder / runtime['asset']) == runtime['sha256']
    check_sums(folder, f'{tag}-SHA256SUMS.txt', expected - {f'{tag}-SHA256SUMS.txt'})
    print('PASS: Hyper-V executable, notices, inventory, and checksums; no plugin archive')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--hyperv-release', type=Path)
    args = parser.parse_args()
    test_hyperv(args.hyperv_release) if args.hyperv_release else test_rizin()

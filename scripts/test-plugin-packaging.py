"""Verify complete plugin ZIPs without redundant standalone skill downloads."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import uuid
import zipfile

root = Path(__file__).resolve().parents[1]
version = json.loads((root / 'plugins/rizin-re-toolkit/plugin.json').read_text(encoding='utf-8-sig'))['version']
tag = f'rizin-re-toolkit-v{version}'
output = root / 'build' / f'plugin-packaging-test-{uuid.uuid4().hex}'
output.mkdir(parents=True)
(output / f'{tag}.skill').write_bytes(b'obsolete standalone export')
subprocess.run([sys.executable, str(root / 'scripts/package_bundle.py'), '--plugin-only', '--version', version, '--output', str(output)], check=True)
zip_name = f'rizin-re-toolkit-plugin-v{version}.zip'
expected = {zip_name, f'{tag}-release.json', f'{tag}-SHA256SUMS.txt'}
assert {p.name for p in output.iterdir()} == expected, 'Only the plugin ZIP, inventory and checksum file should be published'
with zipfile.ZipFile(output / zip_name) as archive:
    assert 'skills/rizin-re-toolkit/SKILL.md' in archive.namelist(), 'Skill must remain bundled in the plugin'
    assert 'plugin.json' in archive.namelist(), 'Plugin manifest is required'
inventory = json.loads((output / f'{tag}-release.json').read_text())
assert len(inventory['assets']) == 1 and inventory['assets'][0]['name'] == zip_name
for line in (output / f'{tag}-SHA256SUMS.txt').read_text().splitlines():
    digest, name = line.split('  ', 1)
    assert hashlib.sha256((output / name).read_bytes()).hexdigest() == digest
print('PASS: plugin includes its skill; no standalone skill asset; inventory and checksums match')

"""Package the portable Rizin runtime; plugins are installed from the marketplace."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def archive(source, output):
    with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as result:
        for file in sorted(source.rglob('*')):
            if not file.is_file():
                continue
            if file.is_symlink() or '.git' in file.parts:
                raise RuntimeError(f'Unexpected link or Git metadata: {file}')
            result.write(file, (Path('rizin') / file.relative_to(source)).as_posix())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--version', required=True)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', args.version):
        raise RuntimeError('Use a numeric semantic version')
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / 'build'):
        raise RuntimeError('Output must be inside the repository build directory')
    runtime = args.runtime.resolve()
    if not runtime.is_relative_to(ROOT / 'build') or output.is_relative_to(runtime):
        raise RuntimeError('Runtime must be inside build, and output must be outside the runtime')
    manifest = json.loads((runtime / 'bundle-manifest.json').read_text(encoding='utf-8-sig'))
    if manifest['bundleVersion'] != args.version:
        raise RuntimeError('Runtime and requested versions disagree')
    if any((runtime / name).exists() for name in ('skills', 'plugin.json', '.codex-plugin', '.claude-plugin', '.mcp.json', 'mcp.json')):
        raise RuntimeError('Runtime contains plugin files; reassemble it without marketplace content')
    name = f'rizin-windows-x64-bundle-v{args.version}.zip'
    output.mkdir(parents=True, exist_ok=True)
    allowed = {name, 'bundle-manifest.json', 'verification.json', 'SHA256SUMS'}
    if any(path.name not in allowed or not path.is_file() for path in output.iterdir()):
        raise RuntimeError('Use a clean runtime release directory without unrelated or obsolete assets')
    destination = output / name
    archive(runtime, destination)
    print(f'{name}: {destination.stat().st_size:,} bytes', flush=True)
    manifest_path = output / 'bundle-manifest.json'
    shutil.copy2(runtime / 'bundle-manifest.json', manifest_path)
    artifacts = [destination, manifest_path]
    # Package evidence only when it was produced by the verifier, never a dump.
    evidence = output / 'verification.json'
    if evidence.is_file():
        artifacts.append(evidence)
    sums = ''.join(f'{sha256(file)}  {file.name}\n' for file in artifacts)
    # Existing runtime installers require this filename.
    (output / 'SHA256SUMS').write_text(sums, encoding='ascii')


if __name__ == '__main__':
    main()

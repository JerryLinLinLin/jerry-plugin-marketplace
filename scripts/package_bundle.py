"""Package the runtime and independently versioned plugin/skill artifacts."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def archive(source, output, include_root=False):
    with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as result:
        for file in sorted(source.rglob('*')):
            if not file.is_file():
                continue
            if file.is_symlink() or '.git' in file.parts:
                raise RuntimeError(f'Unexpected link or Git metadata: {file}')
            base = source.parent if include_root else source
            result.write(file, file.relative_to(base).as_posix())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--version', required=True)
    parser.add_argument('--runtime', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--plugin-only', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', args.version):
        raise RuntimeError('Use a numeric semantic version')
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / 'build'):
        raise RuntimeError('Output must be inside the repository build directory')
    plugin = ROOT / 'plugins/rizin-re-toolkit'
    metadata = json.loads((plugin / 'plugin.json').read_text(encoding='utf-8-sig'))
    plugin_version = metadata['version']
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', plugin_version):
        raise RuntimeError('Plugin manifest must use a numeric semantic version')
    packages = []
    if args.plugin_only:
        if plugin_version != args.version:
            raise RuntimeError('Plugin and requested versions disagree')
    else:
        if args.runtime is None:
            raise RuntimeError('--runtime is required unless --plugin-only is set')
        runtime = args.runtime.resolve()
        if not runtime.is_relative_to(ROOT / 'build') or output.is_relative_to(runtime):
            raise RuntimeError('Runtime must be inside build, and output must be outside the runtime')
        manifest = json.loads((runtime / 'bundle-manifest.json').read_text(encoding='utf-8-sig'))
        if manifest['bundleVersion'] != args.version:
            raise RuntimeError('Runtime and requested versions disagree')
        packages.append((runtime, f'rizin-windows-x64-bundle-v{args.version}.zip', True))
    packages.extend([
        (plugin, f'rizin-re-toolkit-plugin-v{plugin_version}.zip', False),
        (plugin / 'skills/rizin-re-toolkit', f'rizin-re-toolkit-v{plugin_version}.skill', True),
    ])
    output.mkdir(parents=True, exist_ok=True)
    artifacts = []
    for folder, name, root in packages:
        destination = output / name
        archive(folder, destination, root)
        artifacts.append(destination)
        print(f'{destination.name}: {destination.stat().st_size:,} bytes', flush=True)
    if not args.plugin_only:
        manifest_path = output / 'bundle-manifest.json'
        shutil.copy2(runtime / 'bundle-manifest.json', manifest_path)
        artifacts.append(manifest_path)
        # Package evidence only when it was produced by the verifier, never a dump.
        evidence = output / 'verification.json'
        if evidence.is_file():
            artifacts.append(evidence)
    sums = ''.join(f'{hashlib.file_digest(file.open("rb"), "sha256").hexdigest()}  {file.name}\n' for file in artifacts)
    (output / 'SHA256SUMS').write_text(sums, encoding='ascii')


if __name__ == '__main__':
    main()

"""Exercise the real REPL and trace executables on owned native fixtures."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument('--fixtures', type=Path, required=True)
    cli.add_argument('--out', type=Path, required=True)
    options = cli.parse_args()
    output = options.out.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source = ROOT / 'plugins/frida-use/skills/frida-use/assets/windows-observe.js'
    results = []
    for arch in ('x64', 'x86'):
        executable = (options.fixtures / arch / 'fixture.exe').resolve()
        for mode in ('repl', 'trace'):
            folder = output / (arch + '-' + mode)
            folder.mkdir()
            if mode == 'repl':
                command = [shutil.which('frida'), '-f', str(executable), '-l', str(source), '-q', '-t', '3',
                           '--exit-on-error', '--kill-on-exit', '--', str(folder / 'cli.bin'), '1']
            else:
                command = [shutil.which('frida-trace'), '-f', str(executable), '-i', 'KERNELBASE.dll!ReadFile',
                           '--', str(folder / 'cli.bin'), '1']
            result = subprocess.run(command, cwd=folder, capture_output=True, text=True, encoding='utf-8', errors='replace',
                                    timeout=20, env={**os.environ, 'PYTHONUTF8': '1'})
            (folder / 'stdout.txt').write_text(result.stdout, encoding='utf-8')
            (folder / 'stderr.txt').write_text(result.stderr, encoding='utf-8')
            if mode == 'trace':
                # frida-tools reports natural target detachment as CLI exit 1.
                assert result.returncode == 1 and 'Process terminated' in result.stdout, (arch, result.stdout, result.stderr)
                assert 'ms  ReadFile()' in result.stdout
                assert list((folder / '__handlers__').rglob('ReadFile.js'))
            else:
                assert result.returncode == 0, (arch, mode, result.returncode, result.stdout, result.stderr)
                assert 'file-open' in result.stdout and 'ready' in result.stdout
            results.append({'arch': arch, 'mode': mode, 'passed': True, 'exit_code': result.returncode})
    (output / 'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
    print(json.dumps({'passed': len(results), 'evidence': str(output)}, indent=2))


if __name__ == '__main__':
    main()

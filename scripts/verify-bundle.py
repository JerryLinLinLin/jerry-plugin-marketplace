"""Exercise a relocated Windows runtime against benign PE, dump, and raw fixtures."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time

import pefile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--dump', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    runtime, fixture, dump, output = (p.resolve() for p in (args.runtime, args.fixture, args.dump, args.output))
    output.mkdir(parents=True, exist_ok=True)
    cache = output / 'retdec-cache'
    cache.mkdir(exist_ok=True)
    env = os.environ.copy()
    for key in list(env):
        if key.startswith(('RZ_', 'RIZIN_', 'SLEIGH', 'DEC_')):
            env.pop(key)
    env['PATH'] = str(runtime / 'bin') + os.pathsep + str(Path(env['SYSTEMROOT']) / 'System32')
    env['DEC_SAVE_DIR'] = str(cache)
    exe = runtime / 'bin/rizin.exe'
    report = {'runtime': str(runtime), 'checks': {}, 'warnings': {}, 'elapsedSeconds': 0}
    start = time.monotonic()

    def run(name, argv, dump_warnings=False):
        completed = subprocess.run([str(x) for x in argv], env=env, cwd=output,
                                   capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=180)
        (output / (name + '.stdout.txt')).write_text(completed.stdout, encoding='utf-8')
        (output / (name + '.stderr.txt')).write_text(completed.stderr, encoding='utf-8')
        if completed.returncode:
            raise RuntimeError(f'{name}: exit {completed.returncode}: {completed.stderr[-1500:]}')
        remaining = completed.stderr
        if dump_warnings:
            # Known 0.9.1 MDMP parser diagnostics; preserve them in the report.
            remaining = '\n'.join(line for line in remaining.splitlines() if not any(text in line for text in (
                'Invalid or unsupported enumeration encountered 21',
                'Invalid or unsupported enumeration encountered 22',
                'Invalid cert.dwLength (must be > 6)',
                'Parsing data sections for large dumps',
            )))
            if completed.stderr.strip():
                report['warnings'][name] = completed.stderr.strip().splitlines()
        if re.search(r'ERROR:|assertion .*failed|Cannot find|Could not load|No function at', remaining, re.I):
            raise RuntimeError(f'{name}: unexpected diagnostic: {remaining[-1500:]}')
        return completed.stdout.strip()

    def rz(name, commands, target=fixture, extra=(), dump_warnings=False):
        argv = [exe, '-q', '-N', '-e', 'scr.color=0', *extra]
        for command in commands:
            argv.extend(('-c', command))
        return run(name, [*argv, target], dump_warnings)

    def arithmetic(text):
        assert 'return' in text
        assert re.search(r'\*\s*(?:3|0x0?3)\b|\b3\s*\*', text), text
        assert re.search(r'\+(?:=)?\s*(?:7|0x0?7)\b', text), text

    version = run('version', [exe, '-v'])
    assert 'rizin 0.9.1 @ windows-x86-64' in version
    report['checks']['version'] = version
    # Inspect all shipped PE executables and DLLs, including delay imports.
    local = {p.name.lower() for p in (runtime / 'bin').glob('*.dll')}
    system = Path(env['SYSTEMROOT']) / 'System32'
    binaries = sorted([p for p in runtime.rglob('*') if p.suffix.lower() in ('.exe', '.dll')])
    missing = []
    for binary in binaries:
        with pefile.PE(str(binary)) as pe:
            assert pe.FILE_HEADER.Machine == 0x8664, binary
            for entry in [*getattr(pe, 'DIRECTORY_ENTRY_IMPORT', []), *getattr(pe, 'DIRECTORY_ENTRY_DELAY_IMPORT', [])]:
                name = entry.dll.decode().lower()
                if name not in local and not (system / name).is_file() and not name.startswith(('api-ms-win-', 'ext-ms-win-')):
                    missing.append([str(binary.relative_to(runtime)), name])
    assert not missing, missing
    for name in ('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'libcrypto-1_1-x64.dll', 'libcrypto-3-x64.dll'):
        assert name in local, name
    report['checks']['x64AndDependencies'] = {'peFiles': len(binaries), 'missing': missing}

    exports = json.loads(rz('pe-exports', ['iEj']))
    symbol = next(item['flagname'] for item in exports if item['name'] == 'bundle_fixture')
    for engine in ('pdg', 'pdd', 'pdz'):
        code = rz(engine, [f'af @ {symbol}', f'{engine} @ {symbol}'])
        arithmetic(code)
        report['checks'][engine] = 'Recovered multiplication by 3 and addition of 7'
    for engine in ('pdgj', 'pddj', 'pdzj'):
        value = json.loads(rz(engine, [f'af @ {symbol}', f'{engine} @ {symbol}']))
        assert value
        report['checks'][engine] = 'Valid nonempty JSON'
    sleigh = rz('sleigh-home', ['e ghidra.sleighhome'])
    assert Path(sleigh).resolve().is_relative_to(runtime), sleigh
    assert 'x86:LE:64' in rz('sleigh-languages', ['pdgs'])
    report['checks']['sleigh'] = sleigh

    rules = output / 'fixture.yar'
    digest = hashlib.sha256(fixture.read_bytes()).hexdigest()
    rules.write_text('import "hash"\nimport "pe"\n'
                     'rule bundle_marker { strings: $a = "RIZIN_BUNDLE_SMOKE_2026" condition: pe.is_pe and $a }\n'
                     f'rule bundle_sha {{ strings: $a = "RIZIN_BUNDLE_SMOKE_2026" condition: $a and hash.sha256(0, filesize) == "{digest}" }}\n', encoding='utf-8')
    yara_output = rz('yara', ['e io.va=false', f'yaral "{rules.as_posix()}"', 'yaraMj'])
    matches = json.loads(yara_output.splitlines()[-1])
    assert 'bundle_marker' in json.dumps(matches) and 'bundle_sha' in json.dumps(matches), matches
    report['checks']['yara'] = 'PE/string and OpenSSL SHA-256 rules matched'
    signatures = rz('flirt-list', ['Fl'])
    assert 'VisualStudio2022.sig' in signatures and 'winsdk.sig' in signatures
    report['checks']['sigdb'] = 'Bundled Windows and cross-platform FLIRT databases listed'
    sig = output / 'fixture.sig'
    run('flirt-create', [runtime / 'bin/rz-sign.exe', '-o', sig, fixture])
    assert sig.stat().st_size > 16
    applied = rz('flirt-apply', ['aa', f'Fs "{sig.as_posix()}"', 'aflj'])
    assert 'flirt.' in applied, applied
    report['checks']['flirt'] = 'Created and applied a fixture FLIRT signature'

    info = json.loads(rz('dump-info', ['iIj'], dump, dump_warnings=True))
    assert info['bintype'] == 'mdmp' and info['bits'] == 64
    maps = json.loads(rz('dump-maps', ['omlj'], dump, dump_warnings=True))
    assert len(maps) > 1
    exports = json.loads(rz('dump-exports', ['iEj'], dump, dump_warnings=True))
    item = next(item for item in exports if item['name'] == 'bundle_fixture')
    assert any(m['from'] <= item['vaddr'] < m['to'] for m in maps)
    for engine in ('pdg', 'pdd'):
        arithmetic(rz('dump-' + engine, [f'af @ {item["flagname"]}', f'{engine} @ {item["flagname"]}'], dump, dump_warnings=True))
    report['checks']['minidump'] = {'format': info['bintype'], 'maps': len(maps), 'functionVA': hex(item['vaddr']), 'decompilers': ['pdg', 'pdd']}
    raw = output / 'region.bin'
    raw.write_bytes(bytes.fromhex('8d04498d4007c3'))  # eax = ecx * 3 + 7; ret
    raw_args = ('-F', 'any', '-a', 'x86', '-b', '64', '-m', '0x140000000')
    data = json.loads(rz('raw-disassembly', ['pdj 3'], raw, raw_args))
    assert data[0]['offset'] == 0x140000000 and data[-1]['type'] == 'ret'
    report['checks']['rawMemory'] = 'Explicit base address and x64 disassembly verified'
    report['elapsedSeconds'] = round(time.monotonic() - start, 2)
    (output / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'passed': len(report['checks']), 'output': str(output / 'report.json'), 'seconds': report['elapsedSeconds']}, indent=2))


if __name__ == '__main__':
    main()

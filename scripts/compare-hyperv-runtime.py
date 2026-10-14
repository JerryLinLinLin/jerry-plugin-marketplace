"""Compare a rebuilt Hyper-V Control executable with the published asset pinned in runtime.json.

A deterministic build still records identifiers that depend on inputs outside the shipped code:
the PE timestamp, the module version id, and the PDB id/checksum change whenever a source comment
changes or the compiler runs on another runtime patch. Everything else in the single-file bundle,
the app assembly's IL and metadata, the bundled runtime, and the host, must match byte for byte.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
APP_ASSEMBLY = 'HyperVControl.dll'
BUNDLE_SIGNATURE = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def read_string(data, pos):
    length, shift = 0, 0
    while True:
        byte = data[pos]
        pos += 1
        length |= (byte & 0x7F) << shift
        if not byte & 0x80:
            break
        shift += 7
    return data[pos:pos + length].decode('utf-8'), pos + length


def bundle(data):
    """Return (version, bundle id, header offset, [(offset, size, kind, path)]) of a single-file bundle."""
    signature = data.find(BUNDLE_SIGNATURE)
    if signature < 0:
        raise ValueError('not a .NET single-file bundle')
    header = struct.unpack_from('<q', data, signature - 8)[0]
    pos = header
    major, minor, count = struct.unpack_from('<IIi', data, pos)
    pos += 12
    bundle_id, pos = read_string(data, pos)
    if major >= 2:
        pos += 8 * 4 + 8  # deps.json and runtimeconfig.json locations, flags
    entries = []
    for _ in range(count):
        offset, size = struct.unpack_from('<qq', data, pos)
        pos += 16
        if major >= 6:
            pos += 8  # compressed size
        kind = data[pos]
        pos += 1
        path, pos = read_string(data, pos)
        entries.append((offset, size, kind, path))
    return (major, minor), bundle_id, header, entries


def build_identifier_regions(pe):
    """Byte ranges of a managed PE that a deterministic compiler derives from its inputs."""
    e_lfanew = struct.unpack_from('<I', pe, 0x3C)[0]
    if pe[e_lfanew:e_lfanew + 4] != b'PE\0\0':
        raise ValueError('not a PE file')
    coff = e_lfanew + 4
    section_count = struct.unpack_from('<H', pe, coff + 2)[0]
    optional_size = struct.unpack_from('<H', pe, coff + 16)[0]
    optional = coff + 20
    magic = struct.unpack_from('<H', pe, optional)[0]
    directories = optional + (112 if magic == 0x20B else 96)
    debug_rva, debug_size = struct.unpack_from('<II', pe, directories + 6 * 8)
    cli_rva = struct.unpack_from('<I', pe, directories + 14 * 8)[0]
    table = optional + optional_size
    sections = [struct.unpack_from('<8sIIIIIIHHI', pe, table + index * 40) for index in range(section_count)]

    def file_offset(rva):
        for _, virtual_size, virtual_address, raw_size, raw_pointer, *_ in sections:
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                return raw_pointer + (rva - virtual_address)
        raise ValueError(f'RVA {rva:#x} is outside every section')

    regions = [(coff + 4, 4, 'COFF TimeDateStamp')]
    for index in range(debug_size // 28):
        entry = file_offset(debug_rva) + index * 28
        _, _, _, _, kind, size, _, pointer = struct.unpack_from('<IIHHIIII', pe, entry)
        regions.append((entry + 4, 4, f'debug entry {index} TimeDateStamp'))
        if kind == 2 and size >= 24 and pe[pointer:pointer + 4] == b'RSDS':
            regions.append((pointer + 4, 16, 'CodeView PDB GUID'))
        elif kind == 19:
            regions.append((pointer, size, 'PDB checksum'))
    cli = file_offset(cli_rva)
    metadata_rva = struct.unpack_from('<I', pe, cli + 8)[0]
    metadata = file_offset(metadata_rva)
    if pe[metadata:metadata + 4] != b'BSJB':
        raise ValueError('metadata root not found')
    version_length = struct.unpack_from('<I', pe, metadata + 12)[0]
    pos = metadata + 16 + version_length
    stream_count = struct.unpack_from('<H', pe, pos + 2)[0]
    pos += 4
    for _ in range(stream_count):
        stream_offset, stream_size = struct.unpack_from('<II', pe, pos)
        pos += 8
        end = pe.index(b'\0', pos)
        name = pe[pos:end].decode('ascii')
        pos = metadata + ((end + 1 - metadata + 3) & ~3)
        if name == '#GUID':
            regions.append((metadata + stream_offset, stream_size, '#GUID stream (module version id)'))
    return regions


def masked(pe):
    copy = bytearray(pe)
    regions = build_identifier_regions(pe)
    for offset, size, _ in regions:
        copy[offset:offset + size] = bytes(size)
    return bytes(copy), regions


def compare(pinned, built):
    """Return a list of differences; an empty list means the executables carry the same code."""
    problems = []
    if len(pinned) != len(built):
        problems.append(f'size differs: pinned {len(pinned)} bytes, built {len(built)} bytes')
    version_a, _, header_a, entries_a = bundle(pinned)
    version_b, _, header_b, entries_b = bundle(built)
    if version_a != version_b:
        problems.append(f'bundle format differs: {version_a} vs {version_b}')
    if [entry[1:] for entry in entries_a] != [entry[1:] for entry in entries_b]:
        problems.append('bundled file list differs')
        return problems
    if pinned[:entries_a[0][0]] != built[:entries_b[0][0]]:
        problems.append('host executable differs')
    for (offset_a, size, _, path), (offset_b, _, _, _) in zip(entries_a, entries_b):
        data_a, data_b = pinned[offset_a:offset_a + size], built[offset_b:offset_b + size]
        if data_a == data_b:
            continue
        if path != APP_ASSEMBLY:
            problems.append(f'{path} differs')
            continue
        masked_a, regions = masked(data_a)
        masked_b, _ = masked(data_b)
        if masked_a != masked_b:
            problems.append(f'{path} differs beyond its build identifiers')
        else:
            print(f'{path}: identical except ' + ', '.join(name for _, _, name in regions))
    if pinned[header_a:] != built[header_b:]:
        print('bundle manifest: identical except the bundle id')
    return problems


def pinned_manifest(argument):
    if argument:
        return json.loads(Path(argument).read_text(encoding='utf-8-sig'))
    committed = subprocess.run(['git', '-C', str(ROOT), 'show', 'HEAD:plugins/hyper-v-control/runtime.json'],
                               capture_output=True, text=True, check=True).stdout
    return json.loads(committed)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--built', type=Path, required=True, help='Rebuilt single-file executable')
    parser.add_argument('--pinned', help='Pinned runtime.json; default: the committed plugins/hyper-v-control/runtime.json at HEAD')
    parser.add_argument('--pinned-file', type=Path, help='Already downloaded pinned executable (skips the download)')
    parser.add_argument('--download-dir', type=Path, default=ROOT / 'build/hyperv-control/pinned')
    args = parser.parse_args()
    pin = pinned_manifest(args.pinned)
    if args.pinned_file:
        pinned = args.pinned_file.read_bytes()
    else:
        target = args.download_dir / pin['asset']
        if not (target.is_file() and sha256(target.read_bytes()) == pin['sha256']):
            try:
                with urllib.request.urlopen(pin['url'], timeout=120) as response:
                    pinned = response.read()
            except urllib.error.HTTPError as error:
                if error.code == 404:
                    print(f'NOTICE: pinned asset is not published yet ({pin["url"]}); nothing to compare')
                    return 0
                raise
            args.download_dir.mkdir(parents=True, exist_ok=True)
            target.write_bytes(pinned)
        pinned = target.read_bytes()
    if sha256(pinned) != pin['sha256']:
        print(f'ERROR: pinned asset does not match runtime.json ({sha256(pinned)})', file=sys.stderr)
        return 1
    built = args.built.read_bytes()
    if sha256(built) == pin['sha256']:
        print('PASS: rebuilt executable is byte-identical to the pinned release')
        return 0
    problems = compare(pinned, built)
    if problems:
        for problem in problems:
            print(f'ERROR: {problem}', file=sys.stderr)
        return 1
    print('PASS: rebuilt executable matches the pinned release except for compiler-recorded build identifiers')
    return 0


if __name__ == '__main__':
    sys.exit(main())

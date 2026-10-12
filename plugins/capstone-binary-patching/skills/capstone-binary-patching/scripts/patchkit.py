# /// script
# requires-python = ">=3.11,<3.15"
# dependencies = ["capstone==5.0.9", "lief==1.0.0"]
# ///
"""Instruction-aware, copy-only binary patching. Run with uv; never executes inputs."""

from __future__ import annotations

import argparse
import difflib
import hashlib
import importlib.metadata
import io
import json
import os
from pathlib import Path
import platform
import stat
import sys

import capstone as cs
import lief

VERSION = "0.1.0"
SCHEMA = "capstone-patch-plan/v1"
ARCHES = {
    "x86": (cs.CS_ARCH_X86, cs.CS_MODE_32, 1),
    "x86_64": (cs.CS_ARCH_X86, cs.CS_MODE_64, 1),
    "arm": (cs.CS_ARCH_ARM, cs.CS_MODE_ARM, 4),
    "thumb": (cs.CS_ARCH_ARM, cs.CS_MODE_THUMB, 2),
    "arm64": (cs.CS_ARCH_ARM64, cs.CS_MODE_LITTLE_ENDIAN, 4),
    "mips32le": (cs.CS_ARCH_MIPS, cs.CS_MODE_MIPS32 | cs.CS_MODE_LITTLE_ENDIAN, 4),
    "mips32be": (cs.CS_ARCH_MIPS, cs.CS_MODE_MIPS32 | cs.CS_MODE_BIG_ENDIAN, 4),
}
NOP = {
    "x86": "90", "x86_64": "90", "arm": "00f020e3", "thumb": "00bf",
    "arm64": "1f2003d5", "mips32le": "00000000", "mips32be": "00000000",
}


class PatchError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise PatchError(message)


def number(value):
    require(isinstance(value, (str, int)) and not isinstance(value, bool), "Expected an integer or 0x-prefixed string")
    result = int(value, 0) if isinstance(value, str) else value
    require(0 <= result <= 0xFFFFFFFFFFFFFFFF, "Integer must fit an unsigned 64-bit value")
    return result


def hexbytes(value):
    require(isinstance(value, str), "Byte sequences must be hex strings")
    result = bytes.fromhex(value)
    require(bool(result), "Empty byte sequences are not patches")
    return result


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def emit(value):
    print(json.dumps(value, indent=2, ensure_ascii=True))


def write_new(path, data, mode=None):
    """Exclusive creation: existing files, symlinks and hardlinks are never overwritten."""
    target = Path(path)
    with target.open("xb") as handle:
        try:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        except BaseException:
            handle.close()
            target.unlink()
            raise
    try:
        require(target.read_bytes() == data, "Output read-back verification failed")
        if mode is not None:
            os.chmod(target, mode & 0o777)
    except BaseException:
        target.unlink()
        raise


def write_json(path, obj):
    write_new(path, (json.dumps(obj, indent=2, ensure_ascii=True) + "\n").encode("utf-8"))


def region(name, offset, size, va, virtual_size, executable):
    return dict(name=name, offset=offset, size=size, va=va,
                virtual_size=virtual_size, executable=bool(executable))


def layout(data, fmt="auto", base_va=None):
    require(fmt in ("auto", "raw"), "format must be auto or raw")
    if fmt == "raw":
        require(base_va is not None, "Raw input requires base_va (VA corresponding to file offset zero)")
        base = number(base_va)
        require(base + len(data) <= 1 << 64, "Raw mapping overflows address space")
        return dict(format="raw", machine="user-specified", arches=list(ARCHES),
                    regions=[region("raw", 0, len(data), base, len(data), True)])
    require(data[:4] not in (b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca",
                            b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca"),
            "Universal/fat Mach-O is unsupported: extract and identify a thin slice first")
    binary = lief.parse(io.BytesIO(data))
    require(binary is not None, "Not a supported executable; select raw with an explicit base_va")
    regions = []
    if isinstance(binary, lief.PE.Binary):
        name = "PE"
        machine = binary.header.machine.name
        arches = {"AMD64": ["x86_64"], "I386": ["x86"], "ARM": ["arm"],
                  "ARMNT": ["thumb"], "ARM64": ["arm64"]}.get(machine, [])
        for s in binary.sections:
            regions.append(region(s.name, s.pointerto_raw_data,
                                  min(s.sizeof_raw_data, s.virtual_size or s.sizeof_raw_data),
                                  binary.optional_header.imagebase + s.virtual_address,
                                  s.virtual_size, int(s.characteristics) & 0x20000000))
    elif isinstance(binary, lief.ELF.Binary):
        name = "ELF"
        require(binary.header.file_type.name in ("EXEC", "DYN"), "ELF must be EXEC or DYN; objects/cores need a separate mapping")
        machine = binary.header.machine_type.name
        big = binary.header.identity_data.name == "MSB"
        arches = {"X86_64": ["x86_64"], "I386": ["x86"], "ARM": ["arm", "thumb"],
                  "AARCH64": ["arm64"]}.get(machine, []) if not big else []
        if machine == "MIPS" and binary.header.identity_class.name == "ELF32":
            arches = ["mips32be" if big else "mips32le"]
        for i, s in enumerate(binary.segments):
            if s.type == lief.ELF.Segment.TYPE.LOAD:
                regions.append(region(f"PT_LOAD[{i}]", s.file_offset, s.physical_size,
                                      s.virtual_address, s.virtual_size, int(s.flags) & 1))
    elif isinstance(binary, lief.MachO.Binary):
        name = "Mach-O"
        require(binary.header.file_type.name in ("EXECUTE", "DYLIB", "BUNDLE"), "Mach-O must be a linked image")
        machine = binary.header.cpu_type.name
        arches = {"X86_64": ["x86_64"], "X86": ["x86"], "ARM": ["arm", "thumb"],
                  "ARM64": ["arm64"]}.get(machine, [])
        for s in binary.segments:
            regions.append(region(s.name, s.file_offset, s.file_size, s.virtual_address,
                                  s.virtual_size, int(s.init_protection) & 4))
    else:
        raise PatchError("Only PE, linked ELF, thin linked Mach-O and explicit raw inputs are supported")
    for r in regions:
        require(r["offset"] >= 0 and r["size"] >= 0 and r["offset"] + r["size"] <= len(data),
                f"Truncated or invalid file-backed region: {r['name']}")
        require(r["size"] <= r["virtual_size"] or name == "PE", "File-backed region exceeds virtual size")
    return dict(format=name, machine=machine, arches=arches, regions=regions)


def map_span(info, value, size, by="offset", executable=False):
    require(size > 0, "Mapping size must be positive")
    # Count all matching regions before checking permissions: overlapping mappings are ambiguous.
    matches = [r for r in info["regions"] if r["size"] > 0
               and r[by] < value + size and value < r[by] + r["size"]]
    require(len(matches) == 1 and matches[0][by] <= value
            and value + size <= matches[0][by] + matches[0]["size"],
            "Range must have exactly one file-backed mapping (no BSS, gaps, overlap or cross-region range)")
    r = matches[0]
    require(not executable or r["executable"], "Code patch is outside an executable region")
    other = "va" if by == "offset" else "offset"
    return r[other] + value - r[by]


def decode(data, arch, va):
    require(arch in ARCHES, f"Unsupported architecture: {arch}")
    arch_id, mode, alignment = ARCHES[arch]
    require(va % alignment == 0, f"Unaligned {arch} VA; Thumb state bit is not part of the byte address")
    require(0 <= va and va + len(data) <= 1 << 64, "Decode address overflow")
    require(bool(data), "Empty code range")
    md = cs.Cs(arch_id, mode)
    md.detail = True
    md.skipdata = False
    instructions = list(md.disasm(data, va))
    consumed = sum(i.size for i in instructions)
    require(consumed == len(data), f"Incomplete decode: {consumed}/{len(data)} bytes at VA {va:#x}; wrong mode, data or truncated instruction")
    return [dict(va=i.address, size=i.size, hex=i.bytes.hex(), mnemonic=i.mnemonic,
                 operands=i.op_str, groups=[i.group_name(g) for g in i.groups])
            for i in instructions]


def bounded(data, offset, size):
    require(size > 0 and offset + size <= len(data), "Requested range is outside the file or empty")
    require(size <= 65536, "Instruction inspection is limited to 64 KiB per range; split at known boundaries")
    return data[offset:offset + size]


def canonical_spec(spec):
    require(isinstance(spec, dict), "Spec must be an object")
    allowed = {"format", "base_va", "arch", "patches"}
    require(not set(spec) - allowed, f"Unknown spec fields: {sorted(set(spec) - allowed)}")
    fmt = spec.get("format", "auto")
    require(fmt in ("raw", "auto"), "format must be auto or raw")
    arch = spec.get("arch")
    require(arch in ARCHES or arch is None, "Unknown arch")
    require(fmt == "raw" or "base_va" not in spec, "base_va is only for raw files")
    patches = spec.get("patches")
    require(isinstance(patches, list) and 0 < len(patches) <= 10000, "Provide 1..10000 patches")
    result = []
    for p in patches:
        require(isinstance(p, dict), "Each patch must be an object")
        allowed_patch = {"offset", "before", "after", "kind", "anchor_offset", "reason"}
        require(not set(p) - allowed_patch, f"Unknown patch fields: {sorted(set(p) - allowed_patch)}")
        before, after = hexbytes(p["before"]), hexbytes(p["after"])
        require(len(before) == len(after), "Only equal-length edits are supported; use a rewriter for insertion/growth")
        require(before != after, "No-op patch")
        kind = p.get("kind")
        require(kind in ("code", "data"), "Each patch needs kind=code or kind=data")
        require(isinstance(p.get("reason"), str) and p["reason"].strip(), "Each patch needs a nonempty reason")
        q = dict(offset=number(p["offset"]), before=before.hex(), after=after.hex(), kind=kind, reason=p["reason"])
        if kind == "code":
            require(arch is not None, "Code patches require an explicit arch")
            q["anchor_offset"] = number(p["anchor_offset"])
            require(q["anchor_offset"] <= q["offset"], "Anchor must be at or before patch start")
        else:
            require("anchor_offset" not in p, "Data patches do not take anchor_offset")
        result.append(q)
    result.sort(key=lambda p: p["offset"])
    for a, b in zip(result, result[1:]):
        require(a["offset"] + len(bytes.fromhex(a["before"])) <= b["offset"], "Overlapping patches are not allowed")
    out = dict(format=fmt, arch=arch, patches=result)
    if fmt == "raw":
        out["base_va"] = number(spec["base_va"])
    return out


def transform(data, spec):
    info = layout(data, spec["format"], spec.get("base_va"))
    code_patches = [p for p in spec["patches"] if p["kind"] == "code"]
    require(not code_patches or spec["arch"] in info["arches"], "Selected architecture does not match the executable header")
    result = bytearray(data)
    for p in spec["patches"]:
        offset = p["offset"]
        before, after = bytes.fromhex(p["before"]), bytes.fromhex(p["after"])
        require(offset + len(before) <= len(data), "Patch extends beyond EOF")
        require(data[offset:offset + len(before)] == before, f"Expected original bytes do not match at offset {offset:#x}")
        result[offset:offset + len(before)] = after
    result = bytes(result)
    after_info = layout(result, spec["format"], spec.get("base_va"))
    require(not code_patches or (info["machine"] == after_info["machine"]
            and spec["arch"] in after_info["arches"]), "Patch changed the executable architecture")
    previews = []
    for p in code_patches:
        offset, anchor = p["offset"], p["anchor_offset"]
        size = len(bytes.fromhex(p["before"]))
        span = offset + size - anchor
        va = map_span(info, anchor, span, executable=True)
        require(map_span(after_info, anchor, span, executable=True) == va,
                "Patch changed the address mapping; use a structural rewriter")
        old = decode(bounded(data, anchor, span), spec["arch"], va)
        new = decode(bounded(result, anchor, span), spec["arch"], va)
        patch_va = va + offset - anchor
        require(any(i["va"] == patch_va for i in old), "Patch begins inside an original instruction")
        require(any(i["va"] == patch_va for i in new), "Patch begins inside a replacement instruction")
        previews.append(dict(offset=offset, va=patch_va,
                             before=[i for i in old if i["va"] >= patch_va],
                             after=[i for i in new if i["va"] >= patch_va]))
    return result, previews


def validate_plan(plan):
    require(isinstance(plan, dict) and plan.get("schema") == SCHEMA, "Unsupported plan schema")
    spec = canonical_spec(plan["spec"])
    for key in ("input_sha256", "output_sha256"):
        value = plan[key]
        require(isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdef" for c in value), "Invalid SHA-256")
    require(number(plan["size"]) > 0, "Invalid plan file size")
    return spec


def execute_plan(data, plan, reverse=False):
    spec = validate_plan(plan)
    expected = plan["output_sha256"] if reverse else plan["input_sha256"]
    target = plan["input_sha256"] if reverse else plan["output_sha256"]
    require(len(data) == plan["size"] and digest(data) == expected, "Input size/SHA-256 mismatch: wrong version, already patched or modified file")
    if reverse:
        for p in spec["patches"]:
            p["before"], p["after"] = p["after"], p["before"]
    result, previews = transform(data, spec)
    require(digest(result) == target, "Computed output SHA-256 does not match the plan")
    return result, previews


def assemble(arch, va, source, pad_to=None):
    try:
        import keystone as ks
    except ImportError as exc:
        raise PatchError("Assembly needs: uv run --with keystone-engine==0.9.2 patchkit.py assemble ...") from exc
    modes = {
        "x86": (ks.KS_ARCH_X86, ks.KS_MODE_32), "x86_64": (ks.KS_ARCH_X86, ks.KS_MODE_64),
        "arm": (ks.KS_ARCH_ARM, ks.KS_MODE_ARM), "thumb": (ks.KS_ARCH_ARM, ks.KS_MODE_THUMB),
        "arm64": (ks.KS_ARCH_ARM64, ks.KS_MODE_LITTLE_ENDIAN),
        "mips32le": (ks.KS_ARCH_MIPS, ks.KS_MODE_MIPS32 | ks.KS_MODE_LITTLE_ENDIAN),
        "mips32be": (ks.KS_ARCH_MIPS, ks.KS_MODE_MIPS32 | ks.KS_MODE_BIG_ENDIAN),
    }
    try:
        encoding, count = ks.Ks(*modes[arch]).asm(source, addr=va)
    except ks.KsError as exc:
        raise PatchError(f"Keystone assembly failed: {exc}") from exc
    require(encoding is not None and count > 0, "Assembler produced no instructions")
    data = bytes(encoding)
    unpadded_size = len(data)
    if pad_to is not None:
        nop = bytes.fromhex(NOP[arch])
        require(pad_to <= 65536 and pad_to >= len(data) and (pad_to - len(data)) % len(nop) == 0,
                "Patch does not fit pad-to, or padding would split an instruction")
        data += nop * ((pad_to - len(data)) // len(nop))
    return dict(hex=data.hex(), size=len(data), unpadded_size=unpadded_size,
                instructions=decode(data, arch, va))


def byte_diff(left, right, limit):
    require(0 < limit <= 10000, "limit must be 1..10000")
    ranges, start, changed, total_ranges = [], None, 0, 0
    for i in range(max(len(left), len(right)) + 1):
        differs = i < max(len(left), len(right)) and (i >= len(left) or i >= len(right) or left[i] != right[i])
        if differs:
            changed += 1
            if start is None:
                start = i
        elif start is not None:
            total_ranges += 1
            if len(ranges) < limit:
                ranges.append(dict(offset=start, size=i - start, before=left[start:min(i, start + 32)].hex(),
                                   after=right[start:min(i, start + 32)].hex(), preview_truncated=i - start > 32))
            start = None
    return dict(old_size=len(left), new_size=len(right), old_sha256=digest(left), new_sha256=digest(right),
                changed_positions=changed, range_count=total_ranges, ranges=ranges,
                ranges_truncated=total_ranges > limit, comparison="positional bytes, not function matching")


def parser():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--version", action="version", version=VERSION)
    sub = p.add_subparsers(dest="command", required=True)
    sub.add_parser("doctor", help="Report installed packages and native engine availability")
    for command in ("info", "map"):
        q = sub.add_parser(command)
        q.add_argument("file", type=Path)
        q.add_argument("--format", choices=("auto", "raw"), default="auto")
        q.add_argument("--base-va", type=number)
        if command == "map":
            group = q.add_mutually_exclusive_group(required=True)
            group.add_argument("--va", type=number)
            group.add_argument("--offset", type=number)
            q.add_argument("--size", type=number, required=True)
    q = sub.add_parser("disasm", help="Decode a bounded range; addresses are explicit")
    q.add_argument("file", type=Path)
    q.add_argument("--arch", choices=ARCHES, required=True)
    for option in ("offset", "va", "size"):
        q.add_argument("--" + option, type=number, required=True)
    q = sub.add_parser("assemble", help="Generate bytes with optional Keystone; does not write a binary")
    q.add_argument("--arch", choices=ARCHES, required=True)
    q.add_argument("--va", type=number, required=True)
    q.add_argument("--asm", required=True)
    q.add_argument("--pad-to", type=number)
    q = sub.add_parser("diff", help="Positional byte ranges, including insertion/deletion tails")
    q.add_argument("old", type=Path)
    q.add_argument("new", type=Path)
    q.add_argument("--limit", type=int, default=64)
    q = sub.add_parser("diff-code", help="Instruction diff of two already matched code ranges")
    q.add_argument("old", type=Path)
    q.add_argument("new", type=Path)
    q.add_argument("--arch", choices=ARCHES, required=True)
    for side in ("old", "new"):
        for option in ("offset", "va", "size"):
            q.add_argument(f"--{side}-{option}", type=number, required=True)
    q = sub.add_parser("plan", help="Validate a spec and create a hash-bound plan, without writing a binary")
    q.add_argument("file", type=Path)
    q.add_argument("--spec", type=Path, required=True)
    q.add_argument("--out", type=Path, required=True)
    for command in ("apply", "revert", "verify"):
        q = sub.add_parser(command)
        q.add_argument("file", type=Path)
        q.add_argument("--plan", type=Path, required=True)
        if command == "verify":
            q.add_argument("--state", choices=("original", "patched"), default="patched")
        else:
            q.add_argument("--out", type=Path, required=True)
    return p


def run(args):
    if args.command == "doctor":
        packages = {}
        for package in ("capstone", "lief", "keystone-engine", "unicorn"):
            try:
                packages[package] = importlib.metadata.version(package)
            except importlib.metadata.PackageNotFoundError:
                packages[package] = None
        emit(dict(patchkit=VERSION, python=platform.python_version(), python_executable=sys.executable,
                  python_base_prefix=sys.base_prefix, platform=platform.platform(),
                  packages=packages, capstone_binding=cs.__version__, capstone_core=cs.cs_version(),
                  decode_smoke=decode(bytes.fromhex("b807000000c3"), "x86_64", 0x1000),
                  note="Package metadata and Capstone's embedded version strings may differ"))
    elif args.command in ("info", "map"):
        data = args.file.read_bytes()
        info = layout(data, args.format, args.base_va)
        if args.command == "info":
            emit(dict(path=str(args.file.resolve()), size=len(data), sha256=digest(data), **info))
        else:
            by = "va" if args.va is not None else "offset"
            value = getattr(args, by)
            other = "offset" if by == "va" else "va"
            emit({by: value, other: map_span(info, value, args.size, by), "size": args.size})
    elif args.command == "disasm":
        emit(decode(bounded(args.file.read_bytes(), args.offset, args.size), args.arch, args.va))
    elif args.command == "assemble":
        emit(assemble(args.arch, args.va, args.asm, args.pad_to))
    elif args.command == "diff":
        emit(byte_diff(args.old.read_bytes(), args.new.read_bytes(), args.limit))
    elif args.command == "diff-code":
        decoded = []
        for side in ("old", "new"):
            data = getattr(args, side).read_bytes()
            offset, va, size = (getattr(args, f"{side}_{key}") for key in ("offset", "va", "size"))
            decoded.append(decode(bounded(data, offset, size), args.arch, va))
        lines = [[f"{i['hex']}  {i['mnemonic']} {i['operands']}" for i in items] for items in decoded]
        emit(dict(old=decoded[0], new=decoded[1],
                  diff=list(difflib.unified_diff(*lines, fromfile=str(args.old), tofile=str(args.new), lineterm="")),
                  note="Exact bytes and operands; no function matching, relocation normalization or equivalence proof"))
    elif args.command == "plan":
        data = args.file.read_bytes()
        spec = canonical_spec(read_json(args.spec))
        result, previews = transform(data, spec)
        plan = dict(schema=SCHEMA, tool_version=VERSION, size=len(data), input_sha256=digest(data),
                    output_sha256=digest(result), spec=spec, instruction_preview=previews)
        write_json(args.out, plan)
        emit(plan)
    else:
        data = args.file.read_bytes()
        plan = read_json(args.plan)
        reverse = args.command == "revert" or (args.command == "verify" and args.state == "patched")
        result, _ = execute_plan(data, plan, reverse)
        if args.command == "verify":
            emit(dict(verified=True, state=args.state, sha256=digest(data), size=len(data),
                      verification="hash, bytes, mapping and instruction boundaries; behavior requires a separate test"))
        else:
            write_new(args.out, result, stat.S_IMODE(args.file.stat().st_mode))
            emit(dict(output=str(args.out.resolve()), sha256=digest(result), size=len(result)))


def main():
    args = parser().parse_args()
    try:
        run(args)
        return 0
    except (PatchError, OSError, ValueError, KeyError, TypeError, cs.CsError, RuntimeError) as exc:
        print(json.dumps(dict(error=str(exc)), ensure_ascii=True), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())

# /// script
# requires-python = ">=3.11,<3.15"
# dependencies = ["lief==1.0.0", "capstone==5.0.9", "unicorn==2.1.4"]
# ///
"""Real CLI experiments: compile/run a local PE, patch/run/revert, then CPU emulation."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import struct
import subprocess

import lief
import unicorn as uc
from unicorn import arm64_const, arm_const, mips_const, x86_const

PLUGIN = Path(__file__).resolve().parents[1]
CLI = PLUGIN / "skills/capstone-binary-patching/scripts/patchkit.py"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check(condition, message):
    if not condition:
        raise AssertionError(message)


class Experiment:
    def __init__(self, out):
        self.out = out.resolve()
        self.out.mkdir(parents=True, exist_ok=False)
        self.log = self.out / "transcript.jsonl"
        self.results = []

    def command(self, argv, expected=0):
        argv = [str(a) for a in argv]
        result = subprocess.run(argv, cwd=self.out, capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=90)
        with self.log.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(dict(argv=argv, returncode=result.returncode,
                                         stdout=result.stdout, stderr=result.stderr)) + "\n")
        check(result.returncode == expected,
              f"Command returned {result.returncode}, expected {expected}: {argv}\n{result.stdout}\n{result.stderr}")
        return result

    def cli(self, *args, expected=0, assembler=False):
        argv = ["uv", "run", "--managed-python", "--locked"]
        if assembler:
            argv += ["--with", "keystone-engine==0.9.2"]
        result = self.command([*argv, CLI, *args], expected)
        return json.loads(result.stdout) if expected == 0 else result

    def record(self, name, **details):
        self.results.append(dict(name=name, passed=True, **details))
        print(f"PASS {name}", flush=True)

    def spec(self, name, value):
        path = self.out / name
        path.write_text(json.dumps(value, indent=2), encoding="utf-8")
        return path

    def native_pe(self):
        check(platform.system() == "Windows", "Native PE experiment requires Windows")
        vswhere = Path(os.environ["ProgramFiles(x86)"]) / "Microsoft Visual Studio/Installer/vswhere.exe"
        root = self.command([vswhere, "-latest", "-products", "*", "-requires",
                             "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"]).stdout.strip()
        check(bool(root), "MSVC x64 build tools are required")
        compilers = sorted((Path(root) / "VC/Tools/MSVC").glob("*/bin/Hostx64/x64/cl.exe"))
        check(bool(compilers), "Cannot locate MSVC compiler")
        tools = compilers[-1].parent
        (self.out / "kernel32.def").write_text("LIBRARY KERNEL32.dll\nEXPORTS\nExitProcess\nGetStdHandle\nWriteFile\n", encoding="ascii")
        self.command([tools / "lib.exe", "/nologo", "/machine:x64", "/def:kernel32.def", "/out:kernel32.lib"])
        for name, constant in (("original", 7), ("other-version", 9)):
            self.command([tools / "cl.exe", "/nologo", "/c", "/Od", "/GS-", f"/DPATCH_VALUE={constant}",
                          f"/Fo{name}.obj", PLUGIN / "experiments/fixture.c"])
            self.command([tools / "link.exe", "/nologo", "/machine:x64", "/nodefaultlib", "/subsystem:console",
                          "/entry:entry", f"/out:{name}.exe", f"{name}.obj", "kernel32.lib"])
        original = self.out / "original.exe"
        old_hash = sha(original)
        baseline = self.command([original], expected=7)
        check(baseline.stdout.strip() == "result=07", "Unexpected baseline output")
        binary = lief.parse(original)
        entry = next(e for e in binary.get_export().entries if e.name == "patch_target")
        va = binary.optional_header.imagebase + entry.address
        mapping = self.cli("map", original, "--va", hex(va), "--size", "5")
        offset = mapping["offset"]
        decoded = self.cli("disasm", original, "--arch", "x86_64", "--offset", hex(offset), "--va", hex(va), "--size", "5")
        check(len(decoded) == 1 and decoded[0]["hex"] == "b807000000", "Fixture compiler changed instruction shape")
        asm = self.cli("assemble", "--arch", "x86_64", "--va", hex(va), "--asm", "mov eax, 0x2a", assembler=True)
        spec = dict(format="auto", arch="x86_64", patches=[dict(kind="code", offset=offset,
                    anchor_offset=offset, before=decoded[0]["hex"], after=asm["hex"], reason="Experiment: return 42 instead of 7")])
        plan_path = self.out / "pe-plan.json"
        self.cli("plan", original, "--spec", self.spec("pe-spec.json", spec), "--out", plan_path)
        self.cli("verify", original, "--plan", plan_path, "--state", "original")
        patched = self.out / "patched.exe"
        self.cli("apply", original, "--plan", plan_path, "--out", patched)
        self.cli("verify", patched, "--plan", plan_path)
        result = self.command([patched], expected=42)
        check(result.stdout.strip() == "result=42", "Patched behavior is wrong")
        diff = self.cli("diff", original, patched)
        check(diff["changed_positions"] == 1 and diff["ranges"][0]["offset"] == offset + 1, "Unexpected extra byte changes")
        self.cli("diff-code", original, patched, "--arch", "x86_64", "--old-offset", offset,
                 "--new-offset", offset, "--old-va", va, "--new-va", va, "--old-size", 6, "--new-size", 6)
        restored = self.out / "restored.exe"
        self.cli("revert", patched, "--plan", plan_path, "--out", restored)
        self.cli("verify", restored, "--plan", plan_path, "--state", "original")
        back = self.command([restored], expected=7)
        check(sha(restored) == old_hash == sha(original), "Rollback/source hash mismatch")
        self.record("native Windows x64 PE: compile -> execute -> patch -> execute -> revert -> execute",
                    original_stdout=baseline.stdout.strip(), patched_stdout=result.stdout.strip(),
                    restored_stdout=back.stdout.strip(), exit_codes=[7, 42, 7], original_sha256=old_hash,
                    patched_sha256=sha(patched), restored_sha256=sha(restored), offset=offset, va=va,
                    compiler=str(compilers[-1]), modified_bytes=diff["changed_positions"])

        failures = []
        def rejected(name, *args, absent=None, message=None):
            result = self.cli(*args, expected=2)
            if absent is not None:
                check(not absent.exists(), f"{name} unexpectedly created output")
            if message is not None:
                check(message in result.stderr, f"{name} rejected for unexpected reason: {result.stderr}")
            failures.append(name)

        bad_out = self.out / "must-not-exist.exe"
        rejected("wrong build", "apply", self.out / "other-version.exe", "--plan", plan_path, "--out", bad_out,
                 absent=bad_out, message="SHA-256 mismatch")
        rejected("already patched", "apply", patched, "--plan", plan_path, "--out", bad_out, absent=bad_out)
        rejected("overwrite original", "apply", original, "--plan", plan_path, "--out", original)
        rejected("overwrite output", "apply", original, "--plan", plan_path, "--out", patched)
        corrupt = bytearray(patched.read_bytes())
        corrupt[-1] ^= 1
        (self.out / "tampered.exe").write_bytes(corrupt)
        rejected("tampering outside patch", "verify", self.out / "tampered.exe", "--plan", plan_path, message="SHA-256 mismatch")
        # 00 00 is a valid x86 instruction from a false start: anchor must catch this.
        bad_specs = []
        middle = json.loads(json.dumps(spec))
        middle["patches"][0].update(offset=offset + 2, before="0000", after="9090")
        bad_specs.append(("mid-instruction start", middle))
        trunc = json.loads(json.dumps(spec))
        trunc["patches"][0].update(before="b8070000", after="90909090")
        bad_specs.append(("truncated original instruction", trunc))
        replacement = json.loads(json.dumps(spec))
        replacement["patches"][0]["after"] = "909090900f"
        bad_specs.append(("truncated replacement", replacement))
        growth = json.loads(json.dumps(spec))
        growth["patches"][0]["after"] = "909090909090"
        bad_specs.append(("unequal length", growth))
        overlap = json.loads(json.dumps(spec))
        overlap["patches"].append(dict(overlap["patches"][0]))
        bad_specs.append(("overlapping hunks", overlap))
        wrong_mode = json.loads(json.dumps(spec))
        wrong_mode["arch"] = "x86"
        bad_specs.append(("wrong executable architecture", wrong_mode))
        for i, (label, value) in enumerate(bad_specs):
            out_plan = self.out / f"rejected-{i}.json"
            rejected(label, "plan", original, "--spec", self.spec(f"bad-spec-{i}.json", value), "--out", out_plan, absent=out_plan)
        check(sha(original) == old_hash and sha(patched) == diff["new_sha256"], "Rejected commands changed existing files")
        self.record("native PE CLI refusal paths", cases=failures, count=len(failures))

    def raw_emulation(self):
        cases = [
            ("x86", "mov eax, 0x7", "mov eax, 0x2a", uc.UC_ARCH_X86, uc.UC_MODE_32, x86_const.UC_X86_REG_EAX),
            ("x86_64", "mov eax, 0x7", "mov eax, 0x2a", uc.UC_ARCH_X86, uc.UC_MODE_64, x86_const.UC_X86_REG_EAX),
            ("arm", "mov r0, #7", "mov r0, #42", uc.UC_ARCH_ARM, uc.UC_MODE_ARM, arm_const.UC_ARM_REG_R0),
            ("thumb", "movs r0, #7", "movs r0, #42", uc.UC_ARCH_ARM, uc.UC_MODE_THUMB, arm_const.UC_ARM_REG_R0),
            ("arm64", "mov w0, #7", "mov w0, #42", uc.UC_ARCH_ARM64, uc.UC_MODE_ARM, arm64_const.UC_ARM64_REG_W0),
            ("mips32le", "addiu $v0, $zero, 7", "addiu $v0, $zero, 42", uc.UC_ARCH_MIPS, uc.UC_MODE_MIPS32 | uc.UC_MODE_LITTLE_ENDIAN, mips_const.UC_MIPS_REG_V0),
            ("mips32be", "addiu $v0, $zero, 7", "addiu $v0, $zero, 42", uc.UC_ARCH_MIPS, uc.UC_MODE_MIPS32 | uc.UC_MODE_BIG_ENDIAN, mips_const.UC_MIPS_REG_V0),
        ]
        for arch, old_asm, new_asm, uc_arch, mode, register in cases:
            old = self.cli("assemble", "--arch", arch, "--va", "0x1000", "--asm", old_asm, assembler=True)
            new = self.cli("assemble", "--arch", arch, "--va", "0x1000", "--asm", new_asm, assembler=True)
            before = self.out / f"{arch}.bin"
            before.write_bytes(bytes.fromhex(old["hex"]))
            spec = dict(format="raw", base_va="0x1000", arch=arch, patches=[dict(kind="code", offset=0,
                        anchor_offset=0, before=old["hex"], after=new["hex"], reason="CPU experiment: constant 7 to 42")])
            plan = self.out / f"{arch}-plan.json"
            self.cli("plan", before, "--spec", self.spec(f"{arch}-spec.json", spec), "--out", plan)
            after = self.out / f"{arch}-patched.bin"
            restored = self.out / f"{arch}-restored.bin"
            self.cli("apply", before, "--plan", plan, "--out", after)
            self.cli("verify", after, "--plan", plan)
            self.cli("revert", after, "--plan", plan, "--out", restored)
            values = []
            for path in (before, after, restored):
                machine = uc.Uc(uc_arch, mode)
                machine.mem_map(0x1000, 0x1000)
                machine.mem_write(0x1000, path.read_bytes())
                machine.emu_start(0x1001 if arch == "thumb" else 0x1000, 0x1000 + path.stat().st_size, timeout=100000, count=1)
                values.append(machine.reg_read(register))
            check(values == [7, 42, 7] and sha(before) == sha(restored), f"Emulation mismatch: {arch}, {values}")
            self.record(f"CPU emulation {arch}: full CLI apply/verify/revert", register_values=values,
                        before=old["hex"], after=new["hex"], original_sha256=sha(before), restored_sha256=sha(restored))

    def branch_and_data(self):
        # Absolute branch targets expose an incorrect assembly origin.
        source = "cmp edi, 0x7; jne 0x8010; mov eax, 0x7; jmp 0x8015; nop; nop; nop; nop; mov eax, 0x1; nop"
        assembled = self.cli("assemble", "--arch", "x86_64", "--va", "0x8000", "--asm", source, assembler=True)
        code = bytes.fromhex(assembled["hex"])
        check(len(code) == 22 and code[:3].hex() == "83ff07", "Unexpected branch fixture encoding")
        original = self.out / "branch-data.bin"
        original.write_bytes(code + b"OLD\0")
        spec = dict(format="raw", base_va=0x8000, arch="x86_64", patches=[
            dict(kind="code", offset=0, anchor_offset=0, before="83ff07", after="83ff08", reason="Correct comparison threshold"),
            dict(kind="data", offset=len(code), before="4f4c44", after="4e4557", reason="Change fixture label OLD to NEW")])
        plan = self.out / "branch-data-plan.json"
        after = self.out / "branch-data-patched.bin"
        back = self.out / "branch-data-restored.bin"
        self.cli("plan", original, "--spec", self.spec("branch-data-spec.json", spec), "--out", plan)
        self.cli("apply", original, "--plan", plan, "--out", after)
        self.cli("verify", after, "--plan", plan)
        self.cli("revert", after, "--plan", plan, "--out", back)
        outputs = []
        for path in (original, after, back):
            values = []
            for value in (6, 7, 8):
                machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_64)
                machine.mem_map(0x8000, 0x1000)
                machine.mem_write(0x8000, path.read_bytes())
                machine.reg_write(x86_const.UC_X86_REG_EDI, value)
                machine.emu_start(0x8000, 0x8000 + len(code), timeout=100000, count=50)
                values.append(machine.reg_read(x86_const.UC_X86_REG_EAX))
            outputs.append(values)
        check(outputs == [[1, 7, 1], [1, 1, 7], [1, 7, 1]], f"Conditional behavior mismatch: {outputs}")
        check(after.read_bytes()[-4:] == b"NEW\0" and sha(back) == sha(original), "Data/rollback failure")
        padded = self.cli("assemble", "--arch", "x86_64", "--va", "0x8000", "--asm", "jmp 0x8010", "--pad-to", 5, assembler=True)
        check(padded["hex"] == "eb0e909090", "Address-specific jump or NOP padding failure")
        self.record("conditional branch behavior and multi-hunk code/data patch", inputs=[6, 7, 8],
                    before=outputs[0], after=outputs[1], restored=outputs[2], padded_jump=padded["hex"])

    def format_mapping(self):
        # Deliberately constructed mapping fixtures, not OS-loadability tests.
        code = bytes.fromhex("b807000000c3")
        elf_ident = b"\x7fELF\x02\x01\x01" + bytes(9)
        elf_header = elf_ident + struct.pack("<HHIQQQIHHHHHH", 2, 62, 1, 0x401000, 64, 0, 0, 64, 56, 1, 0, 0, 0)
        elf_load = struct.pack("<IIQQQQQQ", 1, 5, 0, 0x400000, 0x400000, 0x1006, 0x3000, 0x1000)
        elf = (elf_header + elf_load).ljust(0x1000, b"\0") + code
        macho_header = struct.pack("<IIIIIIII", 0xFEEDFACF, 0x01000007, 3, 2, 1, 72, 0, 0)
        macho_load = struct.pack("<II16sQQQQIIII", 0x19, 72, b"__TEXT", 0x100000000, 0x3000, 0, 0x1006, 7, 5, 0, 0)
        macho = (macho_header + macho_load).ljust(0x1000, b"\0") + code
        for label, data, base in (("elf", elf, 0x400000), ("macho", macho, 0x100000000)):
            original = self.out / f"mapping-{label}.bin"
            original.write_bytes(data)
            info = self.cli("info", original)
            mapped = self.cli("map", original, "--va", hex(base + 0x1000), "--size", 5)
            check(mapped["offset"] == 0x1000, "VA mapping mismatch")
            reverse = self.cli("map", original, "--offset", "0x1000", "--size", 5)
            check(reverse["va"] == base + 0x1000, "Offset mapping mismatch")
            self.cli("map", original, "--va", hex(base + len(data)), "--size", 1, expected=2)
            spec = dict(format="auto", arch="x86_64", patches=[dict(kind="code", offset=0x1000,
                        anchor_offset=0x1000, before="b807000000", after="b82a000000", reason="Format mapping experiment")])
            plan = self.out / f"mapping-{label}-plan.json"
            after = self.out / f"mapping-{label}-patched.bin"
            back = self.out / f"mapping-{label}-restored.bin"
            self.cli("plan", original, "--spec", self.spec(f"mapping-{label}-spec.json", spec), "--out", plan)
            self.cli("apply", original, "--plan", plan, "--out", after)
            self.cli("verify", after, "--plan", plan)
            self.cli("revert", after, "--plan", plan, "--out", back)
            values = []
            for path in (original, after, back):
                machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_64)
                machine.mem_map(base, 0x3000)
                machine.mem_write(base, path.read_bytes())
                machine.emu_start(base + 0x1000, base + 0x1005, timeout=100000, count=1)
                values.append(machine.reg_read(x86_const.UC_X86_REG_EAX))
            check(values == [7, 42, 7] and sha(back) == sha(original), "Mapped patch execution failed")
            self.record(f"{info['format']} mapping fixture: parse/patch/revert and CPU emulation",
                        register_values=values, bss_mapping_rejected=True, native_loader_tested=False)
        fat = self.out / "unsupported-fat.bin"
        fat.write_bytes(bytes.fromhex("cafebabe00000002") + bytes(64))
        self.cli("info", fat, expected=2)
        # Overlap only part of the requested span, not the whole span.
        overlap = bytearray(elf)
        struct.pack_into("<H", overlap, 56, 2)
        overlap[120:176] = struct.pack("<IIQQQQQQ", 1, 5, 0x1002, 0x401002, 0x401002, 2, 2, 1)
        ambiguous = self.out / "ambiguous-elf.bin"
        ambiguous.write_bytes(overlap)
        self.cli("map", ambiguous, "--offset", "0x1000", "--size", 5, expected=2)
        self.record("format refusal paths", cases=["BSS has no file bytes", "fat Mach-O needs slice selection", "partial mapping overlap"])

    def finish(self):
        doctor = self.cli("doctor")
        summary = dict(host=platform.platform(), doctor=doctor, results=self.results,
                       cli_sha256=sha(CLI), experiment_sha256=sha(Path(__file__)),
                       fixture_source_sha256=sha(PLUGIN / "experiments/fixture.c"),
                       note="Native PE execution and CPU emulation are distinct. No untrusted samples executed.")
        (self.out / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
        print(f"Evidence: {self.out}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True, help="New evidence directory (must not exist)")
    parser.add_argument("--emulation-only", action="store_true")
    args = parser.parse_args()
    check(shutil.which("uv"), "uv must be available")
    experiment = Experiment(args.out)
    if not args.emulation_only:
        experiment.native_pe()
    experiment.raw_emulation()
    experiment.branch_and_data()
    experiment.format_mapping()
    experiment.finish()


if __name__ == "__main__":
    main()

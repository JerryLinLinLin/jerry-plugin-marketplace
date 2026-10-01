"""Build pinned plugins and assemble a runtime-only, relocatable Windows bundle."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / 'build'
LOCK = json.loads((ROOT / 'bundle.lock.json').read_text(encoding='utf-8-sig'))
PREFIX = BUILD / 'rizin'
LOGS = BUILD / 'logs'


def run(name, args, cwd=ROOT):
    LOGS.mkdir(parents=True, exist_ok=True)
    print(name, flush=True)
    with (LOGS / f'{name}.log').open('w', encoding='utf-8') as log:
        result = subprocess.run([str(x) for x in args], cwd=cwd, stdout=log, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f'{name} failed ({result.returncode}); see {LOGS / (name + ".log")}')


def download(key):
    item = LOCK['downloads'][key]
    path = BUILD / 'downloads' / item['file']
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        run('download-' + key, ['curl.exe', '-fL', '--retry', '3', item['url'], '-o', path])
    with path.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    if digest != item['sha256']:
        raise RuntimeError(f'Checksum mismatch for {path}; expected {item["sha256"]}, found {digest}')
    return path


def extract_zip(archive, destination):
    destination.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as source:
        for member in source.infolist():
            if not (destination / member.filename).resolve().is_relative_to(destination.resolve()):
                raise RuntimeError(f'Unsafe archive path: {member.filename}')
        source.extractall(destination)


def copy_tree(source, target):
    shutil.copytree(source, target, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns('.git', '.github', '__pycache__', '*.pdb', '*.exp', '*.lib'))


def prepare():
    run('submodules', ['git', 'submodule', 'update', '--init', '--depth', '1'])
    for name, component in LOCK['components'].items():
        source = ROOT / 'sources' / name
        actual = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
        if actual != component['commit']:
            raise RuntimeError(f'{name}: expected {component["commit"]}, got {actual}')
        if component['patch']:
            patch = ROOT / 'patches' / component['patch']
            check = subprocess.run(['git', '-C', str(source), 'apply', '--reverse', '--check', str(patch)], capture_output=True)
            if check.returncode:
                run('patch-' + name, ['git', 'apply', '--check', patch], source)
                run('apply-' + name, ['git', 'apply', patch], source)
    run('ghidra-submodules', ['git', 'submodule', 'update', '--init', '--depth', '1'], ROOT / 'sources/rz-ghidra')
    # YARA's fuzz corpus contains antivirus-sensitive samples. Build only libyara.
    yara = ROOT / 'sources/rz-libyara/subprojects/yara'
    if not (yara / '.git').exists():
        run('yara-clone', ['git', 'clone', '--filter=blob:none', '--no-checkout', '--depth', '1',
                          '--branch', 'v4.5.8', 'https://github.com/VirusTotal/yara.git', yara])
        run('yara-sparse-init', ['git', 'sparse-checkout', 'init', '--cone'], yara)
        run('yara-sparse-source', ['git', 'sparse-checkout', 'set', 'libyara'], yara)
        run('yara-checkout', ['git', 'checkout', '--detach', LOCK['dependencies']['yara']['commit']], yara)
    actual = subprocess.check_output(['git', '-C', str(yara), 'rev-parse', 'HEAD'], text=True).strip()
    if actual != LOCK['dependencies']['yara']['commit']:
        raise RuntimeError('Unexpected YARA source revision')
    for path in (ROOT / 'sources/rz-libyara/subprojects/packagefiles/yara').iterdir():
        shutil.copy2(path, yara / path.name)
    inputs = {key: download(key) for key in LOCK['downloads']}
    if not PREFIX.exists():
        extract_zip(inputs['rizin'], BUILD / 'upstream')
        shutil.copytree(BUILD / 'upstream/rizin-win-installer-clang_cl-64', PREFIX)
    for key, directory in [('retdecSdk', 'previous'), ('openssl3', 'openssl-3.5.9'), ('openssl11', 'openssl-1.1.1w')]:
        destination = BUILD / directory
        if not destination.exists():
            extract_zip(inputs[key], destination)
    zlib = BUILD / 'deps/zlib-1.3.2'
    if not zlib.exists():
        zlib.parent.mkdir(parents=True, exist_ok=True)
        with tarfile.open(inputs['zlib']) as source:
            source.extractall(zlib.parent, filter='data')
    for path in (PREFIX / 'lib/pkgconfig').glob('*.pc'):
        lines = path.read_text().splitlines()
        path.write_text('\n'.join('prefix=${pcfiledir}/../..' if x.startswith('prefix=') else x for x in lines) + '\n')


def build(jobs):
    def cmake(name, source, options):
        target = BUILD / name
        run(name + '-configure', ['cmake', '--fresh', '-S', source, '-B', target, '-G', 'Ninja', '-DCMAKE_BUILD_TYPE=Release', *options])
        run(name + '-build', ['cmake', '--build', target, '-j', str(jobs)])
        run(name + '-install', ['cmake', '--install', target])
    cmake('zlib', BUILD / 'deps/zlib-1.3.2', [f'-DCMAKE_INSTALL_PREFIX={(BUILD / "zlib-install").as_posix()}', '-DZLIB_BUILD_TESTING=OFF'])
    cmake('rz-ghidra', ROOT / 'sources/rz-ghidra', [
        f'-DCMAKE_PREFIX_PATH={PREFIX.as_posix()};{(BUILD / "zlib-install").as_posix()}',
        f'-DCMAKE_INSTALL_PREFIX={PREFIX.as_posix()}',
        f'-DRIZIN_INSTALL_PLUGDIR={(PREFIX / "lib/rizin/plugins").as_posix()}',
        '-DZLIB_USE_STATIC_LIBS=ON', '-DBUILD_CUTTER_PLUGIN=OFF'])
    openssl11 = BUILD / 'openssl-1.1.1w/openssl-1.1/x64'
    cmake('rz-retdec', ROOT / 'sources/rz-retdec', [
        f'-DCMAKE_PREFIX_PATH={PREFIX.as_posix()};{(BUILD / "previous/rizin").as_posix()};{openssl11.as_posix()}',
        f'-DCMAKE_INSTALL_PREFIX={PREFIX.as_posix()}', f'-DOPENSSL_ROOT_DIR={openssl11.as_posix()}',
        '-DBUILD_BUNDLED_RETDEC=OFF', '-DBUILD_CUTTER_PLUGIN=OFF', '-DRZ_RETDEC_DOC=OFF'])
    os.environ['CMAKE_PREFIX_PATH'] = str(PREFIX) + ';' + str(BUILD / 'openssl-3.5.9/x64')
    for name, options in [('jsdec', ['-Dbuild_type=rizin']), ('rz-libyara', ['-Duse_sys_yara=disabled', '-Denable_openssl=true'])]:
        target = BUILD / name
        command = ['meson', 'setup', target, ROOT / 'sources' / name,
                   '--backend=ninja', '--buildtype=release', '-Db_vscrt=md',
                   f'--prefix={PREFIX.as_posix()}', f'-Drizin_plugdir={(PREFIX / "lib/rizin/plugins").as_posix()}', *options]
        if (target / 'meson-private/coredata.dat').exists():
            command.append('--reconfigure')
        run(name + '-configure', command)
        run(name + '-build', ['meson', 'compile', '-C', target, '-j', str(jobs)])
        run(name + '-install', ['meson', 'install', '-C', target, '--no-rebuild'])
    shutil.copy2(PREFIX / 'lib/rizin/plugins/core_ghidra.dll', PREFIX / 'bin/core_ghidra.dll')
    for source, names in [(openssl11 / 'bin', ['libcrypto-1_1-x64.dll', 'libssl-1_1-x64.dll']),
                          (BUILD / 'openssl-3.5.9/x64/bin', ['libcrypto-3-x64.dll', 'libssl-3-x64.dll'])]:
        for name in names:
            shutil.copy2(source / name, PREFIX / 'bin' / name)
    redist = Path(os.environ['VCToolsRedistDir']) / 'x64'
    crt = next(redist.glob('Microsoft.VC*.CRT'))
    for path in crt.glob('*.dll'):
        shutil.copy2(path, PREFIX / 'bin' / path.name)
    copy_tree(BUILD / 'previous/rizin/lib/rizin/plugins/support', PREFIX / 'lib/rizin/plugins/support')
    copy_tree(ROOT / 'sources/sigdb', PREFIX / 'share/sigdb')


def assemble():
    # Refuse to merge with an older runtime: this keeps deleted files out of releases.
    target = BUILD / 'portable/rizin'
    if target.exists():
        raise RuntimeError(f'{target} already exists. Archive or remove that generated directory before reassembling.')
    (target / 'bin').mkdir(parents=True)
    for path in (PREFIX / 'bin').iterdir():
        if path.suffix.lower() in ('.exe', '.dll'):
            shutil.copy2(path, target / 'bin' / path.name)
    # Rizin and dependency libraries pass UTF-8 paths through Win32 A APIs.
    # Activate UTF-8 for each CLI process; no machine locale setting is changed.
    for path in (target / 'bin').glob('*.exe'):
        run('manifest-' + path.stem, ['mt.exe', '-nologo', '-manifest', ROOT / 'scripts/windows-utf8.manifest',
                                    f'-outputresource:{path};#1'])
    copy_tree(PREFIX / 'lib/rizin/plugins', target / 'lib/rizin/plugins')
    copy_tree(PREFIX / 'share', target / 'share')
    licenses = target / 'licenses'
    for name in LOCK['components']:
        source = ROOT / 'sources' / name
        for path in source.iterdir():
            if path.name.upper().startswith(('LICENSE', 'COPYING', 'NOTICE')):
                dest = licenses / name / path.name
                dest.parent.mkdir(parents=True, exist_ok=True)
                copy_tree(path, dest) if path.is_dir() else shutil.copy2(path, dest)
    notices = [
        (ROOT / 'sources/rz-ghidra/ghidra/ghidra/LICENSE', 'ghidra/LICENSE'),
        (ROOT / 'sources/rz-ghidra/ghidra/ghidra/NOTICE', 'ghidra/NOTICE'),
        (ROOT / 'sources/rz-ghidra/third-party/pugixml/LICENSE.md', 'pugixml/LICENSE.md'),
        (ROOT / 'sources/jsdec/subprojects/libquickjs/LICENSE', 'quickjs/LICENSE'),
        (ROOT / 'sources/rz-libyara/subprojects/yara/COPYING', 'yara/COPYING'),
        (ROOT / 'sources/sigdb/README.md', 'sigdb/README.md'),
        (BUILD / 'openssl-3.5.9/LICENSE.txt', 'openssl-3.5.9/LICENSE.txt'),
        (BUILD / 'openssl-1.1.1w/openssl-1.1/LICENSE', 'openssl-1.1.1w/LICENSE'),
        (BUILD / 'deps/zlib-1.3.2/LICENSE', 'zlib/LICENSE'),
    ]
    for path in (BUILD / 'previous/rizin/share/retdec').glob('LICENSE*'):
        notices.append((path, 'retdec/' + path.name))
    for source, relative in notices:
        dest = licenses / relative
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, dest)
    manifest = dict(LOCK)
    manifest['compiler'] = os.environ.get('VCToolsVersion', '').strip()
    manifest['windowsManifest'] = {'file': 'scripts/windows-utf8.manifest', 'sha256': hashlib.sha256((ROOT / 'scripts/windows-utf8.manifest').read_bytes()).hexdigest(), 'minimumWindows': 'Windows 10 version 1903 (x64)'}
    manifest['patches'] = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (ROOT / 'patches').glob('*.patch')}
    (target / 'bundle-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    copy_tree(ROOT / 'plugins/rizin-windows-re/skills', target / 'skills')
    shutil.copy2(ROOT / 'plugins/rizin-windows-re/README.md', target / 'README.md')
    shutil.copy2(ROOT / 'docs/third-party.md', target / 'THIRD-PARTY.md')
    print(f'Portable runtime: {target}', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--assemble-only', action='store_true')
    parser.add_argument('--jobs', type=int, default=8)
    args = parser.parse_args()
    if not args.assemble_only:
        prepare()
        build(args.jobs)
    assemble()

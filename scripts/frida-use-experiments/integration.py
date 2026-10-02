"""Real Windows process experiments: no mocked Frida APIs or simulated hooks."""
import argparse
import concurrent.futures
import hashlib
import json
from pathlib import Path
import socket
import subprocess
import sys
import threading
import time
import urllib.request

import frida

HERE = Path(__file__).resolve().parent
RUNNER = HERE.parents[1] / 'plugins/frida-use/skills/frida-use/scripts/frida_session.py'
PAYLOAD = bytes([0, 1, 2, 127, 128, 254, 255]) + b'Frida'


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def events(path):
    return [json.loads(line) for line in (path / 'events.jsonl').read_text(encoding='utf-8').splitlines()]


def run_capture(out, *args, expected=0):
    command = [sys.executable, str(RUNNER), '--out', str(out), *map(str, args)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=35)
    (out.parent / (out.name + '-controller.txt')).write_text(result.stdout + result.stderr, encoding='utf-8')
    require(result.returncode == expected, f'{out.name}: exit {result.returncode}, wanted {expected}\n{result.stdout}{result.stderr}')
    return events(out)


def not_running(device, pid):
    return all(p.pid != pid for p in device.enumerate_processes())


def inspector_probe(script):
    from websockets.sync.client import connect
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    script.enable_debugger(port)
    with urllib.request.urlopen(f'http://127.0.0.1:{port}/json/list', timeout=5) as response:
        targets = json.load(response)
    require(targets, 'Inspector exposes a target')
    with connect(targets[0]['webSocketDebuggerUrl'], open_timeout=5) as ws:
        ws.send(json.dumps({'id': 1, 'method': 'Debugger.enable'}))
        while json.loads(ws.recv(timeout=5)).get('id') != 1:
            pass
        with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
            future = pool.submit(script.exports_sync.debug_probe)
            paused = None
            deadline = time.monotonic() + 5
            try:
                while time.monotonic() < deadline:
                    event = json.loads(ws.recv(timeout=5))
                    if event.get('method') == 'Debugger.paused':
                        paused = event
                        break
            finally:
                ws.send(json.dumps({'id': 2, 'method': 'Debugger.resume'}))
            answer = future.result(timeout=5)
        require(paused is not None and answer == 42, 'V8 JS breakpoint and resume')
    script.disable_debugger()
    return {'paused': True, 'result': answer, 'top_frame': paused['params']['callFrames'][0]['functionName']}


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument('--fixtures', type=Path, required=True)
    cli.add_argument('--out', type=Path, required=True)
    options = cli.parse_args()
    output = options.out.resolve()
    output.mkdir(parents=True, exist_ok=False)
    device = frida.get_local_device()
    results = []
    source = (HERE / 'integration-agent.js').read_text(encoding='utf-8')
    for arch in ('x64', 'x86'):
        exe = (options.fixtures / arch / 'fixture.exe').resolve()
        folder = output / arch
        folder.mkdir()
        target_file = folder / '\u6d4b\u8bd5 file.bin'
        for runtime in ('qjs', 'v8'):
            label = f'{arch}-{runtime}'
            captured = run_capture(folder / runtime, '--spawn', exe, '--duration', 0.6,
                '--runtime', runtime, '--kill-on-exit', '--', target_file, '8')
            file_events = [e['message']['payload'] for e in captured if e.get('message', {}).get('payload', {}).get('type') == 'file-open']
            require(any(e['path'] == str(target_file) and e['success'] for e in file_events), label + ' Unicode file hook')
            require(sum(e.get('message', {}).get('payload', {}).get('type') == 'hook-installed' for e in captured) == 1,
                    label + ' one file API boundary, no wrapper duplication')
            pid = next(e['pid'] for e in captured if e['kind'] == 'spawned')
            require(not_running(device, pid), label + ' owned spawn cleanup')
            results.append({'case': label + '-runner', 'passed': True, 'file_events': len(file_events)})

            proc = subprocess.Popen([str(exe), str(target_file), '15'], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                    creationflags=subprocess.CREATE_NO_WINDOW)
            session = script = None
            messages = []
            ready = threading.Event()
            def on_message(message, data):
                messages.append((message, data))
                if message.get('payload', {}).get('type') == 'ready':
                    ready.set()
            try:
                session = device.attach(proc.pid)
                script = session.create_script(source, name='integration-agent', runtime=runtime)
                script.on('message', on_message)
                script.load()
                require(ready.wait(5), label + ' ready')
                exercise = script.exports_sync.exercise()
                require([exercise[k] for k in ('baseline', 'modified', 'restored', 'replaced', 'reverted')] == [12, 99, 12, 105, 12], label + ' hook/replace/rollback')
                require([exercise['bytePatch'][k] for k in ('before', 'patched', 'after')] == [7, 42, 7], label + ' machine-code patch/rollback')
                require(exercise['winerror']['value'] == 0 and exercise['winerror']['lastError'] == 1234, label + ' Windows ABI/lastError')
                for expected_loads in (1, 2):
                    late = script.exports_sync.load_late(str(exe.parent / 'late.dll'))
                    require(late == {'answer': 18, 'lateLoads': expected_loads}, label + ' delayed DLL and reload')
                inspector = inspector_probe(script) if runtime == 'v8' else None
                deadline = time.monotonic() + 5
                while not any(blob == PAYLOAD for _, blob in messages) and time.monotonic() < deadline:
                    time.sleep(0.05)
                require(any(blob == PAYLOAD for _, blob in messages), label + ' binary ReadFile transfer')
                require(not any(m.get('type') == 'error' for m, _ in messages), label + ' no JS errors')
                script.exports_sync.dispose()
                script.unload()
                script = None
                session.detach()
                session = None
                require(proc.poll() is None, label + ' attached target survives detach')
                results.append({'case': label + '-live-api', 'passed': True, 'exercise': exercise, 'inspector': inspector,
                                'binary_sha256': hashlib.sha256(PAYLOAD).hexdigest()})
            finally:
                if script is not None:
                    try: script.unload()
                    except frida.InvalidOperationError: pass
                if session is not None:
                    try: session.detach()
                    except frida.InvalidOperationError: pass
                proc.terminate()
                proc.communicate(timeout=5)

        proc = subprocess.Popen([str(exe), str(target_file), '10'], creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            captured = run_capture(folder / 'attach', '--pid', proc.pid, '--duration', 0.4)
            require(proc.poll() is None, arch + ' runner leaves attach PID alive')
            require(captured[-1]['exit_code'] == 0, arch + ' attach capture success')
        finally:
            proc.terminate()
            proc.wait(timeout=5)
        results.append({'case': arch + '-runner-attach', 'passed': True})

        capture_path = folder / 'binary'
        captured = run_capture(capture_path, '--spawn', exe, '--script', HERE / 'integration-agent.js',
                               '--duration', 0.5, '--kill-on-exit', '--', target_file, '8')
        attachments = [e['binary'] for e in captured if 'binary' in e]
        require(attachments, arch + ' runner persists binary attachments')
        for attachment in attachments:
            raw = (capture_path / attachment['file']).read_bytes()
            require(raw == PAYLOAD and hashlib.sha256(raw).hexdigest() == attachment['sha256'], arch + ' saved binary integrity')
        results.append({'case': arch + '-runner-binary', 'passed': True, 'attachments': len(attachments)})

        captured = run_capture(folder / 'natural-exit', '--spawn', exe, '--duration', 3, '--', target_file, '1')
        require(any(e['kind'] == 'detached' and e['reason'] == 'process-terminated' for e in captured), arch + ' natural exit')
        results.append({'case': arch + '-natural-exit', 'passed': True})

        captured = run_capture(folder / 'native-crash', '--spawn', exe, '--duration', 3,
                               '--kill-on-exit', '--', target_file, '1', 'crash', expected=1)
        require(any(e['kind'] == 'target_exit' and e['windows_exit_code'] == 0xE0424242 for e in captured),
                arch + ' native exception exit status even without Frida crash object')
        results.append({'case': arch + '-native-crash', 'passed': True})

        for label, js in [('js-error', "throw new Error('intentional startup failure');"),
                          ('syntax-error', 'const = ;'), ('no-ready', 'rpc.exports = {};')]:
            agent = folder / (label + '.js')
            agent.write_text(js, encoding='utf-8')
            captured = run_capture(folder / label, '--spawn', exe, '--script', agent, '--ready-timeout', 0.4,
                                   '--duration', 0.4, '--', target_file, '8', expected=1)
            pid = next(e['pid'] for e in captured if e['kind'] == 'spawned')
            require(not any(e['kind'] == 'resumed' for e in captured), label + ' no unintended resume')
            require(not_running(device, pid), label + ' no suspended orphan')
            results.append({'case': arch + '-' + label, 'passed': True})

    exe = (options.fixtures / 'x64/fixture.exe').resolve()
    for label, js, expected in [
        ('bounded-binary', "send({type: 'large'}, new Uint8Array(2 * 1024 * 1024).buffer); send({type: 'ready'});", 0),
        ('queue-pressure', "for (let i = 0; i < 5000; i++) send({type: 'burst', i}); send({type: 'ready'});", 2),
        ('async-js-error', "send({type: 'ready'}); setTimeout(() => { throw new Error('intentional async error'); }, 100);", 1),
    ]:
        agent = output / (label + '.js')
        agent.write_text(js, encoding='utf-8')
        captured = run_capture(output / label, '--spawn', exe, '--script', agent, '--duration', 0.5,
                               '--kill-on-exit', '--', output / 'stress.bin', '8', expected=expected)
        if label == 'bounded-binary':
            attachment = next(e for e in captured if 'binary' in e)
            require(attachment['original_binary_size'] == 2 * 1024 * 1024 and attachment['binary']['size'] == 1024 * 1024
                    and attachment['binary']['truncated'], 'binary cap and truncation metadata')
        if label == 'queue-pressure':
            require(captured[-1]['dropped_events'] > 0, 'queue overflow is observable')
        results.append({'case': label, 'passed': True})

    (output / 'results.json').write_text(json.dumps({'frida': frida.__version__, 'python': sys.version, 'results': results}, indent=2), encoding='utf-8')
    print(json.dumps({'passed': len(results), 'evidence': str(output)}, indent=2))


if __name__ == '__main__':
    main()

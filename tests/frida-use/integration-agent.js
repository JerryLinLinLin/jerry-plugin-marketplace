'use strict';
// Only used against tests/fixture.c, never a third-party application.
const fixture = Process.mainModule;
function exported(name) {
  const entry = fixture.enumerateExports().find(e => e.name.replace(/^_/, '').split('@')[0] === name);
  if (!entry) throw new Error('Missing fixture export ' + name);
  return entry.address;
}
const scoreAddress = exported('demo_score');
const score = new NativeFunction(scoreAddress, 'int', ['int'], Process.arch === 'ia32' ? 'mscdecl' : 'win64');
const fail = new SystemFunction(exported('demo_fail'), 'int', [], Process.arch === 'ia32' ? 'stdcall' : 'win64');
const listeners = [];
let reads = 0;
listeners.push(Interceptor.attach(Process.getModuleByName('kernel32.dll').getExportByName('ReadFile'), {
  onEnter(args) { this.buffer = args[1]; this.count = args[3]; this.overlapped = args[4]; },
  onLeave(result) {
    if (result.toInt32() === 0 || !this.overlapped.isNull() || this.count.isNull() || reads++ >= 5) return;
    const size = Math.min(this.count.readU32(), 64);
    send({type: 'read', size: size, tid: this.threadId}, this.buffer.readByteArray(size));
  }
}));
let lateLoads = 0;
const lateHooks = new Map();
const observer = Process.attachModuleObserver({
  onAdded(module) {
    if (module.name.toLowerCase() !== 'late.dll') return;
    const entry = module.enumerateExports().find(e => e.name.replace(/^_/, '') === 'late_value');
    const listener = Interceptor.attach(entry.address, {onEnter(args) { send({type: 'late-call', value: args[0].toInt32()}); }});
    lateHooks.set(module.base.toString(), listener);
    lateLoads++;
  },
  onRemoved(module) { lateHooks.delete(module.base.toString()); }
});
rpc.exports = {
  exercise() {
    const baseline = score(5);
    const hook = Interceptor.attach(scoreAddress, {onLeave(value) { value.replace(99); }});
    Interceptor.flush();
    const modified = score(5);
    hook.detach();
    Interceptor.flush();
    const restored = score(5);
    const replacement = new NativeCallback(value => value + 100, 'int', ['int'], Process.arch === 'ia32' ? 'mscdecl' : 'win64');
    Interceptor.replace(scoreAddress, replacement);
    Interceptor.flush();
    const replaced = score(5);
    Interceptor.revert(scoreAddress);
    Interceptor.flush();
    const reverted = score(5);
    // A private, never-concurrently-executed code page: mov eax, imm32; ret.
    const page = Memory.alloc(Process.pageSize);
    const initial = [0xb8, 7, 0, 0, 0, 0xc3];
    page.writeByteArray(initial);
    Memory.protect(page, Process.pageSize, 'r-x');
    const toy = new NativeFunction(page, 'int', []);
    const beforeBytes = Array.from(new Uint8Array(page.readByteArray(6)));
    const before = toy();
    Memory.patchCode(page, 6, writable => writable.writeByteArray([0xb8, 42, 0, 0, 0, 0xc3]));
    const patched = toy();
    Memory.patchCode(page, 6, writable => writable.writeByteArray(beforeBytes));
    const after = toy();
    const winerror = fail();
    return {arch: Process.arch, baseline, modified, restored, replaced, reverted,
      bytePatch: {before, patched, after, bytes: Array.from(new Uint8Array(page.readByteArray(6)))}, winerror};
  },
  loadLate(path) {
    const k32 = Process.getModuleByName('kernel32.dll');
    const abi = Process.arch === 'ia32' ? 'stdcall' : 'win64';
    const load = new NativeFunction(k32.getExportByName('LoadLibraryW'), 'pointer', ['pointer'], abi);
    const free = new NativeFunction(k32.getExportByName('FreeLibrary'), 'int', ['pointer'], abi);
    const handle = load(Memory.allocUtf16String(path));
    if (handle.isNull()) throw new Error('LoadLibraryW failed');
    const module = Process.getModuleByAddress(handle);
    const entry = module.enumerateExports().find(e => e.name.replace(/^_/, '') === 'late_value');
    const invoke = new NativeFunction(entry.address, 'int', ['int'], Process.arch === 'ia32' ? 'mscdecl' : 'win64');
    const answer = invoke(9);
    free(handle);
    return {answer, lateLoads};
  },
  debugProbe() { const value = 41; debugger; return value + 1; },
  dispose() {
    observer.detach();
    for (const listener of listeners) listener.detach();
    for (const listener of lateHooks.values()) listener.detach();
    return true;
  }
};
send({type: 'ready', arch: Process.arch});

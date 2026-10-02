'use strict';

// Bounded observation example: no target arguments or return values are changed.
if (Process.platform !== 'windows') throw new Error('This agent expects Windows');

const limit = 200;
let seen = 0;
const hooks = new Map();

function wide(pointer) {
  if (pointer.isNull()) return null;
  try { return pointer.readUtf16String(512); }
  catch (error) { return '<unreadable: ' + error.message + '>'; }
}

const observer = Process.attachModuleObserver({
  onAdded(module) {
    if (!['kernel32.dll', 'kernelbase.dll'].includes(module.name.toLowerCase())) return;
    // Prefer one API boundary: a kernel32 wrapper and its kernelbase target can
    // have different addresses yet represent the same logical file-open call.
    if (module.name.toLowerCase() === 'kernel32.dll' && Process.findModuleByName('kernelbase.dll') !== null) return;
    const address = module.findExportByName('CreateFileW');
    if (address === null || hooks.has(address.toString())) return;
    const listener = Interceptor.attach(address, {
      onEnter(args) {
        this.capture = seen < limit;
        if (!this.capture) return;
        this.captureNumber = ++seen;
        this.path = wide(args[0]);
        this.caller = this.returnAddress.toString();
        this.flags = args[5].toUInt32();
      },
      onLeave(retval) {
        if (!this.capture) return;
        send({type: 'file-open', pid: Process.id, tid: this.threadId,
          path: this.path, pathMaxCharacters: 512, flags: this.flags, caller: this.caller,
          handle: retval.toString(), success: !retval.equals(ptr(-1)),
          lastError: retval.equals(ptr(-1)) ? this.lastError : null});
        if (this.captureNumber === limit) send({type: 'capture-limit', limit: limit});
      }
    });
    hooks.set(address.toString(), {listener: listener, owner: Process.findModuleByAddress(address)?.base.toString()});
    send({type: 'hook-installed', module: module.name, address: address.toString()});
  },
  onRemoved(module) {
    for (const [key, value] of hooks) {
      if (value.owner === module.base.toString()) {
        // Current Frida discards hooks on module unload. Drop our stale handle.
        hooks.delete(key);
      }
    }
  }
});

rpc.exports = {
  status() { return {pid: Process.id, seen: seen, hooks: hooks.size}; },
  dispose() {
    observer.detach();
    for (const value of hooks.values()) value.listener.detach();
    hooks.clear();
    return {seen: seen};
  }
};

if (hooks.size === 0) throw new Error('CreateFileW was not resolved');
send({type: 'ready', pid: Process.id, arch: Process.arch, pointerSize: Process.pointerSize,
  frida: Frida.version, runtime: Script.runtime,
  modules: Process.enumerateModules().map(m => ({name: m.name, path: m.path, base: m.base.toString(), size: m.size}))});

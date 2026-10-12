# VM operations

Inventory uses local Hyper-V, not network discovery. Exact names resolve to stable GUIDs; ambiguous names fail. Creation leaves VMs Off. CPU/memory, media, firmware and serial-port changes require Off. Export plus import with a new ID supports cloning; specify separate VM/VHD destination directories.

Use graceful `shutdown` or `restart` for ordinary work. `restart` waits for Off before Start. `turn_off` and `reset` are hard power operations, used only when the task calls for them. No automatic fallback from graceful to hard power. Pause/resume and save are distinct operations. Reopen a console after a power transition or checkpoint restore if the transport disconnected.

Before experiments that require rollback, record the current VM state and checkpoint IDs, then create a named task checkpoint. Restore/delete by ID. `current` means the VM's parent checkpoint, not the newest timestamp anywhere in its branching tree. Restoring replaces guest state and discards subsequent changes; save needed output to the host first. Delete only the task's checkpoints unless broader cleanup was requested. At the end, verify the intended power state and checkpoint tree.

`vm_remove` unregisters an Off VM by GUID; Hyper-V may merge its checkpoints, but standalone disk files are not deleted. Disk detach likewise leaves its file. Switch removal can affect other VMs; inspect connections and keep it within the requested scope.

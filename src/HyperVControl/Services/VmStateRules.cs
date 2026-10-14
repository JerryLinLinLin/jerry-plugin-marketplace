using System.Text;

namespace HyperVControl.Services;

/// <summary>
/// Which VM states allow each setting, from the verified Hyper-V state matrix. Off allows everything;
/// Running allows the live-changeable settings, some only while a condition holds; Saved allows only
/// rename, removal, export and checkpoints; Paused, transitional and critical states allow nothing.
/// <see cref="Gate"/> renders one in-script check that refuses a whole request before any change and
/// names every setting that needs the VM Off, so a call is all-or-nothing.
/// </summary>
internal static class VmStateRules
{
    [Flags]
    internal enum States { None = 0, Off = 1, Running = 2, Saved = 4 }

    /// <summary>
    /// A PowerShell condition that must also hold while the VM is Running. It may read <c>$vm</c> and
    /// variables bound earlier in the script; <c>{value}</c> is replaced by the requested value.
    /// </summary>
    internal sealed record Condition(string Expression, string Explanation);

    internal sealed record Rule(States Allowed, string Description, Condition? WhileRunning = null)
    {
        public bool Allows(States state) => (Allowed & state) != 0;
    }

    private const States Live = States.Off | States.Running;
    private static readonly Condition Generation2 = new("[int]$vm.Generation -eq 2", "only on Generation 2");
    private static readonly Condition StaticMemory = new("-not $vm.DynamicMemoryEnabled", "only with static memory");
    private static readonly Condition DynamicMemory = new("[bool]$vm.DynamicMemoryEnabled", "only with dynamic memory on");

    /// <summary>The table, keyed by the tool parameter or action that requests the change.</summary>
    internal static readonly IReadOnlyDictionary<string, Rule> Table = new Dictionary<string, Rule>(StringComparer.Ordinal)
    {
        // hyperv_vm_configure
        ["processors"] = new(States.Off, "vCPU count; Hyper-V has no vCPU hot add"),
        ["memoryMb"] = new(Live, "startup memory", StaticMemory),
        ["dynamicMemory"] = new(States.Off, "dynamic memory on/off"),
        ["memoryMinMb"] = new(Live, "dynamic memory minimum", new("[bool]$vm.DynamicMemoryEnabled -and {value} -le [long]$vm.MemoryMinimum", "it can only decrease, with dynamic memory on")),
        ["memoryMaxMb"] = new(Live, "dynamic memory maximum", new("[bool]$vm.DynamicMemoryEnabled -and {value} -ge [long]$vm.MemoryMaximum", "it can only increase, with dynamic memory on")),
        ["memoryBufferPercent"] = new(Live, "dynamic memory buffer", DynamicMemory),
        ["checkpointType"] = new(Live, "checkpoint type"),
        ["autoCheckpoints"] = new(Live, "automatic checkpoints"),
        ["newName"] = new(States.Off | States.Running | States.Saved, "rename"),
        ["notes"] = new(Live, "notes"),
        ["autoStartAction"] = new(Live, "automatic start action"),
        ["autoStartDelaySeconds"] = new(Live, "automatic start delay"),
        ["autoStopAction"] = new(States.Off, "automatic stop action; sources conflict on live changes"),
        ["nestedVirtualization"] = new(States.Off, "nested virtualization"),
        ["integrationServices"] = new(Live, "integration services"),
        ["enhancedSessionTransport"] = new(States.Off, "enhanced session transport"),
        // hyperv_firmware (Generation 2)
        ["secureBoot"] = new(States.Off, "Secure Boot"),
        ["secureBootTemplate"] = new(States.Off, "Secure Boot template"),
        ["tpm"] = new(States.Off, "virtual TPM and key protector"),
        ["firstBootDevice"] = new(States.Off, "boot order"),
        // hyperv_network
        ["connect"] = new(Live, "connect adapter to a switch"),
        ["disconnect"] = new(Live, "disconnect adapter"),
        ["add_adapter"] = new(Live, "add network adapter", Generation2),
        ["remove_adapter"] = new(Live, "remove network adapter", Generation2),
        ["staticMac"] = new(States.Off, "MAC address"),
        ["macSpoofing"] = new(Live, "MAC address spoofing"),
        ["vlanId"] = new(Live, "VLAN"),
        // hyperv_disk
        ["attach_vhd"] = new(Live, "attach virtual disk (to SCSI while Running)"),
        ["detach_vhd"] = new(Live, "detach virtual disk", new("[string]$__drive.ControllerType -eq 'SCSI' -and -not $__bootDisk", "only a SCSI data disk, never the boot disk")),
        ["attach_iso"] = new(Live, "insert ISO", new("$__dvdCount -gt 0 -or [int]$vm.Generation -eq 2", "Generation 1 needs an existing DVD drive")),
        ["eject_iso"] = new(Live, "eject ISO"),
        ["resize_vhd"] = new(Live, "resize virtual disk", new("[string]$__drive.ControllerType -eq 'SCSI' -and [string]$__drive.Path -match '\\.vhdx$'", "only a VHDX on a SCSI controller")),
        // whole-VM operations
        ["comPort"] = new(Live, "COM port named pipe"),
        ["remove"] = new(States.Off | States.Saved, "remove VM"),
        ["clone"] = new(States.Off, "clone source"),
    };

    internal static Rule Get(string setting) =>
        Table.TryGetValue(setting, out var rule) ? rule : throw new ArgumentException($"No state rule for '{setting}'.");

    /// <summary>Maps a Hyper-V state name to the table's states. FastSaved counts as Saved; Paused,
    /// transitional and critical states map to <see cref="States.None"/>, which nothing allows.</summary>
    internal static States Parse(string state) => state switch
    {
        "Off" => States.Off,
        "Running" => States.Running,
        "Saved" or "FastSaved" => States.Saved,
        _ => States.None
    };

    /// <summary>State names a setting allows, in the form <c>Assert-ControlVmState</c> takes.</summary>
    internal static string[] StateNames(string setting) =>
        new[] { States.Off, States.Running, States.Saved }.Where(Get(setting).Allows).Select(s => s.ToString()).ToArray();

    /// <summary>The requested settings that <paramref name="state"/> rules out on its own. Running
    /// conditions depend on the VM and are evaluated in-script by <see cref="Gate"/>.</summary>
    internal static IReadOnlyList<string> Blocked(string state, IEnumerable<string> settings) =>
        settings.Where(setting => !Get(setting).Allows(Parse(state))).ToList();

    /// <summary>
    /// One script line that refuses the request with <c>InvalidVmState</c> before any change when the
    /// VM's current state rules out any requested setting, listing every such setting. Requested values
    /// feed value-dependent Running conditions (for example, a dynamic memory maximum may only grow).
    /// </summary>
    internal static string Gate(IEnumerable<(string Setting, PsValue? Value)> requested)
    {
        var rules = new List<string>();
        foreach (var (setting, value) in requested)
        {
            var rule = Get(setting);
            var label = rule.WhileRunning is null ? rule.Description : rule.Description + "; while Running " + rule.WhileRunning.Explanation;
            var entry = new StringBuilder("@{ setting = ").Append(PowerShellRunner.Literal(setting))
                .Append("; label = ").Append(PowerShellRunner.Literal(label))
                .Append("; states = @(").Append(string.Join(", ", StateNames(setting).Select(PowerShellRunner.Literal))).Append(')');
            if (rule.WhileRunning is not null)
                entry.Append("; when = { ").Append(rule.WhileRunning.Expression.Replace("{value}", value?.Text ?? "$null", StringComparison.Ordinal)).Append(" }");
            rules.Add(entry.Append(" }").ToString());
        }
        return rules.Count == 0 ? "" : "Assert-ControlSettings $vm @(" + string.Join(", ", rules) + ")";
    }
}

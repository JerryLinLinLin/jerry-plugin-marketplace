using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace HyperVControl.Services;

/// <summary>Disk media, firmware/vTPM and networking, each gated by <see cref="VmStateRules"/> before any change.</summary>
internal sealed partial class VmLifecycleService
{
    private static readonly string[] SecureBootTemplates = ["MicrosoftWindows", "MicrosoftUEFICertificateAuthority", "OpenSourceShieldedVM"];
    private static readonly string[] BootDevices = ["dvd", "disk", "network"];
    private const string MediaResult = "[ordered]@{ success = $true; disks = @(Get-VMHardDiskDrive -VM $vm | ForEach-Object { ConvertTo-ControlDrive $_ }); dvd = @(Get-VMDvdDrive -VM $vm | Select-Object Path,ControllerNumber,ControllerLocation) }";
    private const string AdapterResult = "[ordered]@{ success = $true; adapters = @(Get-VMNetworkAdapter -VM $vm | ForEach-Object { ConvertTo-ControlAdapter $_ }) }";

    /// <summary>Generation 2: whether <c>$__drive</c> is the disk the firmware boots first (SCSI 0:0 when the boot order names no disk).</summary>
    private const string BootDisk = """
        $__bootDisk = $false
        if ([int]$vm.Generation -eq 2) {
            $__bootEntry = @((Get-VMFirmware -VM $vm).BootOrder | Where-Object { [string]$_.BootType -eq 'Drive' -and $null -ne $_.Device -and [string]$_.Device.Path -match '\.a?vhdx?$' }) | Select-Object -First 1
            if ($null -ne $__bootEntry) { $__bootDisk = (Get-ControlPathKey ([string]$__bootEntry.Device.Path)) -eq (Get-ControlPathKey ([string]$__drive.Path)) }
            else { $__bootDisk = ([int]$__drive.ControllerNumber -eq 0 -and [int]$__drive.ControllerLocation -eq 0) }
        }
        """;

    private static string AttachedDrive(string literal) =>
        $"$__drives = @(Get-VMHardDiskDrive -VM $vm | Where-Object {{ $_.Path -eq {literal} }})\n"
        + "if ($__drives.Count -ne 1) { Stop-Control 'NotFound' 'The disk is not attached exactly once to this VM.' }\n"
        + "$__drive = $__drives[0]";

    /// <summary>Media changes: attach_vhd, detach_vhd, attach_iso, eject_iso, resize_vhd. Never deletes files.</summary>
    public Task<JsonElement> DiskAsync(string machine, string action, string? path, long? sizeGb, CancellationToken ct)
    {
        if (action is not "eject_iso" && path == null) throw new ArgumentException("path required.");
        if (action == "attach_iso" && Extension(path!) != ".iso") throw new ArgumentException("attach_iso needs an .iso file.");
        var literal = path == null ? "" : L(ControlService.FullPath(path));
        var s = Script().Vm(machine);
        switch (action)
        {
            case "attach_vhd":
                s.Line("$__path = " + literal)
                    .Line("if (-not [System.IO.File]::Exists($__path)) { Stop-Control 'NotFound' ('Virtual disk not found: ' + $__path) }")
                    .Line("$__reason = Get-ControlReferenceReason (Get-ControlDiskReferences $null) (Get-ControlPathKey $__path)")
                    .Line("if ($null -ne $__reason) { Stop-Control 'InvalidArgument' ('Cannot attach ' + $__path + ': it is ' + $__reason + '. Nothing was changed.') }")
                    .Line(VmStateRules.Gate([("attach_vhd", null)]))
                    // Generation 1 tries IDE first, which a running VM refuses; SCSI is hot-pluggable on both generations.
                    .Line("if ([int]$vm.Generation -eq 2 -or [string]$vm.State -eq 'Running') { "
                        + Render("Add-VMHardDiskDrive", ("VM", VmRef), ("ControllerType", PsValue.Raw("SCSI")), ("Path", PsValue.Raw("$__path"))) + " } else { "
                        + Render("Add-VMHardDiskDrive", ("VM", VmRef), ("Path", PsValue.Raw("$__path"))) + " }");
                break;
            case "detach_vhd":
                s.Line(AttachedDrive(literal)).Line(BootDisk).Line(VmStateRules.Gate([("detach_vhd", null)]))
                    .Cmd("Remove-VMHardDiskDrive", ("VMHardDiskDrive", PsValue.Raw("$__drive")));
                break;
            case "attach_iso":
                s.Line("$__iso = " + literal)
                    .Line("if (-not [System.IO.File]::Exists($__iso)) { Stop-Control 'NotFound' ('ISO not found: ' + $__iso) }")
                    .Line("$__dvd = @(Get-VMDvdDrive -VM $vm); $__dvdCount = $__dvd.Count")
                    .Line("if ($__dvdCount -gt 1) { Stop-Control 'InvalidArgument' 'The VM has several DVD drives; this action needs exactly one or none.' }")
                    .Line(VmStateRules.Gate([("attach_iso", null)]))
                    .Line("if ($__dvdCount -eq 0) { " + Render("Add-VMDvdDrive", ("VM", VmRef), ("Path", PsValue.Raw("$__iso"))) + " } else { "
                        + Render("Set-VMDvdDrive", ("VMDvdDrive", PsValue.Raw("$__dvd[0]")), ("Path", PsValue.Raw("$__iso"))) + " }");
                break;
            case "eject_iso":
                s.Line(VmStateRules.Gate([("eject_iso", null)]))
                    .Line("foreach ($__dvd in @(Get-VMDvdDrive -VM $vm)) { " + Render("Set-VMDvdDrive", ("VMDvdDrive", PsValue.Raw("$__dvd")), ("Path", PsValue.Raw("$null"))) + " }");
                break;
            case "resize_vhd" when sizeGb is > 0 and <= 65536:
                s.Line(AttachedDrive(literal))
                    .Line("if ([string]$__drive.Path -match '\\.avhdx?$') { Stop-Control 'InvalidArgument' 'The VM writes to a checkpoint file (.avhdx); remove its checkpoints before resizing. Nothing was changed.' }")
                    .Line("$__reason = Get-ControlReferenceReason (Get-ControlDiskReferences $vm) (Get-ControlPathKey ([string]$__drive.Path))")
                    .Line("if ($null -ne $__reason) { Stop-Control 'InvalidArgument' ('Cannot resize ' + $__drive.Path + ': it is ' + $__reason + '. Nothing was changed.') }")
                    .Line(VmStateRules.Gate([("resize_vhd", null)]))
                    .Cmd("Resize-VHD", ("Path", PsValue.Raw("$__drive.Path")), ("SizeBytes", PsValue.Gigabytes(sizeGb.Value)));
                break;
            default: throw new ArgumentException("Unknown disk action, or resize_vhd without sizeGb 1-65536.");
        }
        return runner.InvokeAsync(s.Returns(MediaResult), ct, 300);
    }

    /// <summary>
    /// Generation 2 firmware and vTPM on an Off VM, in the safe order: Secure Boot template (it locks once
    /// the vTPM is initialized), Secure Boot, key protector (created when missing), TPM, first boot
    /// device. Reads firmware and security back and verifies the requested values.
    /// </summary>
    public Task<JsonElement> FirmwareAsync(string machine, bool? secureBoot, string? template, bool? tpm, string? firstBootDevice, CancellationToken ct)
    {
        if (template is not null) Choice(template, SecureBootTemplates, "secureBootTemplate");
        if (firstBootDevice is not null) Choice(firstBootDevice, BootDevices, "firstBootDevice");
        var requested = new List<(string, PsValue?)>();
        if (template is not null) requested.Add(("secureBootTemplate", null));
        if (secureBoot is not null) requested.Add(("secureBoot", null));
        if (tpm is not null) requested.Add(("tpm", null));
        if (firstBootDevice is not null) requested.Add(("firstBootDevice", null));
        var s = Script().Vm(machine)
            .Line("$__warnings = New-Object System.Collections.ArrayList")
            .Line("if ([int]$vm.Generation -ne 2) { Stop-Control 'InvalidArgument' (\"VM '\" + $vm.Name + \"' is Generation \" + $vm.Generation + '; Secure Boot, boot order and the virtual TPM here apply to Generation 2 VMs.') }")
            .Line(VmStateRules.Gate(requested));
        if (firstBootDevice is not null)
        {
            var (lookup, missing) = firstBootDevice switch
            {
                "dvd" => ("@(Get-VMDvdDrive -VM $vm) | Select-Object -First 1", "DVD drive (attach an ISO with hyperv_disk action=attach_iso)"),
                "disk" => ("@(Get-VMHardDiskDrive -VM $vm | Sort-Object ControllerNumber, ControllerLocation) | Select-Object -First 1", "virtual disk"),
                _ => ("@(Get-VMNetworkAdapter -VM $vm) | Select-Object -First 1", "network adapter")
            };
            s.Line("$__boot = " + lookup)
                .Line($"if ($null -eq $__boot) {{ Stop-Control 'InvalidArgument' 'The VM has no {missing} to boot from. Nothing was changed.' }}");
        }
        if (tpm is not null) s.Line("$__security = Get-VMSecurity -VM $vm");
        if (template is not null) s.Cmd("Set-VMFirmware", ("VM", VmRef), ("SecureBootTemplate", PsValue.Of(template)));
        if (secureBoot is { } enabled) s.Cmd("Set-VMFirmware", ("VM", VmRef), ("EnableSecureBoot", PsValue.Token(enabled ? "On" : "Off", "On", "Off")));
        if (tpm == true)
            s.Line("if (-not $__security.TpmEnabled) {")
                .Line("if (-not (Test-ControlKeyProtector $vm)) { " + Render("Set-VMKeyProtector", ("VM", VmRef), ("NewLocalKeyProtector", PsValue.Of(true)))
                    + "; [void]$__warnings.Add('Created a local key protector: the VM starts only on hosts that hold this host''s Shielded VM guardian certificates.') }")
                .Cmd("Enable-VMTPM", ("VM", VmRef))
                .Line("}");
        else if (tpm == false)
            s.Line("if ($__security.TpmEnabled) { " + Render("Disable-VMTPM", ("VM", VmRef))
                + "; [void]$__warnings.Add('Disabled the virtual TPM: secrets the guest sealed to it (BitLocker, Windows Hello) stay unavailable until it is enabled again with the same key protector.') }");
        if (firstBootDevice is not null) s.Cmd("Set-VMFirmware", ("VM", VmRef), ("FirstBootDevice", PsValue.Raw("$__boot")));
        s.Line("$__firmware = ConvertTo-ControlFirmware $vm").Line("$__readback = ConvertTo-ControlSecurity $vm");
        if (template is not null)
            s.Line($"if ($__firmware.SecureBootTemplate -ne {L(template)}) {{ Stop-Control 'HyperVOperationFailed' ('Hyper-V did not apply the Secure Boot template; it reads ' + $__firmware.SecureBootTemplate + '.') }}");
        if (secureBoot is { } expected)
            s.Line($"if (($__firmware.SecureBoot -eq 'On') -ne {B(expected)}) {{ Stop-Control 'HyperVOperationFailed' 'Hyper-V did not apply the requested Secure Boot setting.' }}");
        if (tpm is { } expectedTpm)
            s.Line($"if ($null -ne $__readback -and $__readback.TpmEnabled -ne {B(expectedTpm)}) {{ Stop-Control 'HyperVOperationFailed' 'Hyper-V did not apply the requested virtual TPM setting.' }}");
        return runner.InvokeAsync(s.Returns("[ordered]@{ success = $true; machine = [ordered]@{ id = [string]$vm.Id; name = [string]$vm.Name; state = [string]$vm.State }; secureBootEnabled = ($__firmware.SecureBoot -eq 'On'); firmware = $__firmware; security = $__readback; warnings = @($__warnings) }"), ct);
    }

    public Task<JsonElement> ListSwitchesAsync(CancellationToken ct) => runner.InvokeAsync(
        new HyperVScript().Returns("@(Get-VMSwitch | Select-Object Id,Name,@{Name='SwitchType';Expression={[string]$_.SwitchType}},NetAdapterInterfaceDescription)"), ct);

    /// <summary>Private or Internal switches, or NAT: an Internal switch whose host adapter gets the
    /// subnet's first address, plus a WinNAT network for the subnet. A failed NAT step removes what it made.</summary>
    public Task<JsonElement> CreateSwitchAsync(string name, string switchType, string? natSubnet, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("switchName required.");
        var type = Choice(switchType, ["Private", "Internal", "NAT"], "switchType");
        var s = Script().Line("$__name = " + L(name))
            .Line("if (@(Get-VMSwitch | Where-Object { $_.Name -eq $__name }).Count -gt 0) { Stop-Control 'InvalidArgument' (\"A virtual switch named '\" + $__name + \"' already exists. Nothing was changed.\") }");
        if (type != "NAT")
        {
            if (natSubnet is not null) throw new ArgumentException("natSubnet applies only to switchType=NAT.");
            return runner.InvokeAsync(s.Cmd("$switch = New-VMSwitch", ("Name", PsValue.Raw("$__name")), ("SwitchType", PsValue.Token(type, "Private", "Internal")))
                .Returns("($switch | Select-Object Id,Name,@{Name='SwitchType';Expression={[string]$_.SwitchType}})"), ct);
        }
        var (prefix, gateway, length) = NatSubnet(natSubnet ?? throw new ArgumentException("switchType=NAT needs natSubnet, for example 192.168.200.0/24."));
        s.Line("$__subnet = " + L(prefix)).Line("$__gateway = " + L(gateway)).Line("""
            Import-ControlNetModules
            foreach ($__existing in @(Get-NetNat -ErrorAction SilentlyContinue)) {
                $__prefix = [string]$__existing.InternalIPInterfaceAddressPrefix
                if ([string]$__existing.Name -eq $__name) { Stop-Control 'InvalidArgument' ("A NAT network named '" + $__name + "' already exists. Nothing was changed.") }
                if ($__prefix -and $__prefix -notmatch ':' -and (Test-ControlPrefixOverlap $__prefix $__subnet)) {
                    Stop-Control 'InvalidArgument' ("NAT network '" + $__existing.Name + "' already uses " + $__prefix + ', which overlaps ' + $__subnet + '. Windows supports one NAT network per host: choose a non-overlapping natSubnet or reuse that network. Nothing was changed.')
                }
            }
            $__switch = $null; $__nat = $null
            try {
            """)
            .Cmd("$__switch = New-VMSwitch", ("Name", PsValue.Raw("$__name")), ("SwitchType", PsValue.Raw("Internal")))
            .Line("""
                $__hostAdapter = $null
                for ($__try = 0; $__try -lt 20 -and $null -eq $__hostAdapter; $__try++) {
                    $__mac = ([string](@(Get-VMNetworkAdapter -ManagementOS | Where-Object { $_.SwitchName -eq $__name }) | Select-Object -First 1).MacAddress) -replace '[^0-9A-Fa-f]', ''
                    $__hostAdapter = @(Get-NetAdapter -IncludeHidden | Where-Object { $__mac -and ((([string]$_.MacAddress) -replace '[^0-9A-Fa-f]', '') -eq $__mac) }) | Select-Object -First 1
                    if ($null -eq $__hostAdapter) { Start-Sleep -Milliseconds 500 }
                }
                if ($null -eq $__hostAdapter) { Stop-Control 'HyperVOperationFailed' 'The host network adapter of the new switch did not appear.' }
                """)
            .Cmd("$null = New-NetIPAddress", ("InterfaceIndex", PsValue.Raw("$__hostAdapter.ifIndex")), ("IPAddress", PsValue.Raw("$__gateway")), ("PrefixLength", PsValue.Of(length)))
            .Cmd("$__nat = New-NetNat", ("Name", PsValue.Raw("$__name")), ("InternalIPInterfaceAddressPrefix", PsValue.Raw("$__subnet")))
            .Line("""
                } catch {
                    $__failure = $_
                    if ($null -ne $__nat) { try { Remove-NetNat -Name $__name -Confirm:$false } catch { } }
                    if ($null -ne $__switch) { try { Remove-VMSwitch -VMSwitch $__switch -Force } catch { } }
                    throw $__failure
                }
                """);
        return runner.InvokeAsync(s.Returns("[ordered]@{ Id = [string]$__switch.Id; Name = [string]$__switch.Name; SwitchType = 'Internal'; nat = [ordered]@{ name = $__name; prefix = $__subnet; gateway = $__gateway }; note = ('WinNAT has no DHCP: give each guest a static address in ' + $__subnet + ', gateway ' + $__gateway + ', and a DNS server.') }"), ct, 180);
    }

    /// <summary>Parses an IPv4 network prefix; returns it with the host gateway (the first address) and its length.</summary>
    internal static (string Prefix, string Gateway, int Length) NatSubnet(string value)
    {
        var parts = value.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is < 8 or > 30)
            throw new ArgumentException("natSubnet must be an IPv4 network prefix such as 192.168.200.0/24 (length 8-30).");
        var bytes = address.GetAddressBytes();
        var number = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
        var mask = uint.MaxValue << (32 - length);
        static string Text(uint n) => new IPAddress([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]).ToString();
        if ((number & ~mask) != 0) throw new ArgumentException($"natSubnet must name its network address: {Text(number & mask)}/{length}.");
        return ($"{Text(number)}/{length}", Text(number + 1), length);
    }

    /// <summary>Removes one switch by exact name, then a NAT network of the same name (the NAT create makes one).</summary>
    public Task<JsonElement> RemoveSwitchAsync(string name, CancellationToken ct) => runner.InvokeAsync(Script()
        .Line("$switches = @(Get-VMSwitch | Where-Object { $_.Name -eq " + L(name) + " })")
        .Line("if ($switches.Count -ne 1) { Stop-Control 'NotFound' 'Switch missing or ambiguous' }")
        .Cmd("$switches[0] | Remove-VMSwitch", ("Force", PsValue.Of(true)))
        .Line("$__nats = @(); try { Import-ControlNetModules; $__nats = @(Get-NetNat -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq " + L(name) + " }) } catch { }")
        .Line("foreach ($__nat in $__nats) { Remove-NetNat -Name $__nat.Name -Confirm:$false }")
        .Returns("[ordered]@{ success = $true; removedNat = @($__nats | ForEach-Object { [string]$_.Name }) }"), ct);

    private static string AdapterLookup(string adapterName) =>
        "$__adapters = @(Get-VMNetworkAdapter -VM $vm | Where-Object { $_.Name -eq " + L(adapterName) + " })\n"
        + "if ($__adapters.Count -ne 1) { Stop-Control 'NotFound' ('Network adapter ' + " + L(L(adapterName)) + " + ' is missing or ambiguous; this VM has: ' + ((@(Get-VMNetworkAdapter -VM $vm) | ForEach-Object { $_.Name }) -join ', ') + '.') }\n"
        + "$__adapter = $__adapters[0]";

    private static string SwitchLookup(string switchName) =>
        "$__switches = @(Get-VMSwitch | Where-Object { $_.Name -eq " + L(switchName) + " })\n"
        + "if ($__switches.Count -eq 0) { Stop-Control 'NotFound' ('Virtual switch ' + " + L(L(switchName)) + " + ' not found. Call hyperv_network action=list.') }\n"
        + "if ($__switches.Count -gt 1) { Stop-Control 'AmbiguousName' 'Several virtual switches have that name.' }";

    public Task<JsonElement> ConnectAdapterAsync(string machine, string adapterName, string? switchName, CancellationToken ct)
    {
        var s = Script().Vm(machine).Line(AdapterLookup(adapterName));
        if (switchName is not null) s.Line(SwitchLookup(switchName));
        s.Line(VmStateRules.Gate([(switchName is null ? "disconnect" : "connect", null)]));
        if (switchName is null) s.Cmd("Disconnect-VMNetworkAdapter", ("VMNetworkAdapter", PsValue.Raw("$__adapter")));
        else s.Cmd("Connect-VMNetworkAdapter", ("VMNetworkAdapter", PsValue.Raw("$__adapter")), ("SwitchName", PsValue.Of(switchName)));
        return runner.InvokeAsync(s.Returns(AdapterResult), ct);
    }

    public Task<JsonElement> AddAdapterAsync(string machine, string adapterName, string? switchName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) throw new ArgumentException("adapterName required.");
        var s = Script().Vm(machine)
            .Line("if (@(Get-VMNetworkAdapter -VM $vm | Where-Object { $_.Name -eq " + L(adapterName) + " }).Count -gt 0) { Stop-Control 'InvalidArgument' ('The VM already has an adapter named ' + " + L(L(adapterName)) + " + '; pass a unique adapterName. Nothing was changed.') }");
        if (!string.IsNullOrEmpty(switchName)) s.Line(SwitchLookup(switchName));
        s.Line(VmStateRules.Gate([("add_adapter", null)]))
            .Cmd("Add-VMNetworkAdapter", ("VM", VmRef), ("Name", PsValue.Of(adapterName)), ("SwitchName", string.IsNullOrEmpty(switchName) ? null : PsValue.Of(switchName)));
        return runner.InvokeAsync(s.Returns(AdapterResult), ct);
    }

    public Task<JsonElement> RemoveAdapterAsync(string machine, string adapterName, CancellationToken ct) => runner.InvokeAsync(Script().Vm(machine)
        .Line(AdapterLookup(adapterName)).Line(VmStateRules.Gate([("remove_adapter", null)]))
        .Cmd("Remove-VMNetworkAdapter", ("VMNetworkAdapter", PsValue.Raw("$__adapter")))
        .Returns(AdapterResult), ct);

    /// <summary>Static or dynamic MAC (Off only), MAC spoofing and access VLAN (live).</summary>
    public Task<JsonElement> SetAdapterAsync(string machine, string adapterName, string? staticMac, bool? macSpoofing, int? vlanId, CancellationToken ct)
    {
        if (staticMac is null && macSpoofing is null && vlanId is null) throw new ArgumentException("set_adapter needs staticMac, macSpoofing or vlanId.");
        if (vlanId is < 0 or > 4094) throw new ArgumentException("vlanId must be 1-4094, or 0 to remove the VLAN.");
        var mac = staticMac is null || staticMac.Equals("dynamic", StringComparison.OrdinalIgnoreCase) ? staticMac : MacAddress(staticMac);
        var requested = new List<(string, PsValue?)>();
        if (staticMac is not null) requested.Add(("staticMac", null));
        if (macSpoofing is not null) requested.Add(("macSpoofing", null));
        if (vlanId is not null) requested.Add(("vlanId", null));
        var s = Script().Vm(machine).Line(AdapterLookup(adapterName)).Line(VmStateRules.Gate(requested));
        if (mac is not null || macSpoofing is not null)
            s.Cmd("Set-VMNetworkAdapter", ("VMNetworkAdapter", PsValue.Raw("$__adapter")),
                ("StaticMacAddress", mac is null || mac.Equals("dynamic", StringComparison.OrdinalIgnoreCase) ? null : PsValue.Of(mac)),
                ("DynamicMacAddress", mac is not null && mac.Equals("dynamic", StringComparison.OrdinalIgnoreCase) ? PsValue.Of(true) : null),
                ("MacAddressSpoofing", macSpoofing is { } spoofing ? PsValue.Token(spoofing ? "On" : "Off", "On", "Off") : null));
        if (vlanId is 0) s.Cmd("Set-VMNetworkAdapterVlan", ("VMNetworkAdapter", PsValue.Raw("$__adapter")), ("Untagged", PsValue.Of(true)));
        else if (vlanId is { } vlan) s.Cmd("Set-VMNetworkAdapterVlan", ("VMNetworkAdapter", PsValue.Raw("$__adapter")), ("Access", PsValue.Of(true)), ("VlanId", PsValue.Of(vlan)));
        return runner.InvokeAsync(s.Returns(AdapterResult), ct);
    }

    /// <summary>Normalizes a unicast MAC address to 12 uppercase hex digits.</summary>
    internal static string MacAddress(string value)
    {
        var hex = new string(value.Where(c => c is not ('-' or ':' or '.')).ToArray()).ToUpperInvariant();
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit) || hex == "000000000000" || (Convert.ToByte(hex[..2], 16) & 1) != 0)
            throw new ArgumentException("staticMac must be 12 hex digits (separators allowed) of a unicast address, for example 00155D0A0B0C, or 'dynamic'.");
        return hex;
    }
}

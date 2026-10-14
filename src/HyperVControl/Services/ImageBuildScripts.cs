namespace HyperVControl.Services;

/// <summary>
/// The host-side PowerShell steps of an image build, one <see cref="HyperVScript"/> each. Module autoloading is off in
/// the shared bootstrap, so the Storage and DISM modules are imported explicitly by path; under the test seam
/// (HYPERV_CONTROL_MODULE_PATH) those imports and the native-tool helper are skipped so the recording mock supplies them.
/// Every caller-supplied value is rendered as a single-quoted literal.
/// </summary>
internal static class ImageBuildScripts
{
    internal const string BasicDataGptType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    internal const string EspGptType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    internal const string MsrGptType = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
    internal const int EspMegabytes = 300;
    internal const int MsrMegabytes = 16;

    private static PsValue L(string value) => PsValue.Of(value);
    private static string Lit(string value) => PowerShellRunner.Literal(value);
    internal static PsValue Array(IEnumerable<string> values) => PsValue.Raw("@(" + string.Join(", ", values.Select(PowerShellRunner.Literal)) + ")");

    /// <summary>
    /// Runs a native tool without a shell. Stdout and stderr are kept apart; the exit code is returned, never thrown, so
    /// each step decides what a failure means. Defined only outside mock mode.
    /// </summary>
    private const string NativeHelper = """
            function Invoke-ImageBuildNative([string]$FilePath, [string[]]$ArgumentList) {
                if (-not [System.IO.File]::Exists($FilePath)) { Stop-Control 'NotFound' ('Executable not found: ' + $FilePath) }
                $ErrorActionPreference = 'Continue'
                $all = @(& $FilePath @ArgumentList 2>&1)
                $code = $LASTEXITCODE
                @{
                    exitCode = [int]$code
                    stdout = (@($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { [string]$_ }) -join "`n")
                    stderr = (@($all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { [string]$_ }) -join "`n")
                }
            }
        """;

    private static HyperVScript Begin(bool storage = false, bool dism = false, bool native = false)
    {
        var script = new HyperVScript();
        if (!(storage || dism || native)) return script;
        var lines = new List<string> { "if ([string]::IsNullOrWhiteSpace($env:HYPERV_CONTROL_MODULE_PATH)) {" };
        if (storage) lines.Add("    Import-Module -Name ([System.IO.Path]::Combine($systemModuleRoot, 'Storage', 'Storage.psd1')) -ErrorAction Stop");
        if (dism) lines.Add("    Import-Module -Name ([System.IO.Path]::Combine($systemModuleRoot, 'Dism', 'Dism.psd1')) -ErrorAction Stop");
        if (native) lines.Add(NativeHelper);
        lines.Add("}");
        return script.Line(string.Join("\n", lines));
    }

    public static HyperVScript DefaultImageRoot() => new HyperVScript().Returns("[string](Get-VMHost).VirtualHardDiskPath");

    /// <summary>Reports whether the ISO is already attached (and its letter) without changing anything.</summary>
    public static HyperVScript ProbeIso(string iso) => Begin(storage: true)
        .Cmd("$image = Get-DiskImage", ("ImagePath", L(iso)))
        .Line("$letter = $null")
        .Line("if ($image.Attached) { $volume = @(Get-Volume -DiskImage $image)[0]; if ($volume -and ([string]$volume.DriveLetter) -match '^[A-Za-z]$') { $letter = [string]$volume.DriveLetter } }")
        .Returns("@{ attached = [bool]$image.Attached; driveLetter = $letter }");

    /// <summary>Mounts the ISO read-only and reads its drive letter back; a mount without a letter is undone.</summary>
    public static HyperVScript MountIso(string iso) => Begin(storage: true)
        .Cmd("$image = Mount-DiskImage", ("ImagePath", L(iso)), ("StorageType", L("ISO")), ("Access", L("ReadOnly")), ("PassThru", PsValue.Of(true)))
        .Line("$letter = $null")
        .Line("for ($attempt = 0; $attempt -lt 40 -and -not $letter; $attempt++) { $volume = @(Get-Volume -DiskImage $image)[0]; if ($volume -and ([string]$volume.DriveLetter) -match '^[A-Za-z]$') { $letter = [string]$volume.DriveLetter } else { Start-Sleep -Milliseconds 250 } }")
        .Line("if (-not $letter) { Dismount-DiskImage -ImagePath " + Lit(iso) + " | Out-Null; Stop-Control 'HyperVOperationFailed' 'The ISO was mounted without a drive letter (no free letter, or automount is disabled). Free a drive letter, or extract sources\\install.wim and pass imagePath.' }")
        .Returns("@{ driveLetter = $letter }");

    public static HyperVScript ListImages(string imageFile) => Begin(dism: true)
        .Line("$images = @(Get-WindowsImage -ImagePath " + Lit(imageFile) + " | ForEach-Object { @{ index = [int]$_.ImageIndex; name = [string]$_.ImageName; description = [string]$_.ImageDescription; size = [long]$_.ImageSize } })")
        .Returns("$images");

    public static HyperVScript ImageDetails(string imageFile, int index) => Begin(dism: true)
        .Cmd("$image = Get-WindowsImage", ("ImagePath", L(imageFile)), ("Index", PsValue.Of(index)))
        .Returns("@{ index = [int]$image.ImageIndex; name = [string]$image.ImageName; editionId = [string]$image.EditionId; installationType = [string]$image.InstallationType; "
            + "architecture = $(if ($null -ne $image.Architecture) { [int]$image.Architecture } else { -1 }); version = [string]$image.Version; size = [long]$image.ImageSize; "
            + "languages = @($image.Languages | ForEach-Object { [string]$_ }); defaultLanguageIndex = [int]$image.DefaultLanguageIndex }");

    /// <summary>
    /// GPT layout from Microsoft's UEFI guidance: a 300 MB EFI system partition (formatted FAT32 as a basic data partition,
    /// then retyped, which Format-Volume requires), a 16 MB MSR and an NTFS Windows partition on the rest. Partitions are
    /// formatted before they receive a letter, and both letters are read back rather than chosen.
    /// </summary>
    public static HyperVScript CreateDisk(string vhdx, int sizeGb) => Begin(storage: true)
        .Cmd("$null = New-VHD", ("Path", L(vhdx)), ("SizeBytes", PsValue.Gigabytes(sizeGb)), ("Dynamic", PsValue.Of(true)))
        .Cmd("$mounted = Mount-VHD", ("Path", L(vhdx)), ("Passthru", PsValue.Of(true)))
        .Line("$diskNumber = $mounted.DiskNumber")
        .Line("if ($null -eq $diskNumber) { $diskNumber = (Get-VHD -Path " + Lit(vhdx) + ").DiskNumber }")
        .Line("if ($null -eq $diskNumber) { Stop-Control 'HyperVOperationFailed' 'The new VHDX was mounted without a disk number.' }")
        .Line("$diskNumber = [int]$diskNumber")
        .Cmd("Initialize-Disk", ("Number", PsValue.Raw("$diskNumber")), ("PartitionStyle", L("GPT")))
        // Initialize-Disk creates an MSR on its own; start from an empty table so the layout is exactly ESP, MSR, Windows.
        .Line("@(Get-Partition -DiskNumber $diskNumber -ErrorAction SilentlyContinue) | ForEach-Object { Remove-Partition -DiskNumber $diskNumber -PartitionNumber $_.PartitionNumber -Confirm:$false }")
        .Cmd("$system = New-Partition", ("DiskNumber", PsValue.Raw("$diskNumber")), ("Size", PsValue.Megabytes(EspMegabytes)), ("GptType", L(BasicDataGptType)))
        .Cmd("$null = Format-Volume", ("Partition", PsValue.Raw("$system")), ("FileSystem", L("FAT32")), ("NewFileSystemLabel", L("System")), ("Force", PsValue.Of(true)), ("Confirm", PsValue.Of(false)))
        .Cmd("Set-Partition", ("DiskNumber", PsValue.Raw("$diskNumber")), ("PartitionNumber", PsValue.Raw("$system.PartitionNumber")), ("GptType", L(EspGptType)))
        .Cmd("Add-PartitionAccessPath", ("DiskNumber", PsValue.Raw("$diskNumber")), ("PartitionNumber", PsValue.Raw("$system.PartitionNumber")), ("AssignDriveLetter", PsValue.Of(true)))
        .Cmd("$null = New-Partition", ("DiskNumber", PsValue.Raw("$diskNumber")), ("Size", PsValue.Megabytes(MsrMegabytes)), ("GptType", L(MsrGptType)))
        .Cmd("$windows = New-Partition", ("DiskNumber", PsValue.Raw("$diskNumber")), ("UseMaximumSize", PsValue.Of(true)), ("GptType", L(BasicDataGptType)))
        .Cmd("$null = Format-Volume", ("Partition", PsValue.Raw("$windows")), ("FileSystem", L("NTFS")), ("NewFileSystemLabel", L("Windows")), ("Force", PsValue.Of(true)), ("Confirm", PsValue.Of(false)))
        .Cmd("Add-PartitionAccessPath", ("DiskNumber", PsValue.Raw("$diskNumber")), ("PartitionNumber", PsValue.Raw("$windows.PartitionNumber")), ("AssignDriveLetter", PsValue.Of(true)))
        .Line("$systemLetter = [string](Get-Partition -DiskNumber $diskNumber -PartitionNumber $system.PartitionNumber).DriveLetter")
        .Line("$windowsLetter = [string](Get-Partition -DiskNumber $diskNumber -PartitionNumber $windows.PartitionNumber).DriveLetter")
        .Line("if ($systemLetter -notmatch '^[A-Za-z]$' -or $windowsLetter -notmatch '^[A-Za-z]$') { Stop-Control 'HyperVOperationFailed' 'No free drive letter could be assigned to the new partitions; free a drive letter and retry.' }")
        .Returns("@{ diskNumber = $diskNumber; systemLetter = $systemLetter; windowsLetter = $windowsLetter; partitions = @(Get-Partition -DiskNumber $diskNumber | ForEach-Object { "
            + "@{ number = [int]$_.PartitionNumber; gptType = [string]$_.GptType; size = [long]$_.Size; driveLetter = $(if ([string]$_.DriveLetter -match '^[A-Za-z]$') { [string]$_.DriveLetter } else { $null }) } }) }");

    public static HyperVScript ApplyImage(string imageFile, int index, char windowsLetter) => Begin(dism: true)
        .Cmd("$null = Expand-WindowsImage", ("ImagePath", L(imageFile)), ("Index", PsValue.Of(index)), ("ApplyPath", L(windowsLetter + ":\\")))
        .Returns("@{ applied = $true }");

    /// <summary>
    /// <c>bcdboot &lt;W&gt;:\Windows /s &lt;S&gt;: /f UEFI</c>: boot files come from the image, and with /s no firmware (NVRAM)
    /// entry is written on the host. <c>/offline</c> is added when this bcdboot supports it (26100.8037+), so boot-file
    /// selection does not depend on the host's own Secure Boot state and no host BitLocker reseal runs. The exit code
    /// and verbose output are returned for the caller to judge.
    /// </summary>
    public static HyperVScript BcdBoot(char windowsLetter, char systemLetter) => Begin(native: true)
        .Line("$bcdboot = [System.IO.Path]::Combine($env:SystemRoot, 'System32', 'bcdboot.exe')")
        .Cmd("$help = Invoke-ImageBuildNative", ("FilePath", PsValue.Raw("$bcdboot")), ("ArgumentList", Array(["/?"])))
        .Line("$offline = [bool](($help.stdout + ' ' + $help.stderr) -match '(?i)/offline\\b')")
        .Line("$arguments = @(" + Lit(windowsLetter + ":\\Windows") + ", '/s', " + Lit(systemLetter + ":") + ", '/f', 'UEFI', '/v')")
        .Line("if ($offline) { $arguments += '/offline' }")
        .Cmd("$run = Invoke-ImageBuildNative", ("FilePath", PsValue.Raw("$bcdboot")), ("ArgumentList", PsValue.Raw("$arguments")))
        .Returns("@{ exitCode = [int]$run.exitCode; offline = $offline; arguments = @($arguments); output = (($run.stdout + \"`n\" + $run.stderr).Trim()) }");

    private static HyperVScript BcdEditor(char systemLetter) => Begin(native: true)
        .Line("$bcdedit = [System.IO.Path]::Combine($env:SystemRoot, 'System32', 'bcdedit.exe')")
        .Line("$store = " + Lit(systemLetter + ":\\EFI\\Microsoft\\Boot\\BCD"))
        // Labels, not arguments, name a failed step: the dbgsettings arguments carry the KDNET key.
        .Line("function Invoke-ImageBuildBcd([string]$Label, [string[]]$Arguments) { $run = Invoke-ImageBuildNative -FilePath $bcdedit -ArgumentList (@('/store', $store) + $Arguments); "
            + "if ($run.exitCode -ne 0) { Stop-Control 'HyperVOperationFailed' ('bcdedit ' + $Label + ' failed (exit ' + $run.exitCode + '): ' + (($run.stdout + ' ' + $run.stderr).Trim())) }; $run }");

    /// <summary>
    /// The bcdedit sequence that completes a store copied from the image's BCD-Template: one Windows Boot Loader and its
    /// resume object with the same elements bcdboot sets, the boot manager pointing at them, and the UEFI memory tester.
    /// </summary>
    internal static IReadOnlyList<(string Label, string[] Arguments)> BcdStoreSteps(char windowsLetter, char systemLetter, string locale, Guid loader, Guid resume)
    {
        var l = "{" + loader.ToString("D") + "}";
        var r = "{" + resume.ToString("D") + "}";
        var windows = "partition=" + windowsLetter + ":";
        var system = "partition=" + systemLetter + ":";
        return
        [
            ("create resume", ["/create", r, "/d", "Windows Resume Application", "/application", "resume"]),
            ("resume device", ["/set", r, "device", windows]),
            ("resume path", ["/set", r, "path", "\\Windows\\system32\\winresume.efi"]),
            ("resume locale", ["/set", r, "locale", locale]),
            ("resume inherit", ["/set", r, "inherit", "{resumeloadersettings}"]),
            ("resume filedevice", ["/set", r, "filedevice", windows]),
            ("resume filepath", ["/set", r, "filepath", "\\hiberfil.sys"]),
            ("resume isolatedcontext", ["/set", r, "isolatedcontext", "Yes"]),
            ("resume allowedinmemorysettings", ["/set", r, "allowedinmemorysettings", "0x15000075"]),
            ("resume bootmenupolicy", ["/set", r, "bootmenupolicy", "Standard"]),
            ("create loader", ["/create", l, "/d", "Windows", "/application", "osloader"]),
            ("loader device", ["/set", l, "device", windows]),
            ("loader path", ["/set", l, "path", "\\Windows\\system32\\winload.efi"]),
            ("loader locale", ["/set", l, "locale", locale]),
            ("loader inherit", ["/set", l, "inherit", "{bootloadersettings}"]),
            ("loader isolatedcontext", ["/set", l, "isolatedcontext", "Yes"]),
            ("loader allowedinmemorysettings", ["/set", l, "allowedinmemorysettings", "0x15000075"]),
            ("loader osdevice", ["/set", l, "osdevice", windows]),
            ("loader systemroot", ["/set", l, "systemroot", "\\Windows"]),
            ("loader resumeobject", ["/set", l, "resumeobject", r]),
            ("loader nx", ["/set", l, "nx", "OptIn"]),
            ("loader bootmenupolicy", ["/set", l, "bootmenupolicy", "Standard"]),
            ("bootmgr device", ["/set", "{bootmgr}", "device", system]),
            ("bootmgr path", ["/set", "{bootmgr}", "path", "\\EFI\\Microsoft\\Boot\\bootmgfw.efi"]),
            ("bootmgr locale", ["/set", "{bootmgr}", "locale", locale]),
            ("bootmgr default", ["/set", "{bootmgr}", "default", l]),
            ("bootmgr resumeobject", ["/set", "{bootmgr}", "resumeobject", r]),
            ("bootmgr displayorder", ["/displayorder", l]),
            ("bootmgr toolsdisplayorder", ["/set", "{bootmgr}", "toolsdisplayorder", "{memdiag}"]),
            ("memdiag device", ["/set", "{memdiag}", "device", system]),
            ("memdiag path", ["/set", "{memdiag}", "path", "\\EFI\\Microsoft\\Boot\\memtest.efi"])
        ];
    }

    public static HyperVScript BcdStore(char windowsLetter, char systemLetter, string locale, Guid loader, Guid resume)
    {
        var script = BcdEditor(systemLetter);
        var steps = BcdStoreSteps(windowsLetter, systemLetter, locale, loader, resume);
        foreach (var (label, arguments) in steps)
            script.Line("$null = Invoke-ImageBuildBcd " + Lit(label) + " " + Array(arguments).Text);
        return script.Returns("@{ steps = " + steps.Count + " }");
    }

    /// <summary>Offline kernel-debug settings in the image's BCD store (Convert-WindowsImage's -EnableDebugger approach).</summary>
    internal static IReadOnlyList<(string Label, string[] Arguments)> BcdDebugSteps(ImageBuildKernelDebug debug) =>
    [
        ("debug on", ["/set", "{default}", "debug", "on"]),
        debug.Transport == "net"
            ? ("dbgsettings net", ["/dbgsettings", "net", "hostip:" + debug.HostIp, "port:" + debug.Port, "key:" + debug.Key])
            : ("dbgsettings serial", ["/dbgsettings", "serial", "debugport:" + debug.ComPort, "baudrate:115200"])
    ];

    public static HyperVScript BcdDebug(char systemLetter, ImageBuildKernelDebug debug)
    {
        var script = BcdEditor(systemLetter);
        foreach (var (label, arguments) in BcdDebugSteps(debug))
            script.Line("$null = Invoke-ImageBuildBcd " + Lit(label) + " " + Array(arguments).Text);
        return script
            .Line("$check = Invoke-ImageBuildBcd 'enum {default}' @('/enum', '{default}')")
            .Returns("@{ debugOn = [bool]($check.stdout -match '(?im)^debug\\s+Yes'); enumDefault = $check.stdout }");
    }

    /// <summary>Dismounts what the build attached; never throws, reports what is still attached.</summary>
    public static HyperVScript Cleanup(string? vhdx, string? iso)
    {
        var script = Begin(storage: true).Line("$problems = @()");
        if (vhdx is not null)
            script.Line("try { Dismount-VHD -Path " + Lit(vhdx) + " -ErrorAction Stop } catch { $dismountError = $_.Exception.Message; "
                + "try { if ((Get-VHD -Path " + Lit(vhdx) + " -ErrorAction Stop).Attached) { $problems += ('The VHDX is still attached: ' + $dismountError) } } catch { } }");
        if (iso is not null)
            script.Line("try { Dismount-DiskImage -ImagePath " + Lit(iso) + " -ErrorAction Stop | Out-Null } catch { $dismountError = $_.Exception.Message; "
                + "try { if ((Get-DiskImage -ImagePath " + Lit(iso) + " -ErrorAction Stop).Attached) { $problems += ('The ISO is still mounted: ' + $dismountError) } } catch { } }");
        return script.Returns("@{ problems = @($problems) }");
    }

    public static HyperVScript QemuInfo(string qemuImg, string source) => Begin(native: true)
        .Cmd("$run = Invoke-ImageBuildNative", ("FilePath", L(qemuImg)), ("ArgumentList", Array(["info", "--output=json", source])))
        .Line("if ($run.exitCode -ne 0) { Stop-Control 'HyperVOperationFailed' ('qemu-img info failed (exit ' + $run.exitCode + '): ' + (($run.stdout + ' ' + $run.stderr).Trim())) }")
        .Line("$info = $run.stdout | ConvertFrom-Json")
        .Returns("@{ format = [string]$info.format; virtualSize = [long]$info.'virtual-size'; backingFile = [string]$info.'backing-filename' }");

    /// <summary><c>qemu-img convert -f &lt;format&gt; -O vhdx -o subformat=dynamic</c>, the conversion Microsoft documents for Hyper-V.</summary>
    public static HyperVScript QemuConvert(string qemuImg, string source, string format, string destination) => Begin(native: true)
        .Cmd("$run = Invoke-ImageBuildNative", ("FilePath", L(qemuImg)), ("ArgumentList", Array(["convert", "-f", format, "-O", "vhdx", "-o", "subformat=dynamic", source, destination])))
        .Line("if ($run.exitCode -ne 0) { Stop-Control 'HyperVOperationFailed' ('qemu-img convert failed (exit ' + $run.exitCode + '): ' + (($run.stdout + ' ' + $run.stderr).Trim())) }")
        .Returns("@{ converted = $true }");

    /// <summary>Grows the virtual disk to the requested size; never shrinks it.</summary>
    public static HyperVScript ResizeVhd(string vhdx, int sizeGb) => new HyperVScript()
        .Cmd("$vhd = Get-VHD", ("Path", L(vhdx)))
        .Line("$before = [long]$vhd.Size")
        .Line("$target = " + PsValue.Gigabytes(sizeGb).Text)
        .Line("if ($before -lt $target) { Resize-VHD -Path " + Lit(vhdx) + " -SizeBytes $target }")
        .Cmd("$vhd = Get-VHD", ("Path", L(vhdx)))
        .Returns("@{ sizeBefore = $before; size = [long]$vhd.Size; fileSize = [long]$vhd.FileSize; resized = ($before -lt $target) }");

    private const string IsoWriter = """
        if (-not ('HyperVControlImageBuild.IsoStreamWriter' -as [type])) {
        Add-Type -TypeDefinition @'
        namespace HyperVControlImageBuild {
            public static class IsoStreamWriter {
                public static void Save(object comStream, string path) {
                    var stream = (System.Runtime.InteropServices.ComTypes.IStream)comStream;
                    var buffer = new byte[1048576];
                    var read = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
                    try {
                        using (var file = new System.IO.FileStream(path, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write)) {
                            while (true) {
                                stream.Read(buffer, buffer.Length, read);
                                var count = System.Runtime.InteropServices.Marshal.ReadInt32(read);
                                if (count <= 0) break;
                                file.Write(buffer, 0, count);
                            }
                        }
                    } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(read); }
                }
            }
        }
        '@
        }
        """;

    /// <summary>
    /// A data-only ISO (ISO 9660 + Joliet, so names such as user-data keep their spelling) built with the inbox IMAPI2
    /// file-system COM API; no ADK and no administrator rights are needed. The target must not exist.
    /// </summary>
    public static HyperVScript BuildIso(string sourceDirectory, string volumeLabel, string isoPath) => new HyperVScript()
        .Line(IsoWriter)
        .Line("$fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage")
        .Line("$fsi.FileSystemsToCreate = 3")
        .Line("$fsi.VolumeName = " + Lit(volumeLabel))
        .Line("$fsi.Root.AddTree(" + Lit(sourceDirectory) + ", $false)")
        .Line("[HyperVControlImageBuild.IsoStreamWriter]::Save($fsi.CreateResultImage().ImageStream, " + Lit(isoPath) + ")")
        .Returns("@{ path = " + Lit(isoPath) + "; size = [long]([System.IO.FileInfo]::new(" + Lit(isoPath) + ").Length) }");
}

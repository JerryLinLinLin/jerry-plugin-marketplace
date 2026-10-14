namespace HyperVControl.Services;

/// <summary>
/// Golden image builds: a Windows ISO applied offline to a bootable VHDX with an unattend file,
/// and a Linux cloud image converted with a NoCloud seed. Implemented in phase 3; this scaffold
/// only fixes the composition point.
/// </summary>
internal sealed class ImageBuildService(PowerShellRunner runner, JobRunner jobs)
{
    internal PowerShellRunner Runner { get; } = runner;
    internal JobRunner Jobs { get; } = jobs;
}

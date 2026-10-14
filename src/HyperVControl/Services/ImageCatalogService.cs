namespace HyperVControl.Services;

/// <summary>
/// Image catalogs (Microsoft's Quick Create gallery feed plus custom catalogs), the local image
/// library and templates. Implemented in phase 3; this scaffold only fixes the composition point.
/// </summary>
internal sealed class ImageCatalogService(PowerShellRunner runner, JobRunner jobs)
{
    internal PowerShellRunner Runner { get; } = runner;
    internal JobRunner Jobs { get; } = jobs;
}

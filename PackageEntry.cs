namespace GarudaDebloat;

internal enum PackageCategory
{
    Win32,
    Uwp,
    WindowsFeature,
    Startup
}

internal sealed class PackageEntry
{
    public bool Selected { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Publisher { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string TypeBadge { get; set; } = string.Empty;

    public PackageCategory Category { get; set; }

    public string Identifier { get; set; } = string.Empty;

    public string? UninstallString { get; set; }

    public string? QuietUninstallString { get; set; }

    public string? InstallLocation { get; set; }

    public string? DisplayIconPath { get; set; }

    public string? UwpPackageFullName { get; set; }

    public string? UwpPackageName { get; set; }

    public string? FeatureName { get; set; }

    public string? StartupRegistryHive { get; set; }

    public string? StartupRegistryPath { get; set; }

    public string? StartupRegistryValueName { get; set; }

    public string? StartupFilePath { get; set; }
}

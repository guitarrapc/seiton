namespace Seiton.Config;

public enum ConfigPathSource
{
    None,
    ExplicitFlag,
    EnvironmentVariable,
    Discovery,
}

public readonly record struct ConfigPathResolution(
    string? Path,
    ConfigPathSource Source = ConfigPathSource.None,
    string? DiscoveryStartDirectory = null,
    int DiscoveryLevelsWalked = 0)
{
    public string FormatVerboseMessage()
    {
        if (Path is null)
        {
            return DiscoveryStartDirectory is not null
                ? $"(none, using defaults) (searched under cwd {System.IO.Path.GetFullPath(DiscoveryStartDirectory)})"
                : "(none, using defaults)";
        }

        var fullPath = System.IO.Path.GetFullPath(Path);
        return Source switch
        {
            ConfigPathSource.ExplicitFlag => $"{fullPath} (from --config)",
            ConfigPathSource.EnvironmentVariable => $"{fullPath} (from SEITON_CONFIG)",
            ConfigPathSource.Discovery =>
                DiscoveryStartDirectory is null
                    ? $"{fullPath} (discovered)"
                    : $"{fullPath} (discovered under cwd {System.IO.Path.GetFullPath(DiscoveryStartDirectory)})",
            _ => fullPath,
        };
    }
}

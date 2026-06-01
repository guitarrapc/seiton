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
                ? $"(none, using defaults) (searched from {System.IO.Path.GetFullPath(DiscoveryStartDirectory)}, walked up {DiscoveryLevelsWalked} level(s))"
                : "(none, using defaults)";
        }

        var fullPath = System.IO.Path.GetFullPath(Path);
        return Source switch
        {
            ConfigPathSource.ExplicitFlag => $"{fullPath} (from --config)",
            ConfigPathSource.EnvironmentVariable => $"{fullPath} (from SEITON_CONFIG)",
            ConfigPathSource.Discovery =>
                $"{fullPath} (discovered from {System.IO.Path.GetFullPath(DiscoveryStartDirectory!)}, walked up {DiscoveryLevelsWalked} level(s))",
            _ => fullPath,
        };
    }
}

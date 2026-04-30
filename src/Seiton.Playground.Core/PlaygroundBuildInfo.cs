using System.Reflection;

namespace Seiton.Playground;

/// <summary>
/// Build/version strings for the Playground (same trimming rules as the seiton CLI <c>version</c> command).
/// </summary>
public static class PlaygroundBuildInfo
{
    /// <summary>
    /// Returns a user-facing version for <paramref name="assembly"/> (strips <c>+commit</c> suffix when present).
    /// </summary>
    public static string GetDisplayVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var fileVersion = assembly.GetName().Version?.ToString();
        return SelectDisplayVersion(informational, fileVersion);
    }

    /// <summary>
    /// Pure selector for tests; mirrors the seiton CLI version string trimming rules.
    /// </summary>
    public static string SelectDisplayVersion(string? informationalVersion, string? assemblyVersionFallback)
    {
        var version = informationalVersion ?? assemblyVersionFallback ?? "0.0.0";
        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0)
            version = version[..plusIndex];
        return version;
    }
}

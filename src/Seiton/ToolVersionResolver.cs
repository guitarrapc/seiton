using System.Reflection;

namespace Seiton;

internal static class ToolVersionResolver
{
    public static string ResolveFromAssembly(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        return TrimBuildMetadata(version);
    }

    public static string TrimBuildMetadata(string version)
    {
        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version[..plusIndex] : version;
    }
}

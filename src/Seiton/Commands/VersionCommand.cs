using System.Reflection;
using System.Runtime.InteropServices;

namespace Seiton.Commands;

internal static class VersionCommand
{
    public static int Run()
    {
        var version = typeof(VersionCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(VersionCommand).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        // Trim source revision hash if present (e.g. "1.0.0+abc123" -> "1.0.0")
        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0)
            version = version[..plusIndex];

        var runtime = RuntimeInformation.FrameworkDescription;
        var os = RuntimeInformation.RuntimeIdentifier;

        Console.WriteLine($"seiton {version}");
        Console.WriteLine($"built with {runtime}, {os}");
        return ExitCode.Success;
    }
}

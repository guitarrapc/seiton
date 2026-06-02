using System.Runtime.InteropServices;

namespace Seiton.Commands;

internal static class VersionCommand
{
    public static int Run()
    {
        var version = ToolVersionResolver.ResolveFromAssembly(typeof(VersionCommand).Assembly);

        var runtime = RuntimeInformation.FrameworkDescription;
        var os = RuntimeInformation.RuntimeIdentifier;

        Console.WriteLine($"seiton {version}");
        Console.WriteLine($"built with {runtime}, {os}");
        return ExitCode.Success;
    }
}

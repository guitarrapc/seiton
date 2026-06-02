namespace Seiton.Cli;

/// <summary>Detects whether the user passed <c>--format</c> on the CLI (vs the built-in default).</summary>
internal static class CliFormatArgs
{
    internal static bool WasFormatSpecified(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
                break;

            var arg = args[i];
            if (arg is "--format" or "-f")
                return true;

            if (arg.StartsWith("--format=", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

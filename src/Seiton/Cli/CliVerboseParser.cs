namespace Seiton.Cli;

/// <summary>Parses verbose flags from raw CLI arguments before framework binding.</summary>
internal static class CliVerboseParser
{
    /// <summary>
    /// Returns the highest verbose level requested by <paramref name="args"/>.
    /// <c>-vv</c> maps to <see cref="VerboseLevel.Files"/>; <c>-v</c> and <c>--verbose</c> map to
    /// <see cref="VerboseLevel.Summary"/>.
    /// </summary>
    public static VerboseLevel Parse(string[] args)
    {
        var level = VerboseLevel.Off;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                break;
            }

            var arg = args[i];
            if (arg == "-vv")
            {
                level = VerboseLevel.Files;
                continue;
            }

            if (arg is "-v" or "--verbose")
            {
                if (level < VerboseLevel.Summary)
                {
                    level = VerboseLevel.Summary;
                }
            }
        }

        return level;
    }

    /// <summary>
    /// Removes flags handled by <see cref="Parse"/> so the CLI framework does not reject them.
    /// </summary>
    public static string[] FilterArgsForFramework(string[] args)
    {
        if (!ContainsFrameworkFilteredFlag(args))
        {
            return args;
        }

        var filtered = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                for (; i < args.Length; i++)
                {
                    filtered.Add(args[i]);
                }
                break;
            }

            if (args[i] == "-vv")
            {
                continue;
            }

            filtered.Add(args[i]);
        }

        return [.. filtered];
    }

    /// <summary>
    /// Combines raw-arg parsing with framework-bound <paramref name="frameworkVerbose"/>.
    /// </summary>
    public static VerboseLevel Resolve(string[] args, bool frameworkVerbose)
    {
        var level = Parse(args);
        if (level == VerboseLevel.Off && frameworkVerbose)
        {
            return VerboseLevel.Summary;
        }

        return level;
    }

    /// <summary>
    /// Combines raw-arg parsing with framework-bound <paramref name="frameworkVerbose"/>.
    /// Uses <see cref="SetRawArgs"/> to supply the original argv from Program entry.
    /// </summary>
    public static VerboseLevel Resolve(bool frameworkVerbose)
        => Resolve(_rawArgs ?? [], frameworkVerbose);

    /// <summary>Stores the original CLI args for verbose-level resolution in command handlers.</summary>
    public static void SetRawArgs(string[] args) => _rawArgs = args;

    private static string[]? _rawArgs;

    private static bool ContainsFrameworkFilteredFlag(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                break;
            }

            if (args[i] == "-vv")
            {
                return true;
            }
        }

        return false;
    }
}

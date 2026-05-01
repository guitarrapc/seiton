using ConsoleAppFramework;
using Seiton.Commands;
using Seiton.Cli;
using Seiton.Output;

if (CliOptionSuggester.TryWriteSuggestionsForUnknownOptions(args, Console.Error))
{
    Environment.ExitCode = ExitCode.InvalidOptions;
    return;
}

var app = ConsoleApp.Create();
app.Add<SeitonCli>();
app.Run(args);

internal class SeitonCli
{
    /// <summary>Lint workflow files by default, or apply fixes when --fix is specified.</summary>
    /// <param name="files">Workflow files or directories to lint. Auto-discovers .github/workflows/ if omitted.</param>
    /// <param name="config">Path to config file. Auto-discovered from .github/seiton.yaml if omitted.</param>
    /// <param name="stdinFilename">Filename used when reading from stdin (-).</param>
    /// <param name="ignore">Substring patterns for messages to ignore (case-insensitive).</param>
    /// <param name="minSeverity">Minimum severity to report: error | warning | info.</param>
    /// <param name="format">Output format: text | json | sarif.</param>
    /// <param name="oneline">Print each diagnostic on a single line.</param>
    /// <param name="color">Color mode: auto | always | never.</param>
    /// <param name="noColor">Disable color output (overrides --color).</param>
    /// <param name="verbose">Print progress information to stderr.</param>
    /// <param name="fix">Enable fix mode for the root command (equivalent to the fix subcommand).</param>
    /// <param name="dryRun">Print unified diff without modifying files (requires --fix).</param>
    /// <param name="check">Exit non-zero if fixable diagnostics exist, without applying fixes (requires --fix).</param>
    /// <param name="enablePinNetwork">Allow network requests to resolve action SHA pins (requires --fix).</param>
    /// <param name="enableImageNetwork">Allow network requests to resolve container image digests (requires --fix).</param>
    /// <param name="includeActions">When no FILES are provided, include .github/actions/ in auto-discovery.</param>
    [Command("")]
    public async Task Root(
        [Argument] string[]? files = null,
        string? config = null,
        string stdinFilename = "<stdin>",
        string[]? ignore = null,
        string? minSeverity = null,
        OutputFormat format = OutputFormat.Text,
        bool oneline = false,
        ColorMode color = ColorMode.Auto,
        bool noColor = false,
        bool verbose = false,
        bool fix = false,
        bool dryRun = false,
        bool check = false,
        bool enablePinNetwork = false,
        bool enableImageNetwork = false,
        bool includeActions = false)
    {
        if (!fix && (dryRun || check || enablePinNetwork || enableImageNetwork))
        {
            Console.Error.WriteLine("--dry-run, --check, --enable-pin-network, and --enable-image-network require --fix on the root command");
            Environment.ExitCode = ExitCode.InvalidOptions;
            return;
        }

        var code = fix
            ? await FixCommand.RunAsync(files ?? [], config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verbose, dryRun, check, enablePinNetwork, enableImageNetwork, includeActions)
            : CheckCommand.Run(files ?? [], config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verbose, includeActions);

        if (code != 0) Environment.ExitCode = code;
    }

    /// <summary>Lint workflow files.</summary>
    /// <param name="files">Workflow files or directories to lint. Auto-discovers .github/workflows/ if omitted.</param>
    /// <param name="config">Path to config file. Auto-discovered from .github/seiton.yaml if omitted.</param>
    /// <param name="stdinFilename">Filename used when reading from stdin (-).</param>
    /// <param name="ignore">Substring patterns for messages to ignore (case-insensitive).</param>
    /// <param name="minSeverity">Minimum severity to report: error | warning | info.</param>
    /// <param name="format">Output format: text | json | sarif.</param>
    /// <param name="oneline">Print each diagnostic on a single line.</param>
    /// <param name="color">Color mode: auto | always | never.</param>
    /// <param name="noColor">Disable color output (overrides --color).</param>
    /// <param name="verbose">Print progress information to stderr.</param>
    /// <param name="includeActions">When no FILES are provided, include .github/actions/ in auto-discovery.</param>
    public void Check(
        [Argument] string[]? files = null,
        string? config = null,
        string stdinFilename = "<stdin>",
        string[]? ignore = null,
        string? minSeverity = null,
        OutputFormat format = OutputFormat.Text,
        bool oneline = false,
        ColorMode color = ColorMode.Auto,
        bool noColor = false,
        bool verbose = false,
        bool includeActions = false)
    {
        var code = CheckCommand.Run(files ?? [], config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verbose, includeActions);
        if (code != 0) Environment.ExitCode = code;
    }


    /// <summary>Generate a starter seiton config file.</summary>
    /// <param name="output">Path to write the config file to.</param>
    /// <param name="force">Overwrite the file if it already exists.</param>
    public void Init(string output = ".github/seiton.yaml", bool force = false)
    {
        var code = InitCommand.Run(output, force);
        if (code != 0) Environment.ExitCode = code;
    }

    /// <summary>Validate the seiton config file.</summary>
    /// <param name="config">Path to the config file to validate. Auto-discovered if omitted.</param>
    [Command("validate-config")]
    public void ValidateConfig(string? config = null)
    {
        var code = ValidateCommand.Run(config);
        if (code != 0) Environment.ExitCode = code;
    }

    /// <summary>Show version and runtime information.</summary>
    public void Version()
    {
        VersionCommand.Run();
    }
}

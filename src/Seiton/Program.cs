using ConsoleAppFramework;
using Seiton.Commands;
using Seiton.Cli;
using Seiton.Output;

var frameworkArgs = CliVerboseParser.FilterArgsForFramework(args);
CliVerboseParser.SetRawArgs(args);
if (CliOptionSuggester.TryWriteSuggestionsForUnknownOptions(args, Console.Error))
{
    Environment.ExitCode = ExitCode.InvalidOptions;
    return;
}

var app = ConsoleApp.Create();
app.Add<SeitonCli>();
app.Run(frameworkArgs);

internal class SeitonCli
{
    /// <summary>Lint workflow files by default, or apply fixes when --fix is specified.</summary>
    /// <param name="config">-c, Path to config file. Auto-discovered from .github/seiton.yaml if omitted.</param>
    /// <param name="stdinFilename">Filename used when reading from stdin (-).</param>
    /// <param name="ignore">Substring patterns for messages to ignore (case-insensitive).</param>
    /// <param name="minSeverity">Minimum severity to report: error | warning | info.</param>
    /// <param name="format">Output format: text | json | sarif.</param>
    /// <param name="oneline">Print each diagnostic on a single line.</param>
    /// <param name="color">Color mode: auto | always | never.</param>
    /// <param name="noColor">Disable color output (overrides --color).</param>
    /// <param name="verbose">-v, Print progress information to stderr (-v / --verbose).</param>
    /// <param name="skipAgenticWorkflows">Skip Agentic Workflow files (with # gh-aw-metadata: header).</param>
    /// <param name="fix">Enable fix mode on the root command.</param>
    /// <param name="dryRun">Print unified diff without modifying files (requires --fix).</param>
    /// <param name="showDiff">Print unified diff after applying fixes (requires --fix; --dry-run takes precedence).</param>
    /// <param name="check">Exit non-zero if fixable diagnostics remain after filtering, without applying fixes (requires --fix).</param>
    /// <param name="enablePinNetwork">Allow network requests to resolve action SHA pins (requires --fix).</param>
    /// <param name="enableImageNetwork">Allow network requests to resolve container image digests (requires --fix).</param>
    /// <param name="includeActions">When no FILES are provided, include .github/actions/ in auto-discovery.</param>
    /// <param name="files">Workflow files or directories to lint. Auto-discovers .github/workflows/ if omitted.</param>
    [Command("")]
    public async Task Root(
        string? config = null,
        string stdinFilename = "<stdin>",
        string[]? ignore = null,
        string? minSeverity = null,
        OutputFormat format = OutputFormat.Text,
        bool oneline = false,
        ColorMode color = ColorMode.Auto,
        bool noColor = false,
        bool verbose = false,
        bool skipAgenticWorkflows = false,
        bool fix = false,
        bool dryRun = false,
        bool showDiff = false,
        bool check = false,
        bool enablePinNetwork = false,
        bool enableImageNetwork = false,
        bool includeActions = false,
        [Argument] params string[] files)
    {
        if (!fix && (dryRun || showDiff || check || enablePinNetwork || enableImageNetwork))
        {
            Console.Error.WriteLine("--dry-run, --show-diff, --check, --enable-pin-network, and --enable-image-network require --fix on the root command");
            Environment.ExitCode = ExitCode.InvalidOptions;
            return;
        }

        var verboseLevel = CliVerboseParser.Resolve(verbose);
        var code = fix
            ? await FixCommand.RunAsync(files, config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verboseLevel, dryRun, check, enablePinNetwork, enableImageNetwork, includeActions, skipAgenticWorkflows, showDiff)
            : CheckCommand.Run(files, config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verboseLevel, includeActions, skipAgenticWorkflows);

        if (code != 0) Environment.ExitCode = code;
    }

    /// <summary>Lint workflow files.</summary>
    /// <param name="config">-c, Path to config file. Auto-discovered from .github/seiton.yaml if omitted.</param>
    /// <param name="stdinFilename">Filename used when reading from stdin (-).</param>
    /// <param name="ignore">Substring patterns for messages to ignore (case-insensitive).</param>
    /// <param name="minSeverity">Minimum severity to report: error | warning | info.</param>
    /// <param name="format">Output format: text | json | sarif.</param>
    /// <param name="oneline">Print each diagnostic on a single line.</param>
    /// <param name="color">Color mode: auto | always | never.</param>
    /// <param name="noColor">Disable color output (overrides --color).</param>
    /// <param name="verbose">-v, Print progress information to stderr (-v / --verbose).</param>
    /// <param name="skipAgenticWorkflows">Skip Agentic Workflow files (with # gh-aw-metadata: header).</param>
    /// <param name="includeActions">When no FILES are provided, include .github/actions/ in auto-discovery.</param>
    /// <param name="files">Workflow files or directories to lint. Auto-discovers .github/workflows/ if omitted.</param>
    public void Check(
        string? config = null,
        string stdinFilename = "<stdin>",
        string[]? ignore = null,
        string? minSeverity = null,
        OutputFormat format = OutputFormat.Text,
        bool oneline = false,
        ColorMode color = ColorMode.Auto,
        bool noColor = false,
        bool verbose = false,
        bool skipAgenticWorkflows = false,
        bool includeActions = false,
        [Argument] params string[] files)
    {
        var verboseLevel = CliVerboseParser.Resolve(verbose);
        var code = CheckCommand.Run(files, config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verboseLevel, includeActions, skipAgenticWorkflows);
        if (code != 0) Environment.ExitCode = code;
    }


    /// <summary>Generate a starter seiton config file. Typical flow: init, then validate-config, then lint with --verbose to confirm discovery.</summary>
    /// <param name="output">Path to write the config file to.</param>
    /// <param name="force">Overwrite the file if it already exists.</param>
    public void Init(string output = ".github/seiton.yaml", bool force = false)
    {
        var code = InitCommand.Run(output, force);
        if (code != 0) Environment.ExitCode = code;
    }

    /// <summary>Validate the seiton config file. Run after init and before production linting.</summary>
    /// <param name="config">-c, Path to the config file to validate. Auto-discovered if omitted.</param>
    /// <param name="verbose">-v, Print config resolution and validation summary to stderr (-v / --verbose).</param>
    [Command("validate-config")]
    public void ValidateConfig(string? config = null, bool verbose = false)
    {
        var verboseLevel = CliVerboseParser.Resolve(verbose);
        var code = ValidateCommand.Run(config, verboseLevel);
        if (code != 0) Environment.ExitCode = code;
    }

    /// <summary>List all available lint rules and their effective status.</summary>
    /// <param name="config">-c, Path to config file. Auto-discovered from .github/seiton.yaml if omitted.</param>
    /// <param name="format">Output format: text | json.</param>
    public void Rules(string? config = null, OutputFormat format = OutputFormat.Text)
    {
        var code = RulesCommand.Run(config, format);
        if (code != 0) Environment.ExitCode = code;
    }

    /// <summary>Show version and runtime information.</summary>
    public void Version()
    {
        VersionCommand.Run();
    }

    /// <summary>Install agent skill files and/or a CI workflow template into the workspace.</summary>
    /// <param name="skills">Install agent skill files.</param>
    /// <param name="target">-t, Target agent platform: claude | copilot | cursor.</param>
    /// <param name="output">-o, Override output path. Applies to skills when --skills is set; applies to the workflow path only when --ci is set without --skills.</param>
    /// <param name="force">-f, Overwrite existing files.</param>
    /// <param name="ci">Install CI workflow template.</param>
    public void Install(bool skills = false, string target = "claude", string? output = null, bool force = false, bool ci = false)
    {
        var code = InstallCommand.Run(skills, target, output, force, ci);
        if (code != 0) Environment.ExitCode = code;
    }
}

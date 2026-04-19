using ConsoleAppFramework;
using Seiton.Commands;
using Seiton.Output;

var app = ConsoleApp.Create();
app.Add<SeitonCli>();
app.Run(args);

internal class SeitonCli
{
    /// <summary>Lint workflow files (equivalent to the check command).</summary>
    /// <param name="files">Workflow files or directories to lint. Auto-discovers .github/workflows/ if omitted.</param>
    /// <param name="config">Path to config file. Auto-discovered from .github/seiton.yaml if omitted.</param>
    /// <param name="stdinFilename">Filename used when reading from stdin (-).</param>
    /// <param name="ignore">Regex patterns for messages to ignore.</param>
    /// <param name="minSeverity">Minimum severity to report: error | warning | info.</param>
    /// <param name="format">Output format: text | json | sarif.</param>
    /// <param name="oneline">Print each diagnostic on a single line.</param>
    /// <param name="color">Color mode: auto | always | never.</param>
    /// <param name="noColor">Disable color output (overrides --color).</param>
    /// <param name="verbose">Print progress information to stderr.</param>
    [Command("")]
    public void Root(
        [Argument] string[]? files = null,
        string? config = null,
        string stdinFilename = "<stdin>",
        string[]? ignore = null,
        string? minSeverity = null,
        OutputFormat format = OutputFormat.Text,
        bool oneline = false,
        ColorMode color = ColorMode.Auto,
        bool noColor = false,
        bool verbose = false)
    {
        var code = CheckCommand.Run(files ?? [], config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verbose);
        if (code != 0) Environment.ExitCode = code;
    }

    /// <summary>Lint workflow files.</summary>
    /// <param name="files">Workflow files or directories to lint. Auto-discovers .github/workflows/ if omitted.</param>
    /// <param name="config">Path to config file. Auto-discovered from .github/seiton.yaml if omitted.</param>
    /// <param name="stdinFilename">Filename used when reading from stdin (-).</param>
    /// <param name="ignore">Regex patterns for messages to ignore.</param>
    /// <param name="minSeverity">Minimum severity to report: error | warning | info.</param>
    /// <param name="format">Output format: text | json | sarif.</param>
    /// <param name="oneline">Print each diagnostic on a single line.</param>
    /// <param name="color">Color mode: auto | always | never.</param>
    /// <param name="noColor">Disable color output (overrides --color).</param>
    /// <param name="verbose">Print progress information to stderr.</param>
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
        bool verbose = false)
    {
        var code = CheckCommand.Run(files ?? [], config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verbose);
        if (code != 0) Environment.ExitCode = code;
    }

    /// <summary>Auto-fix lint issues in workflow files.</summary>
    /// <param name="files">Workflow files or directories to fix. Auto-discovers .github/workflows/ if omitted.</param>
    /// <param name="config">Path to config file. Auto-discovered from .github/seiton.yaml if omitted.</param>
    /// <param name="stdinFilename">Filename used when reading from stdin (-).</param>
    /// <param name="ignore">Regex patterns for messages to ignore.</param>
    /// <param name="minSeverity">Minimum severity to report: error | warning | info.</param>
    /// <param name="format">Output format: text | json | sarif.</param>
    /// <param name="oneline">Print each diagnostic on a single line.</param>
    /// <param name="color">Color mode: auto | always | never.</param>
    /// <param name="noColor">Disable color output (overrides --color).</param>
    /// <param name="verbose">Print progress information to stderr.</param>
    /// <param name="dryRun">Print unified diff without modifying files.</param>
    /// <param name="check">Exit with non-zero status if any fixes are available, without applying them.</param>
    /// <param name="enablePinNetwork">Allow network requests to resolve action SHA pins.</param>
    /// <param name="enableImageNetwork">Allow network requests to resolve container image digests.</param>
    public void Fix(
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
        bool dryRun = false,
        bool check = false,
        bool enablePinNetwork = false,
        bool enableImageNetwork = false)
    {
        var code = FixCommand.Run(files ?? [], config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verbose, dryRun, check, enablePinNetwork, enableImageNetwork);
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

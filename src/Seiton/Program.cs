using ConsoleAppFramework;
using Seiton.Commands;
using Seiton.Output;

var app = ConsoleApp.Create();

// Root command: seiton [FILES...] — same as "check"
app.Add("", ([Argument] string[] files,
    string? config = null,
    string stdinFilename = "<stdin>",
    string[]? ignore = null,
    string? minSeverity = null,
    OutputFormat format = OutputFormat.Text,
    bool oneline = false,
    ColorMode color = ColorMode.Auto,
    bool noColor = false,
    bool verbose = false) =>
{
    var code = CheckCommand.Run(files, config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verbose);
    if (code != 0)
    {
        Environment.ExitCode = code;
    }
});

// Explicit check subcommand
app.Add("check", ([Argument] string[] files,
    string? config = null,
    string stdinFilename = "<stdin>",
    string[]? ignore = null,
    string? minSeverity = null,
    OutputFormat format = OutputFormat.Text,
    bool oneline = false,
    ColorMode color = ColorMode.Auto,
    bool noColor = false,
    bool verbose = false) =>
{
    var code = CheckCommand.Run(files, config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verbose);
    if (code != 0)
    {
        Environment.ExitCode = code;
    }
});

// Fix subcommand
app.Add("fix", ([Argument] string[] files,
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
    bool enableImageNetwork = false) =>
{
    var code = FixCommand.Run(files, config, stdinFilename, ignore ?? [], minSeverity, format, oneline, color, noColor, verbose, dryRun, check, enablePinNetwork, enableImageNetwork);
    if (code != 0)
    {
        Environment.ExitCode = code;
    }
});

// Init subcommand
app.Add("init", (string output = ".github/seiton.yaml", bool force = false) =>
{
    var code = InitCommand.Run(output, force);
    if (code != 0)
    {
        Environment.ExitCode = code;
    }
});

// Validate-config subcommand
app.Add("validate-config", (string? config = null) =>
{
    var code = ValidateCommand.Run(config);
    if (code != 0)
    {
        Environment.ExitCode = code;
    }
});

// Version subcommand
app.Add("version", () =>
{
    VersionCommand.Run();
});

app.Run(args);

using Seiton.Config;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using Seiton.Output;

namespace Seiton.Commands;

internal static class FixCommand
{
    public static async Task<int> RunAsync(
        string[] files,
        string? config,
        string stdinFilename,
        string[] ignore,
        string? minSeverity,
        OutputFormat format,
        bool oneline,
        ColorMode color,
        bool noColor,
        bool verbose,
        bool dryRun,
        bool check,
        bool enablePinNetwork,
        bool enableImageNetwork,
        bool includeActions)
    {
        var resolvedFormat = CliConfigBridge.ResolveOutputFormat(format);
        var colorEnabled = CliConfigBridge.ResolveColorEnabled(color, noColor);

        // Resolve config
        string? configPath;
        try
        {
            configPath = CliConfigBridge.ResolveConfigPath(config);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCode.FatalError;
        }

        var (lintConfig, configDiags) = CliConfigBridge.LoadConfig(configPath, enablePinNetwork, enableImageNetwork);
        if (CheckCommand.HasConfigErrors(configDiags, resolvedFormat, colorEnabled, oneline))
            return ExitCode.FatalError;

        // Resolve input files
        string[] resolvedFiles;
        try
        {
            resolvedFiles = InputDiscovery.ResolveFiles(files, includeActions);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCode.FatalError;
        }

        if (resolvedFiles.Length == 0 && !files.Contains("-"))
        {
            Console.Error.WriteLine(includeActions ? "no workflow/action files found" : "no workflow files found");
            return ExitCode.Success;
        }

        // Build pin remediation engine if network pin/image resolution is enabled.
        // GitHub and OCI use separate HttpClients: GitHub bearer calls only follow same-origin redirects;
        // registry clients may rely on cross-origin redirects (e.g. auth challenges).
        PinRemediationEngine? pinRemediation = null;
        HttpClient? githubHttpClient = null;
        HttpClient? ociHttpClient = null;
        if (enablePinNetwork || enableImageNetwork)
        {
            githubHttpClient = enablePinNetwork ? GitHubApiHttpClientFactory.CreateForGitHubApi() : null;
            ociHttpClient = enableImageNetwork ? new HttpClient() : null;
            var networkConfig = lintConfig?.Network ?? new NetworkConfig();
            var pinningConfig = lintConfig?.Fix.Pinning ?? new FixPinningConfig();
            var imagesConfig = lintConfig?.Fix.Images ?? new FixImagesConfig();

            IActionShaResolver? shaResolver = enablePinNetwork
                ? new GitHubActionShaResolver(githubHttpClient!, pinningConfig, networkConfig.GitHub)
                : null;
            IImageDigestResolver? imageResolver = enableImageNetwork
                ? new OciImageDigestResolver(ociHttpClient!, imagesConfig)
                : null;

            pinRemediation = new PinRemediationEngine(
                shaResolver,
                imageResolver,
                pinningConfig,
                imagesConfig,
                networkConfig);
        }

        try
        {
            var engine = new LintEngine();
            var allDiagnostics = new List<Diagnostic>();
            var hasFixable = false;

            // Fix command always builds fixes; enable fix construction for all Check() calls.
            var fixEnabledLintConfig = new LintConfig
            {
                Rules = lintConfig?.Rules,
                Exclusions = lintConfig?.Exclusions,
                Fix = (lintConfig?.Fix ?? new FixConfig()) with { Enabled = true },
                Network = lintConfig?.Network ?? new NetworkConfig(),
            };

            for (var i = 0; i < resolvedFiles.Length; i++)
            {
                var filePath = resolvedFiles[i];
                if (filePath == "-")
                {
                    Console.Error.WriteLine("fix: stdin not supported for fix command");
                    return ExitCode.InvalidOptions;
                }

                var utf8Yaml = File.ReadAllBytes(filePath);

                if (verbose)
                    Console.Error.WriteLine($"fixing {filePath}...");

                var result = engine.Check(utf8Yaml, filePath, fixEnabledLintConfig);

                // Apply network-assisted pin remediation: attaches SHA/digest DiagnosticFix values
                // to unpinned-uses and unpinned-image diagnostics. This runs once per file.
                IReadOnlyList<Diagnostic> effectiveDiagnostics = result.Diagnostics;
                if (pinRemediation != null)
                {
                    var remResult = await pinRemediation.RemediateAsync(result.Diagnostics, utf8Yaml);
                    effectiveDiagnostics = remResult.Diagnostics;
                    if (verbose && remResult.ResolvedCount > 0)
                        Console.Error.WriteLine($"  resolved {remResult.ResolvedCount} pin(s) for {filePath}");
                }

                // Check whether any diagnostic (local or pin-remediated) has a fix attached.
                var hasAnyFix = false;
                for (var j = 0; j < effectiveDiagnostics.Count; j++)
                {
                    if (effectiveDiagnostics[j].Fix != null)
                    {
                        hasAnyFix = true;
                        break;
                    }
                }

                if (!hasAnyFix)
                {
                    allDiagnostics.AddRange(effectiveDiagnostics);
                    continue;
                }

                hasFixable = true;

                if (check)
                {
                    // --check: report fixable but don't apply
                    allDiagnostics.AddRange(effectiveDiagnostics);
                    continue;
                }

                if (dryRun)
                {
                    // --dry-run: print diff using all effective (local + pin-remediated) fixes
                    FixEngine.WriteUnifiedDiff(Console.Out, utf8Yaml, effectiveDiagnostics, filePath);
                    allDiagnostics.AddRange(effectiveDiagnostics);
                    continue;
                }

                // Apply first fix pass: includes both pin-remediated and local fixes.
                var currentYaml = utf8Yaml;
                var appliedFixes = 0;
                const int maxFixPasses = 8;

                var firstPassYaml = FixEngine.Apply(currentYaml, effectiveDiagnostics);
                if (!firstPassYaml.AsSpan().SequenceEqual(currentYaml))
                {
                    var firstPassFixCount = 0;
                    for (var j = 0; j < effectiveDiagnostics.Count; j++)
                        if (effectiveDiagnostics[j].Fix != null) firstPassFixCount++;
                    appliedFixes += firstPassFixCount;
                    currentYaml = firstPassYaml;
                }

                // Subsequent re-check passes: local AST fixes only (pin diagnostics are now
                // satisfied so they won't reappear). Skip pass 0 since it was already applied above.
                var currentResult = engine.Check(currentYaml, filePath, fixEnabledLintConfig);
                for (var pass = 1; pass < maxFixPasses && currentResult.HasFixableDiagnostics; pass++)
                {
                    var nextYaml = FixEngine.Apply(currentYaml, currentResult.FixableDiagnostics);
                    if (nextYaml.AsSpan().SequenceEqual(currentYaml))
                    {
                        break;
                    }

                    appliedFixes += currentResult.FixableDiagnosticCount;
                    currentYaml = nextYaml;
                    currentResult = engine.Check(currentYaml, filePath, lintConfig);
                }

                File.WriteAllBytes(filePath, currentYaml);
                allDiagnostics.AddRange(currentResult.Diagnostics);

                if (verbose)
                    Console.Error.WriteLine($"  applied {appliedFixes} fix(es) to {filePath}");
            }

            // Apply ignore patterns
            if (ignore.Length > 0)
            {
                var patterns = new System.Text.RegularExpressions.Regex[ignore.Length];
                for (var i = 0; i < ignore.Length; i++)
                    patterns[i] = new System.Text.RegularExpressions.Regex(ignore[i], System.Text.RegularExpressions.RegexOptions.Compiled);

                allDiagnostics.RemoveAll(d =>
                {
                    for (var i = 0; i < patterns.Length; i++)
                    {
                        if (patterns[i].IsMatch(d.Message))
                            return true;
                    }
                    return false;
                });
            }

            // Apply min-severity filter
            if (minSeverity is not null)
            {
                var threshold = minSeverity.ToLowerInvariant() switch
                {
                    "error" => DiagnosticSeverity.Error,
                    "warning" => DiagnosticSeverity.Warning,
                    "info" => DiagnosticSeverity.Info,
                    _ => (DiagnosticSeverity?)null,
                };
                if (threshold is not null)
                    allDiagnostics.RemoveAll(d => d.Severity < threshold.Value);
            }

            // Output remaining diagnostics
            if (allDiagnostics.Count > 0)
                DiagnosticFormatter.Write(Console.Out, allDiagnostics, resolvedFormat, oneline, colorEnabled);

            CheckCommand.WriteSummary(allDiagnostics, resolvedFiles.Length);

            if (check && hasFixable)
                return ExitCode.LintIssuesFound;

            return CheckCommand.HasActionableDiagnostics(allDiagnostics) ? ExitCode.LintIssuesFound : ExitCode.Success;
        }
        finally
        {
            githubHttpClient?.Dispose();
            ociHttpClient?.Dispose();
        }
    }
}

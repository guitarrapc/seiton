using Seiton.Core.Linting;
using Seiton.Output;

namespace Seiton.Config;

public static class CliConfigBridge
{
    /// <summary>
    /// Resolve the config file path using precedence: explicit flag > env var > discovery.
    /// Returns null if no config file exists (valid: use defaults).
    /// Throws if an explicit path was given but the file does not exist.
    /// </summary>
    public static string? ResolveConfigPath(string? explicitConfigPath)
    {
        // 1. Explicit flag or SEITON_CONFIG env var
        var configPath = explicitConfigPath
            ?? Environment.GetEnvironmentVariable("SEITON_CONFIG");

        if (!string.IsNullOrEmpty(configPath))
        {
            if (!File.Exists(configPath))
                throw new FileNotFoundException($"config file not found: {configPath}", configPath);
            return configPath;
        }

        // 2. Discovery from current directory and parents
        var current = Environment.CurrentDirectory;
        while (current is not null)
        {
            var discovered = LintConfigLibrary.FindRecommendedConfigPath(current);
            if (discovered is not null)
            {
                return discovered;
            }

            var parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        return null;
    }

    /// <summary>
    /// Load and validate config from file, then apply CLI flag overrides.
    /// Returns null config (use defaults) if no config file found.
    /// </summary>
    public static (LintConfig? Config, Core.Parsing.Diagnostic[] Diagnostics) LoadConfig(
        string? configPath,
        bool enablePinNetwork,
        bool enableImageNetwork)
    {
        if (configPath is null)
        {
            // No config file: apply only CLI overrides to defaults
            if (enablePinNetwork || enableImageNetwork)
            {
                var config = new LintConfig
                {
                    Fix = new FixConfig
                    {
                        Pinning = new FixPinningConfig { EnableNetwork = enablePinNetwork },
                        Images = new FixImagesConfig { EnableNetwork = enableImageNetwork },
                    },
                };
                return (config, []);
            }

            return (null, []);
        }

        var result = LintConfigLibrary.ValidateFile(configPath);
        if (!result.IsValid || result.Config is null)
        {
            return (null, result.Diagnostics);
        }

        var loaded = result.Config;

        // Apply CLI overrides (flag > config file > default)
        if (enablePinNetwork || enableImageNetwork)
        {
            loaded = new LintConfig
            {
                Utf8Yaml = loaded.Utf8Yaml,
                FilePath = loaded.FilePath,
                Rules = loaded.Rules,
                Exclusions = loaded.Exclusions,
                Fix = new FixConfig
                {
                    Defaults = loaded.Fix.Defaults,
                    Pinning = enablePinNetwork
                        ? loaded.Fix.Pinning with { EnableNetwork = true }
                        : loaded.Fix.Pinning,
                    Images = enableImageNetwork
                        ? loaded.Fix.Images with { EnableNetwork = true }
                        : loaded.Fix.Images,
                },
                Network = loaded.Network,
            };
        }

        return (loaded, result.Diagnostics);
    }

    /// <summary>
    /// Resolve color mode considering flags, env vars, and CI detection.
    /// </summary>
    public static bool ResolveColorEnabled(ColorMode colorFlag, bool noColorFlag)
    {
        // --no-color flag or SEITON_NO_COLOR env var
        if (noColorFlag)
            return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SEITON_NO_COLOR")))
            return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            return false;

        return colorFlag switch
        {
            ColorMode.Always => true,
            ColorMode.Never => false,
            // Auto: disable in CI or when not a TTY
            _ => !IsCi() && Console.IsOutputRedirected is false,
        };
    }

    /// <summary>
    /// Resolve output format from flag and SEITON_FORMAT env var.
    /// </summary>
    public static OutputFormat ResolveOutputFormat(OutputFormat flagFormat)
    {
        if (flagFormat != OutputFormat.Text)
            return flagFormat;

        var envFormat = Environment.GetEnvironmentVariable("SEITON_FORMAT");
        if (string.IsNullOrEmpty(envFormat))
            return OutputFormat.Text;

        return envFormat.ToLowerInvariant() switch
        {
            "json" => OutputFormat.Json,
            "sarif" => OutputFormat.Sarif,
            _ => OutputFormat.Text,
        };
    }

    static bool IsCi() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
}

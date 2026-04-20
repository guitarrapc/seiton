using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

internal sealed class LintConfigLineParser
{
    readonly string[] lines;
    readonly string filePath;

    int index;

    readonly Dictionary<string, RuleConfig> rules = new(StringComparer.OrdinalIgnoreCase);
    readonly List<LintExclusion> exclusions = [];
    readonly List<Diagnostic> diagnostics = [];
    FixConfig fix = new();
    NetworkConfig network = new();

    public LintConfigLineParser(string yamlText, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        lines = yamlText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        this.filePath = filePath;
    }

    public ParseResult Parse()
    {
        while (index < lines.Length)
        {
            var lineNumber = index + 1;
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent != 0)
            {
                diagnostics.Add(CreateError("expected top-level mapping key", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                diagnostics.Add(CreateError("expected mapping key", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            index++;
            if (key == "rules")
            {
                if (!string.IsNullOrEmpty(value))
                {
                    diagnostics.Add(CreateError("rules must be a mapping section", lineNumber, 1, line.Trim().Length));
                    continue;
                }

                ParseRulesSection();
                continue;
            }

            if (key == "exclusions")
            {
                if (!string.IsNullOrEmpty(value))
                {
                    diagnostics.Add(CreateError("exclusions must be a sequence section", lineNumber, 1, line.Trim().Length));
                    continue;
                }

                ParseExclusionsSection();
                continue;
            }

            if (key == "fix")
            {
                if (!string.IsNullOrEmpty(value))
                {
                    diagnostics.Add(CreateError("fix must be a mapping section", lineNumber, 1, line.Trim().Length));
                    continue;
                }

                ParseFixSection();
                continue;
            }

            if (key == "network")
            {
                if (!string.IsNullOrEmpty(value))
                {
                    diagnostics.Add(CreateError("network must be a mapping section", lineNumber, 1, line.Trim().Length));
                    continue;
                }

                ParseNetworkSection();
                continue;
            }

            diagnostics.Add(CreateError($"unknown top-level key '{key}'", lineNumber, 1, key.Length));
            if (string.IsNullOrEmpty(value))
            {
                SkipIndentedBlock(0);
            }
        }

        return new ParseResult(
            rules,
            exclusions,
            fix,
            network,
            diagnostics.ToArray());
    }

    void ParseRulesSection()
    {
        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 0)
            {
                return;
            }

            var lineNumber = index + 1;
            if (indent != 2)
            {
                diagnostics.Add(CreateError("rules entry must be indented by 2 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseKey(line, out var ruleId))
            {
                diagnostics.Add(CreateError("rules entry must be a mapping key", lineNumber, 3, line.Trim().Length));
                index++;
                continue;
            }

            index++;
            ParseRuleBody(ruleId, lineNumber);
        }
    }

    void ParseRuleBody(string ruleId, int ruleLineNumber)
    {
        var enabled = true;
        DiagnosticSeverity? severity = null;
        IReadOnlyList<string> events = [];
        IReadOnlyList<string> knownHostedLabels = [];
        IReadOnlyList<string> publicRegistries = [];
        IReadOnlyList<string> untrustedTriggers = [];
        IReadOnlyList<string> outputCommands = [];
        IReadOnlyList<string> assumeEvents = [];
        IReadOnlyList<string> allow = [];
        IReadOnlyList<string> deny = [];
        int? maxStepEnvSecrets = null;
        int? maxJobSecrets = null;
        var seenRuleSpecificKeys = new HashSet<string>(StringComparer.Ordinal);

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 2)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 4)
            {
                diagnostics.Add(CreateError("rule option must be indented by 4 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                if (!TryParseKey(line, out key))
                {
                    diagnostics.Add(CreateError("rule option must be key: value or a section key", lineNumber, 5, line.Trim().Length));
                    index++;
                    continue;
                }

                index++;
                switch (key)
                {
                    case "events":
                        seenRuleSpecificKeys.Add("events");
                        events = ParseExtendableList(4);
                        break;
                    case "known-hosted-labels":
                        seenRuleSpecificKeys.Add("known-hosted-labels");
                        knownHostedLabels = ParseExtendableList(4);
                        break;
                    case "public-registries":
                        seenRuleSpecificKeys.Add("public-registries");
                        publicRegistries = ParseExtendableList(4);
                        break;
                    case "untrusted-triggers":
                        seenRuleSpecificKeys.Add("untrusted-triggers");
                        untrustedTriggers = ParseExtendableList(4);
                        break;
                    case "output-commands":
                        seenRuleSpecificKeys.Add("output-commands");
                        outputCommands = ParseExtendableList(4);
                        break;
                    case "assume-events":
                        seenRuleSpecificKeys.Add("assume-events");
                        assumeEvents = ParseListBlock(4, "assume-events");
                        break;
                    case "allow":
                        seenRuleSpecificKeys.Add("allow");
                        allow = ParseListBlock(4, "allow");
                        break;
                    case "deny":
                        seenRuleSpecificKeys.Add("deny");
                        deny = ParseListBlock(4, "deny");
                        break;
                    default:
                        diagnostics.Add(CreateError($"unknown rule option '{key}'", lineNumber, 5, key.Length));
                        SkipIndentedBlock(4);
                        break;
                }

                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                index++;
                switch (key)
                {
                    case "events":
                        seenRuleSpecificKeys.Add("events");
                        events = ParseExtendableList(4);
                        break;
                    case "known-hosted-labels":
                        seenRuleSpecificKeys.Add("known-hosted-labels");
                        knownHostedLabels = ParseExtendableList(4);
                        break;
                    case "public-registries":
                        seenRuleSpecificKeys.Add("public-registries");
                        publicRegistries = ParseExtendableList(4);
                        break;
                    case "untrusted-triggers":
                        seenRuleSpecificKeys.Add("untrusted-triggers");
                        untrustedTriggers = ParseExtendableList(4);
                        break;
                    case "output-commands":
                        seenRuleSpecificKeys.Add("output-commands");
                        outputCommands = ParseExtendableList(4);
                        break;
                    case "assume-events":
                        seenRuleSpecificKeys.Add("assume-events");
                        assumeEvents = ParseListBlock(4, "assume-events");
                        break;
                    case "allow":
                        seenRuleSpecificKeys.Add("allow");
                        allow = ParseListBlock(4, "allow");
                        break;
                    case "deny":
                        seenRuleSpecificKeys.Add("deny");
                        deny = ParseListBlock(4, "deny");
                        break;
                    default:
                        diagnostics.Add(CreateError($"unknown rule option '{key}'", lineNumber, 5, key.Length));
                        SkipIndentedBlock(4);
                        break;
                }

                continue;
            }

            if (key == "enabled")
            {
                if (!TryParseBool(value, out var parsedEnabled))
                {
                    diagnostics.Add(CreateError("enabled must be true or false", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    enabled = parsedEnabled;
                }

                index++;
                continue;
            }

            if (key == "severity")
            {
                if (!TryParseSeverity(value, out var parsedSeverity))
                {
                    diagnostics.Add(CreateError("severity must be one of info, warning, error", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    severity = parsedSeverity;
                }

                index++;
                continue;
            }

            if (key == "max-step-env-secrets")
            {
                seenRuleSpecificKeys.Add("max-step-env-secrets");
                if (!int.TryParse(value, out var parsedMax) || parsedMax < 0)
                {
                    diagnostics.Add(CreateError("max-step-env-secrets must be a non-negative integer", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    maxStepEnvSecrets = parsedMax;
                }

                index++;
                continue;
            }

            if (key == "max-job-secrets")
            {
                seenRuleSpecificKeys.Add("max-job-secrets");
                if (!int.TryParse(value, out var parsedMax) || parsedMax < 0)
                {
                    diagnostics.Add(CreateError("max-job-secrets must be a non-negative integer", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    maxJobSecrets = parsedMax;
                }

                index++;
                continue;
            }

            diagnostics.Add(CreateError($"unknown rule option '{key}'", lineNumber, 5, key.Length));
            index++;
        }

        var config = new RuleConfig
        {
            Enabled = enabled,
            Severity = severity,
            Specific = BuildSpecificFromParsedRuleOptions(
                ruleId,
                seenRuleSpecificKeys,
                events,
                knownHostedLabels,
                publicRegistries,
                untrustedTriggers,
                outputCommands,
                assumeEvents,
                allow,
                deny,
                maxStepEnvSecrets,
                maxJobSecrets,
                ruleLineNumber),
        };

        if (!rules.TryAdd(ruleId, config))
        {
            diagnostics.Add(CreateError($"duplicate rule entry '{ruleId}'", ruleLineNumber, 3, ruleId.Length));
        }
    }

    IReadOnlyList<string> ParseExtendableList(int parentIndent)
    {
        IReadOnlyList<string> values = [];

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= parentIndent)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != parentIndent + 2)
            {
                diagnostics.Add(CreateError("extend key must be properly indented", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseKey(line, out var key))
            {
                if (TryParseProperty(line, out key, out var propValue) && string.IsNullOrEmpty(propValue))
                {
                    // key: with empty value, treated as section header
                }
                else
                {
                    diagnostics.Add(CreateError("expected 'extend' key", lineNumber, parentIndent + 3, line.Trim().Length));
                    index++;
                    continue;
                }
            }

            if (key != "extend")
            {
                diagnostics.Add(CreateError($"unknown key '{key}', expected 'extend'", lineNumber, parentIndent + 3, key.Length));
                index++;
                SkipIndentedBlock(parentIndent + 2);
                continue;
            }

            index++;
            values = ParseListBlock(parentIndent + 2, "extend");
        }

        return values;
    }

    RuleSpecificConfig BuildSpecificFromParsedRuleOptions(
        string ruleId,
        IReadOnlySet<string> seenRuleSpecificKeys,
        IReadOnlyList<string> events,
        IReadOnlyList<string> knownHostedLabels,
        IReadOnlyList<string> publicRegistries,
        IReadOnlyList<string> untrustedTriggers,
        IReadOnlyList<string> outputCommands,
        IReadOnlyList<string> assumeEvents,
        IReadOnlyList<string> allow,
        IReadOnlyList<string> deny,
        int? maxStepEnvSecrets,
        int? maxJobSecrets,
        int ruleLineNumber)
    {
        if (!RuleCatalog.TryResolveRuleId(ruleId, out var resolvedRuleId))
        {
            return RuleSpecificConfig.None;
        }

        if (RuleCatalog.TryGetAllowedConfigKeys(resolvedRuleId, out var allowed))
        {
            foreach (var key in seenRuleSpecificKeys)
            {
                if (!allowed.Contains(key))
                {
                    diagnostics.Add(CreateError($"rule '{resolvedRuleId}' does not accept '{key}' config key", ruleLineNumber, 3, key.Length));
                }
            }
        }

        return resolvedRuleId switch
        {
            "dangerous-triggers" when events is { Count: > 0 } => new DangerousTriggersSpecificConfig(events),
            "runner-label" when knownHostedLabels is { Count: > 0 } => new RunnerLabelSpecificConfig(knownHostedLabels),
            "credentials" when publicRegistries is { Count: > 0 } => new CredentialsSpecificConfig(publicRegistries),
            "cache-poisoning" or "self-hosted-runner" when untrustedTriggers is { Count: > 0 } => new UntrustedTriggersSpecificConfig(untrustedTriggers),
            "unredacted-secrets" when outputCommands is { Count: > 0 } => new UnredactedSecretsSpecificConfig(outputCommands),
            "expr-undefined-var" when assumeEvents is { Count: > 0 } => new ExprUndefinedVarSpecificConfig(assumeEvents),
            "forbidden-uses" when allow.Count > 0 || deny.Count > 0 => new ForbiddenUsesSpecificConfig(allow.Count > 0 ? allow : null, deny.Count > 0 ? deny : null),
            "overprovisioned-secrets" when maxStepEnvSecrets is not null || maxJobSecrets is not null => new OverprovisionedSecretsSpecificConfig(
                maxStepEnvSecrets ?? 5,
                maxJobSecrets ?? 5),
            _ => RuleSpecificConfig.None,
        };
    }

    void ParseFixSection()
    {
        var defaults = new FixDefaultsConfig();
        var pinning = new FixPinningConfig();
        var images = new FixImagesConfig();

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 0)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 2)
            {
                diagnostics.Add(CreateError("fix key must be indented by 2 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseKey(line, out var key))
            {
                if (TryParseProperty(line, out key, out var propValue) && string.IsNullOrEmpty(propValue))
                {
                    // key: with empty value
                }
                else
                {
                    diagnostics.Add(CreateError("fix entry must be a section key", lineNumber, 3, line.Trim().Length));
                    index++;
                    continue;
                }
            }

            index++;
            if (key == "defaults")
            {
                defaults = ParseFixDefaultsSection();
                continue;
            }

            if (key == "pinning")
            {
                pinning = ParseFixPinningSection();
                continue;
            }

            if (key == "images")
            {
                images = ParseFixImagesSection();
                continue;
            }

            diagnostics.Add(CreateError($"unknown fix key '{key}'", lineNumber, 3, key.Length));
            SkipIndentedBlock(2);
        }

        fix = new FixConfig
        {
            Defaults = defaults,
            Pinning = pinning,
            Images = images,
        };
    }

    FixDefaultsConfig ParseFixDefaultsSection()
    {
        int? jobTimeoutMinutes = null;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 2)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 4)
            {
                diagnostics.Add(CreateError("fix.defaults key must be indented by 4 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                diagnostics.Add(CreateError("fix.defaults entry must be key: value", lineNumber, 5, line.Trim().Length));
                index++;
                continue;
            }

            if (key == "job-timeout-minutes")
            {
                if (!TryParseInt(value, out var parsed))
                {
                    diagnostics.Add(CreateError("fix.defaults.job-timeout-minutes must be an integer", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    jobTimeoutMinutes = parsed;
                }

                index++;
                continue;
            }

            diagnostics.Add(CreateError($"unknown fix.defaults key '{key}'", lineNumber, 5, key.Length));
            index++;
        }

        return new FixDefaultsConfig { JobTimeoutMinutes = jobTimeoutMinutes };
    }

    FixPinningConfig ParseFixPinningSection()
    {
        var enableNetwork = false;
        var minAgeDays = 14;
        IReadOnlyList<string> excludeBranches = [];
        IReadOnlyList<IgnoreActionEntry> ignoreActions = [];

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 2)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 4)
            {
                diagnostics.Add(CreateError("fix.pinning key must be indented by 4 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                if (!TryParseKey(line, out key))
                {
                    diagnostics.Add(CreateError("fix.pinning entry must be key or key: value", lineNumber, 5, line.Trim().Length));
                    index++;
                    continue;
                }

                index++;
                if (key == "exclude-branches")
                {
                    excludeBranches = ParseListBlock(4, "exclude-branches");
                    continue;
                }

                if (key == "ignore-actions")
                {
                    ignoreActions = ParseIgnoreActionsList(4);
                    continue;
                }

                diagnostics.Add(CreateError($"unknown fix.pinning key '{key}'", lineNumber, 5, key.Length));
                SkipIndentedBlock(4);
                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                index++;
                if (key == "exclude-branches")
                {
                    excludeBranches = ParseListBlock(4, "exclude-branches");
                    continue;
                }

                if (key == "ignore-actions")
                {
                    ignoreActions = ParseIgnoreActionsList(4);
                    continue;
                }

                diagnostics.Add(CreateError($"unknown fix.pinning key '{key}'", lineNumber, 5, key.Length));
                SkipIndentedBlock(4);
                continue;
            }

            if (key == "enable-network")
            {
                if (!TryParseBool(value, out var parsed))
                {
                    diagnostics.Add(CreateError("fix.pinning.enable-network must be true or false", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    enableNetwork = parsed;
                }

                index++;
                continue;
            }

            if (key == "min-age-days")
            {
                if (!TryParseInt(value, out var parsed))
                {
                    diagnostics.Add(CreateError("fix.pinning.min-age-days must be an integer", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    minAgeDays = parsed;
                }

                index++;
                continue;
            }

            diagnostics.Add(CreateError($"unknown fix.pinning key '{key}'", lineNumber, 5, key.Length));
            index++;
        }

        return new FixPinningConfig
        {
            EnableNetwork = enableNetwork,
            MinAgeDays = minAgeDays,
            ExcludeBranches = excludeBranches.Count > 0 ? excludeBranches : new FixPinningConfig().ExcludeBranches,
            IgnoreActions = ignoreActions,
        };
    }

    FixImagesConfig ParseFixImagesSection()
    {
        var enableNetwork = false;
        IReadOnlyList<string> excludeImages = [];
        IReadOnlyList<string> excludeTags = [];
        IReadOnlyList<string> ignoreImages = [];

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 2)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 4)
            {
                diagnostics.Add(CreateError("fix.images key must be indented by 4 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                if (!TryParseKey(line, out key))
                {
                    diagnostics.Add(CreateError("fix.images entry must be key or key: value", lineNumber, 5, line.Trim().Length));
                    index++;
                    continue;
                }

                index++;
                if (key == "exclude-images")
                {
                    excludeImages = ParseListBlock(4, "exclude-images");
                    continue;
                }

                if (key == "exclude-tags")
                {
                    excludeTags = ParseListBlock(4, "exclude-tags");
                    continue;
                }

                if (key == "ignore-images")
                {
                    ignoreImages = ParseListBlock(4, "ignore-images");
                    continue;
                }

                diagnostics.Add(CreateError($"unknown fix.images key '{key}'", lineNumber, 5, key.Length));
                SkipIndentedBlock(4);
                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                index++;
                if (key == "exclude-images")
                {
                    excludeImages = ParseListBlock(4, "exclude-images");
                    continue;
                }

                if (key == "exclude-tags")
                {
                    excludeTags = ParseListBlock(4, "exclude-tags");
                    continue;
                }

                if (key == "ignore-images")
                {
                    ignoreImages = ParseListBlock(4, "ignore-images");
                    continue;
                }

                diagnostics.Add(CreateError($"unknown fix.images key '{key}'", lineNumber, 5, key.Length));
                SkipIndentedBlock(4);
                continue;
            }

            if (key == "enable-network")
            {
                if (!TryParseBool(value, out var parsed))
                {
                    diagnostics.Add(CreateError("fix.images.enable-network must be true or false", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    enableNetwork = parsed;
                }

                index++;
                continue;
            }

            diagnostics.Add(CreateError($"unknown fix.images key '{key}'", lineNumber, 5, key.Length));
            index++;
        }

        return new FixImagesConfig
        {
            EnableNetwork = enableNetwork,
            ExcludeImages = excludeImages.Count > 0 ? excludeImages : new FixImagesConfig().ExcludeImages,
            ExcludeTags = excludeTags.Count > 0 ? excludeTags : new FixImagesConfig().ExcludeTags,
            IgnoreImages = ignoreImages,
        };
    }

    void ParseNetworkSection()
    {
        var onError = NetworkErrorMode.Skip;
        var timeoutSeconds = 30;
        var maxConcurrency = 4;
        var github = new GitHubNetworkConfig();

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 0)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 2)
            {
                diagnostics.Add(CreateError("network key must be indented by 2 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                if (!TryParseKey(line, out key))
                {
                    diagnostics.Add(CreateError("network entry must be key or key: value", lineNumber, 3, line.Trim().Length));
                    index++;
                    continue;
                }

                index++;
                if (key == "github")
                {
                    github = ParseNetworkGitHubSection();
                    continue;
                }

                diagnostics.Add(CreateError($"unknown network key '{key}'", lineNumber, 3, key.Length));
                SkipIndentedBlock(2);
                continue;
            }

            if (string.IsNullOrEmpty(value) && key == "github")
            {
                index++;
                github = ParseNetworkGitHubSection();
                continue;
            }

            if (key == "on-error")
            {
                if (string.Equals(value, "skip", StringComparison.OrdinalIgnoreCase))
                {
                    onError = NetworkErrorMode.Skip;
                }
                else if (string.Equals(value, "fail", StringComparison.OrdinalIgnoreCase))
                {
                    onError = NetworkErrorMode.Fail;
                }
                else
                {
                    diagnostics.Add(CreateError("network.on-error must be 'skip' or 'fail'", lineNumber, 3, line.Trim().Length));
                }

                index++;
                continue;
            }

            if (key == "timeout-seconds")
            {
                if (!TryParseInt(value, out var parsed))
                {
                    diagnostics.Add(CreateError("network.timeout-seconds must be an integer", lineNumber, 3, line.Trim().Length));
                }
                else
                {
                    timeoutSeconds = parsed;
                }

                index++;
                continue;
            }

            if (key == "max-concurrency")
            {
                if (!TryParseInt(value, out var parsed))
                {
                    diagnostics.Add(CreateError("network.max-concurrency must be an integer", lineNumber, 3, line.Trim().Length));
                }
                else
                {
                    maxConcurrency = parsed;
                }

                index++;
                continue;
            }

            diagnostics.Add(CreateError($"unknown network key '{key}'", lineNumber, 3, key.Length));
            index++;
        }

        network = new NetworkConfig
        {
            OnError = onError,
            TimeoutSeconds = timeoutSeconds,
            MaxConcurrency = maxConcurrency,
            GitHub = github,
        };
    }

    GitHubNetworkConfig ParseNetworkGitHubSection()
    {
        string? ghesApiUrl = null;
        var ghesFallback = false;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 2)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 4)
            {
                diagnostics.Add(CreateError("network.github key must be indented by 4 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                diagnostics.Add(CreateError("network.github entry must be key: value", lineNumber, 5, line.Trim().Length));
                index++;
                continue;
            }

            if (key == "ghes-api-url")
            {
                ghesApiUrl = Unquote(value);
                index++;
                continue;
            }

            if (key == "ghes-fallback")
            {
                if (!TryParseBool(value, out var parsed))
                {
                    diagnostics.Add(CreateError("network.github.ghes-fallback must be true or false", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    ghesFallback = parsed;
                }

                index++;
                continue;
            }

            diagnostics.Add(CreateError($"unknown network.github key '{key}'", lineNumber, 5, key.Length));
            index++;
        }

        return new GitHubNetworkConfig
        {
            GhesApiUrl = ghesApiUrl,
            GhesFallback = ghesFallback,
        };
    }

    IReadOnlyList<IgnoreActionEntry> ParseIgnoreActionsList(int parentIndent)
    {
        var result = new List<IgnoreActionEntry>();

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= parentIndent)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != parentIndent + 2)
            {
                diagnostics.Add(CreateError("ignore-actions list entry must be indented by 6 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                diagnostics.Add(CreateError("ignore-actions must be a YAML list", lineNumber, parentIndent + 3, trimmed.Length));
                index++;
                continue;
            }

            string? name = null;
            string? reference = null;
            var inline = trimmed[1..].Trim();
            if (inline.Length > 0)
            {
                if (!TryParseProperty(inline, out var inlineKey, out var inlineValue))
                {
                    diagnostics.Add(CreateError("ignore-actions item must be 'uses: ...' or 'ref: ...'", lineNumber, parentIndent + 3, trimmed.Length));
                }
                else if (inlineKey == "uses")
                {
                    name = Unquote(inlineValue);
                }
                else if (inlineKey == "ref")
                {
                    reference = Unquote(inlineValue);
                }
                else
                {
                    diagnostics.Add(CreateError($"unknown ignore-actions inline key '{inlineKey}'", lineNumber, parentIndent + 3, inlineKey.Length));
                }
            }

            index++;
            while (index < lines.Length)
            {
                var subLine = lines[index];
                if (TrySkip(subLine))
                {
                    index++;
                    continue;
                }

                var subIndent = GetIndent(subLine);
                if (subIndent <= parentIndent + 2)
                {
                    break;
                }

                if (subIndent != parentIndent + 4)
                {
                    diagnostics.Add(CreateError("ignore-actions fields must be indented by 8 spaces", index + 1, subIndent + 1, subLine.Trim().Length));
                    index++;
                    continue;
                }

                if (!TryParseProperty(subLine, out var key, out var value))
                {
                    diagnostics.Add(CreateError("ignore-actions field must be key: value", index + 1, parentIndent + 5, subLine.Trim().Length));
                    index++;
                    continue;
                }

                if (key == "uses")
                {
                    name = Unquote(value);
                    index++;
                    continue;
                }

                if (key == "ref")
                {
                    reference = Unquote(value);
                    index++;
                    continue;
                }

                diagnostics.Add(CreateError($"unknown ignore-actions key '{key}'", index + 1, parentIndent + 5, key.Length));
                index++;
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(reference))
            {
                diagnostics.Add(CreateError("ignore-actions requires both uses and ref", lineNumber, parentIndent + 3, trimmed.Length));
                continue;
            }

            result.Add(new IgnoreActionEntry(name, reference));
        }

        return result;
    }

    List<string> ParseListBlock(int parentIndent, string keyName)
    {
        var result = new List<string>();

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= parentIndent)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != parentIndent + 2)
            {
                diagnostics.Add(CreateError($"{keyName} list entry must be indented by {parentIndent + 2} spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                diagnostics.Add(CreateError($"{keyName} must be a YAML list", lineNumber, parentIndent + 3, trimmed.Length));
                index++;
                continue;
            }

            var value = trimmed[1..].Trim();
            result.Add(Unquote(value));
            index++;
        }

        return result;
    }

    void ParseExclusionsSection()
    {
        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 0)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 2)
            {
                diagnostics.Add(CreateError("exclusion entry must be indented by 2 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                diagnostics.Add(CreateError("exclusions must be a list", lineNumber, 3, trimmed.Length));
                index++;
                continue;
            }

            // Handle inline content after "- " (e.g. "- files: .github/workflows/*.yml")
            string? inlineKey = null;
            string? inlineValue = null;
            var inlineContent = trimmed[1..].Trim();
            if (inlineContent.Length > 0 && TryParseProperty(inlineContent, out var ik, out var iv))
            {
                inlineKey = ik;
                inlineValue = iv;
            }

            index++;
            ParseExclusionItem(lineNumber, inlineKey, inlineValue);
        }
    }

    void ParseExclusionItem(int lineNumber, string? inlineKey = null, string? inlineValue = null)
    {
        string? files = null;
        IReadOnlyList<string> rulesList = [];
        IReadOnlyList<string> jobsList = [];

        // Apply inline key-value from the "- key: value" line
        if (inlineKey is not null)
        {
            if (inlineKey == "files")
            {
                files = Unquote(inlineValue ?? string.Empty);
            }
            else
            {
                diagnostics.Add(CreateError($"unknown exclusion field '{inlineKey}'", lineNumber, 3, inlineKey.Length));
            }
        }

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 2)
            {
                break;
            }

            if (indent != 4)
            {
                diagnostics.Add(CreateError("exclusion field must be indented by 4 spaces", index + 1, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                diagnostics.Add(CreateError("exclusion field must be key: value", index + 1, 5, line.Trim().Length));
                index++;
                continue;
            }

            if (key == "files")
            {
                files = Unquote(value);
                index++;
                continue;
            }

            if (key == "rules")
            {
                index++;
                rulesList = ParseListBlock(4, "rules");
                continue;
            }

            if (key == "jobs")
            {
                index++;
                jobsList = ParseListBlock(4, "jobs");
                continue;
            }

            diagnostics.Add(CreateError($"unknown exclusion field '{key}'", index + 1, 5, key.Length));
            index++;
        }

        if (string.IsNullOrWhiteSpace(files))
        {
            diagnostics.Add(CreateError("exclusion files is required", lineNumber, 3, 1));
            return;
        }

        if (rulesList.Count == 0)
        {
            diagnostics.Add(CreateError("exclusion rules is required", lineNumber, 3, 1));
            return;
        }

        exclusions.Add(new LintExclusion(files, rulesList, jobsList.Count > 0 ? jobsList : null));
    }

    void SkipIndentedBlock(int parentIndent)
    {
        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            if (GetIndent(line) <= parentIndent)
            {
                break;
            }

            index++;
        }
    }

    Diagnostic CreateError(string message, int line, int column, int length)
    {
        var safeLength = Math.Max(length, 1);
        return new Diagnostic(
            DiagnosticSeverity.Error,
            message,
            new TextRange(0, safeLength, line, column, line, column + safeLength),
            FilePath: filePath);
    }

    static bool TrySkip(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var trimmed = line.TrimStart();
        return trimmed.StartsWith("#", StringComparison.Ordinal);
    }

    static int GetIndent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    static bool TryParseKey(string line, out string key)
    {
        key = string.Empty;

        var trimmed = line.Trim();
        if (!trimmed.EndsWith(':'))
        {
            return false;
        }

        key = Unquote(trimmed[..^1].Trim());
        return key.Length > 0;
    }

    static bool TryParseProperty(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var trimmed = line.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        key = Unquote(trimmed[..colon].Trim());
        value = trimmed[(colon + 1)..].Trim();
        return key.Length > 0;
    }

    static bool TryParseBool(string value, out bool result)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = default;
        return false;
    }

    static bool TryParseSeverity(string value, out DiagnosticSeverity severity)
    {
        if (string.Equals(value, "info", StringComparison.OrdinalIgnoreCase))
        {
            severity = DiagnosticSeverity.Info;
            return true;
        }

        if (string.Equals(value, "warning", StringComparison.OrdinalIgnoreCase))
        {
            severity = DiagnosticSeverity.Warning;
            return true;
        }

        if (string.Equals(value, "error", StringComparison.OrdinalIgnoreCase))
        {
            severity = DiagnosticSeverity.Error;
            return true;
        }

        severity = default;
        return false;
    }

    static bool TryParseInt(string value, out int result)
    {
        return int.TryParse(value, out result);
    }

    static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            if ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"'))
            {
                return value[1..^1];
            }
        }

        return value;
    }

    internal readonly record struct ParseResult(
        Dictionary<string, RuleConfig> Rules,
        List<LintExclusion> Exclusions,
        FixConfig Fix,
        NetworkConfig Network,
        Diagnostic[] Diagnostics);
}

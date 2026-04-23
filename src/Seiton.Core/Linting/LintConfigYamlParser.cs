using System.Collections;
using System.Globalization;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using VYaml.Serialization;

namespace Seiton.Core.Linting;

/// <summary>
/// Parses lint configuration YAML into <see cref="LintConfigParseResult"/>.
/// VYaml dynamic deserialization is used internally; the untyped DOM never leaves this class.
/// </summary>
internal static class LintConfigYamlParser
{
    private const int DomLine = 1;

    /// <summary>Parses lint configuration YAML bytes into a <see cref="LintConfigParseResult"/>.</summary>
    public static LintConfigParseResult Parse(ReadOnlyMemory<byte> utf8Yaml, string filePath)
    {
        Dictionary<string, object?> root;
        try
        {
            root = YamlSerializer.Deserialize<Dictionary<string, object?>>(utf8Yaml)
                ?? new Dictionary<string, object?>();
        }
        catch (Exception ex)
        {
            var d = new Diagnostic(
                DiagnosticSeverity.Error,
                $"invalid lint config YAML: {ex.Message}",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath);
            return new LintConfigParseResult(
                new Dictionary<string, RuleConfig>(StringComparer.OrdinalIgnoreCase),
                [],
                new FixConfig(),
                new NetworkConfig(),
                [d]);
        }

        return Convert(root, filePath);
    }

    private static LintConfigParseResult Convert(Dictionary<string, object?> root, string filePath)
    {
        var diagnostics = new List<Diagnostic>();
        var rules = new Dictionary<string, RuleConfig>(StringComparer.OrdinalIgnoreCase);
        var exclusions = new List<LintExclusion>();
        var fix = new FixConfig();
        var network = new NetworkConfig();

        foreach (var key in root.Keys)
        {
            if (key is not null
                && !string.Equals(key, "rules", StringComparison.Ordinal)
                && !string.Equals(key, "exclusions", StringComparison.Ordinal)
                && !string.Equals(key, "fix", StringComparison.Ordinal)
                && !string.Equals(key, "network", StringComparison.Ordinal))
            {
                diagnostics.Add(Diag(
                    $"unknown top-level key '{key}'",
                    DomLine,
                    1,
                    key.Length,
                    filePath));
            }
        }

        if (root.TryGetValue("rules", out var rulesObj) && rulesObj is not null)
        {
            if (AsMap(rulesObj) is not { } rulesMap)
            {
                diagnostics.Add(Diag("rules must be a mapping section", DomLine, 1, 5, filePath));
            }
            else
            {
                foreach (var pair in rulesMap)
                {
                    var ruleId = pair.Key;
                    if (string.IsNullOrEmpty(ruleId))
                    {
                        continue;
                    }

                    AddRule(ruleId, pair.Value, rules, diagnostics, filePath);
                }
            }
        }

        if (root.TryGetValue("exclusions", out var exObj) && exObj is not null)
        {
            if (AsList(exObj) is not { } exList)
            {
                diagnostics.Add(Diag("exclusions must be a sequence section", DomLine, 1, 10, filePath));
            }
            else
            {
                for (var i = 0; i < exList.Count; i++)
                {
                    AddExclusion(exList[i], exclusions, diagnostics, filePath);
                }
            }
        }

        if (root.TryGetValue("fix", out var fixObj) && fixObj is not null)
        {
            if (AsMap(fixObj) is not { } fixMap)
            {
                diagnostics.Add(Diag("fix must be a mapping section", DomLine, 1, 3, filePath));
            }
            else
            {
                fix = ParseFix(fixMap, diagnostics, filePath);
            }
        }

        if (root.TryGetValue("network", out var netObj) && netObj is not null)
        {
            if (AsMap(netObj) is not { } netMap)
            {
                diagnostics.Add(Diag("network must be a mapping section", DomLine, 1, 7, filePath));
            }
            else
            {
                network = ParseNetwork(netMap, diagnostics, filePath);
            }
        }

        return new LintConfigParseResult(rules, exclusions, fix, network, diagnostics.ToArray());
    }

    private static void AddRule(
        string ruleId,
        object? bodyObj,
        Dictionary<string, RuleConfig> rules,
        List<Diagnostic> diagnostics,
        string filePath)
    {
        if (AsMap(bodyObj) is not { } body)
        {
            diagnostics.Add(Diag("rule entry must be a mapping", DomLine, 3, ruleId.Length, filePath));
            return;
        }

        var enabled = true;
        DiagnosticSeverity? severity = null;
        ExtendableList? events = null;
        ExtendableList? knownHostedLabels = null;
        ExtendableList? publicRegistries = null;
        ExtendableList? untrustedTriggers = null;
        ExtendableList? outputCommands = null;
        IReadOnlyList<string>? assumeEvents = null;
        IReadOnlyList<string>? allow = null;
        IReadOnlyList<string>? deny = null;
        int? maxStepEnvSecrets = null;
        int? maxJobSecrets = null;
        var seenRuleSpecificKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, value) in body)
        {
            switch (key)
            {
                case "enabled":
                    if (!TryCoerceBool(value, out var en))
                    {
                        diagnostics.Add(Diag("enabled must be true or false", DomLine, 5, 7, filePath));
                    }
                    else
                    {
                        enabled = en;
                    }

                    break;
                case "severity":
                    if (!TryParseSeverity(ScalarToString(value), out var sev))
                    {
                        diagnostics.Add(Diag("severity must be one of info, warning, error", DomLine, 5, 8, filePath));
                    }
                    else
                    {
                        severity = sev;
                    }

                    break;
                case "events":
                    seenRuleSpecificKeys.Add("events");
                    events = ToExtendableList(ParseExtendableList(value, diagnostics, filePath));
                    break;
                case "known-hosted-labels":
                    seenRuleSpecificKeys.Add("known-hosted-labels");
                    knownHostedLabels = ToExtendableList(ParseExtendableList(value, diagnostics, filePath));
                    break;
                case "public-registries":
                    seenRuleSpecificKeys.Add("public-registries");
                    publicRegistries = ToExtendableList(ParseExtendableList(value, diagnostics, filePath));
                    break;
                case "untrusted-triggers":
                    seenRuleSpecificKeys.Add("untrusted-triggers");
                    untrustedTriggers = ToExtendableList(ParseExtendableList(value, diagnostics, filePath));
                    break;
                case "output-commands":
                    seenRuleSpecificKeys.Add("output-commands");
                    outputCommands = ToExtendableList(ParseExtendableList(value, diagnostics, filePath));
                    break;
                case "assume-events":
                    seenRuleSpecificKeys.Add("assume-events");
                    assumeEvents = NullIfEmpty(ParseStringList(value, "assume-events", diagnostics, filePath));
                    break;
                case "allow":
                    seenRuleSpecificKeys.Add("allow");
                    allow = NullIfEmpty(ParseStringList(value, "allow", diagnostics, filePath));
                    break;
                case "deny":
                    seenRuleSpecificKeys.Add("deny");
                    deny = NullIfEmpty(ParseStringList(value, "deny", diagnostics, filePath));
                    break;
                case "max-step-env-secrets":
                    seenRuleSpecificKeys.Add("max-step-env-secrets");
                    if (!TryCoerceInt(value, out var ms) || ms < 0)
                    {
                        diagnostics.Add(Diag("max-step-env-secrets must be a non-negative integer", DomLine, 5, 22, filePath));
                    }
                    else
                    {
                        maxStepEnvSecrets = ms;
                    }

                    break;
                case "max-job-secrets":
                    seenRuleSpecificKeys.Add("max-job-secrets");
                    if (!TryCoerceInt(value, out var mj) || mj < 0)
                    {
                        diagnostics.Add(Diag("max-job-secrets must be a non-negative integer", DomLine, 5, 17, filePath));
                    }
                    else
                    {
                        maxJobSecrets = mj;
                    }

                    break;
                default:
                    diagnostics.Add(Diag($"unknown rule option '{key}'", DomLine, 5, key.Length, filePath));
                    break;
            }
        }

        ValidateAllowedKeys(ruleId, seenRuleSpecificKeys, DomLine, diagnostics, filePath);

        var config = new RuleConfig
        {
            Enabled = enabled,
            Severity = severity,
            Events = events,
            KnownHostedLabels = knownHostedLabels,
            PublicRegistries = publicRegistries,
            UntrustedTriggers = untrustedTriggers,
            OutputCommands = outputCommands,
            AssumeEvents = assumeEvents,
            Allow = allow,
            Deny = deny,
            MaxStepEnvSecrets = maxStepEnvSecrets,
            MaxJobSecrets = maxJobSecrets,
        };

        if (!rules.TryAdd(ruleId, config))
        {
            diagnostics.Add(Diag($"duplicate rule entry '{ruleId}'", DomLine, 3, ruleId.Length, filePath));
        }
    }

    private static IReadOnlyList<string> ParseExtendableList(object? value, List<Diagnostic> diagnostics, string filePath)
    {
        if (AsMap(value) is not { } map)
        {
            diagnostics.Add(Diag("extend key must be properly indented", DomLine, 5, 1, filePath));
            return [];
        }

        if (!map.TryGetValue("extend", out var extObj))
        {
            diagnostics.Add(Diag("expected 'extend' key", DomLine, 5, 6, filePath));
            return [];
        }

        return ParseStringList(extObj, "extend", diagnostics, filePath);
    }

    private static IReadOnlyList<string> ParseStringList(
        object? value,
        string keyName,
        List<Diagnostic> diagnostics,
        string filePath)
    {
        if (AsList(value) is not { } list)
        {
            diagnostics.Add(Diag($"{keyName} must be a YAML list", DomLine, 5, keyName.Length, filePath));
            return [];
        }

        var result = new List<string>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            result.Add(Unquote(ScalarToString(list[i])));
        }

        return result;
    }

    private static ExtendableList? ToExtendableList(IReadOnlyList<string> values)
    {
        return values.Count > 0 ? new ExtendableList(values) : null;
    }

    private static IReadOnlyList<string>? NullIfEmpty(IReadOnlyList<string> values)
    {
        return values.Count > 0 ? values : null;
    }

    private static void ValidateAllowedKeys(
        string ruleId,
        IReadOnlySet<string> seenKeys,
        int lineNumber,
        List<Diagnostic> diagnostics,
        string filePath)
    {
        if (!RuleCatalog.TryResolveRuleId(ruleId, out var resolvedRuleId))
        {
            return;
        }

        if (!RuleCatalog.TryGetAllowedConfigKeys(resolvedRuleId, out var allowed))
        {
            return;
        }

        foreach (var key in seenKeys)
        {
            if (!allowed.Contains(key))
            {
                diagnostics.Add(Diag(
                    $"rule '{resolvedRuleId.ToId()}' does not accept '{key}' config key",
                    lineNumber,
                    3,
                    key.Length,
                    filePath));
            }
        }
    }

    private static FixConfig ParseFix(Dictionary<string, object?> map, List<Diagnostic> diagnostics, string filePath)
    {
        var defaults = new FixDefaultsConfig();
        var pinning = new FixPinningConfig();
        var images = new FixImagesConfig();

        foreach (var (key, value) in map)
        {
            switch (key)
            {
                case "defaults":
                    if (AsMap(value) is { } dm)
                    {
                        defaults = ParseFixDefaults(dm, diagnostics, filePath);
                    }

                    break;
                case "pinning":
                    if (AsMap(value) is { } pm)
                    {
                        pinning = ParseFixPinning(pm, diagnostics, filePath);
                    }

                    break;
                case "images":
                    if (AsMap(value) is { } im)
                    {
                        images = ParseFixImages(im, diagnostics, filePath);
                    }

                    break;
                default:
                    diagnostics.Add(Diag($"unknown fix key '{key}'", DomLine, 3, key.Length, filePath));
                    break;
            }
        }

        return new FixConfig
        {
            Defaults = defaults,
            Pinning = pinning,
            Images = images,
        };
    }

    private static FixDefaultsConfig ParseFixDefaults(Dictionary<string, object?> map, List<Diagnostic> diagnostics, string filePath)
    {
        int? jobTimeoutMinutes = null;
        foreach (var (key, value) in map)
        {
            if (key == "job-timeout-minutes")
            {
                if (!TryCoerceInt(value, out var jt))
                {
                    diagnostics.Add(Diag("fix.defaults.job-timeout-minutes must be an integer", DomLine, 5, 22, filePath));
                }
                else
                {
                    jobTimeoutMinutes = jt;
                }
            }
            else
            {
                diagnostics.Add(Diag($"unknown fix.defaults key '{key}'", DomLine, 5, key.Length, filePath));
            }
        }

        return new FixDefaultsConfig { JobTimeoutMinutes = jobTimeoutMinutes };
    }

    private static FixPinningConfig ParseFixPinning(Dictionary<string, object?> map, List<Diagnostic> diagnostics, string filePath)
    {
        var enableNetwork = false;
        var minAgeDays = 14;
        IReadOnlyList<string> excludeBranches = [];
        IReadOnlyList<IgnoreActionEntry> ignoreActions = [];

        foreach (var (key, value) in map)
        {
            switch (key)
            {
                case "enable-network":
                    if (!TryCoerceBool(value, out var en))
                    {
                        diagnostics.Add(Diag("fix.pinning.enable-network must be true or false", DomLine, 5, 15, filePath));
                    }
                    else
                    {
                        enableNetwork = en;
                    }

                    break;
                case "min-age-days":
                    if (!TryCoerceInt(value, out var md))
                    {
                        diagnostics.Add(Diag("fix.pinning.min-age-days must be an integer", DomLine, 5, 14, filePath));
                    }
                    else
                    {
                        minAgeDays = md;
                    }

                    break;
                case "exclude-branches":
                    excludeBranches = ParseStringList(value, "exclude-branches", diagnostics, filePath);
                    break;
                case "ignore-actions":
                    ignoreActions = ParseIgnoreActions(value, diagnostics, filePath);
                    break;
                default:
                    diagnostics.Add(Diag($"unknown fix.pinning key '{key}'", DomLine, 5, key.Length, filePath));
                    break;
            }
        }

        return new FixPinningConfig
        {
            EnableNetwork = enableNetwork,
            MinAgeDays = minAgeDays,
            ExcludeBranches = excludeBranches.Count > 0 ? excludeBranches : new FixPinningConfig().ExcludeBranches,
            IgnoreActions = ignoreActions,
        };
    }

    private static FixImagesConfig ParseFixImages(Dictionary<string, object?> map, List<Diagnostic> diagnostics, string filePath)
    {
        var enableNetwork = false;
        IReadOnlyList<string> excludeImages = [];
        IReadOnlyList<string> excludeTags = [];
        IReadOnlyList<string> ignoreImages = [];

        foreach (var (key, value) in map)
        {
            switch (key)
            {
                case "enable-network":
                    if (!TryCoerceBool(value, out var en))
                    {
                        diagnostics.Add(Diag("fix.images.enable-network must be true or false", DomLine, 5, 15, filePath));
                    }
                    else
                    {
                        enableNetwork = en;
                    }

                    break;
                case "exclude-images":
                    excludeImages = ParseStringList(value, "exclude-images", diagnostics, filePath);
                    break;
                case "exclude-tags":
                    excludeTags = ParseStringList(value, "exclude-tags", diagnostics, filePath);
                    break;
                case "ignore-images":
                    ignoreImages = ParseStringList(value, "ignore-images", diagnostics, filePath);
                    break;
                default:
                    diagnostics.Add(Diag($"unknown fix.images key '{key}'", DomLine, 5, key.Length, filePath));
                    break;
            }
        }

        return new FixImagesConfig
        {
            EnableNetwork = enableNetwork,
            ExcludeImages = excludeImages.Count > 0 ? excludeImages : new FixImagesConfig().ExcludeImages,
            ExcludeTags = excludeTags.Count > 0 ? excludeTags : new FixImagesConfig().ExcludeTags,
            IgnoreImages = ignoreImages,
        };
    }

    private static IReadOnlyList<IgnoreActionEntry> ParseIgnoreActions(
        object? value,
        List<Diagnostic> diagnostics,
        string filePath)
    {
        if (AsList(value) is not { } list)
        {
            diagnostics.Add(Diag("ignore-actions must be a YAML list", DomLine, 5, 14, filePath));
            return [];
        }

        var result = new List<IgnoreActionEntry>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            if (AsMap(list[i]) is not { } item)
            {
                diagnostics.Add(Diag("ignore-actions item must be a mapping with uses and ref", DomLine, 5, 14, filePath));
                continue;
            }

            string? name = null;
            string? reference = null;
            foreach (var (ik, iv) in item)
            {
                if (ik == "uses")
                {
                    name = Unquote(ScalarToString(iv));
                }
                else if (ik == "ref")
                {
                    reference = Unquote(ScalarToString(iv));
                }
                else
                {
                    diagnostics.Add(Diag($"unknown ignore-actions key '{ik}'", DomLine, 5, ik.Length, filePath));
                }
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(reference))
            {
                diagnostics.Add(Diag("ignore-actions requires both uses and ref", DomLine, 5, 14, filePath));
                continue;
            }

            result.Add(new IgnoreActionEntry(name, reference));
        }

        return result;
    }

    private static NetworkConfig ParseNetwork(Dictionary<string, object?> map, List<Diagnostic> diagnostics, string filePath)
    {
        var onError = NetworkErrorMode.Skip;
        var timeoutSeconds = 30;
        var maxConcurrency = 4;
        var github = new GitHubNetworkConfig();

        foreach (var (key, value) in map)
        {
            switch (key)
            {
                case "on-error":
                    var os = ScalarToString(value);
                    if (string.Equals(os, "skip", StringComparison.OrdinalIgnoreCase))
                    {
                        onError = NetworkErrorMode.Skip;
                    }
                    else if (string.Equals(os, "fail", StringComparison.OrdinalIgnoreCase))
                    {
                        onError = NetworkErrorMode.Fail;
                    }
                    else
                    {
                        diagnostics.Add(Diag("network.on-error must be 'skip' or 'fail'", DomLine, 3, 8, filePath));
                    }

                    break;
                case "timeout-seconds":
                    if (!TryCoerceInt(value, out var ts))
                    {
                        diagnostics.Add(Diag("network.timeout-seconds must be an integer", DomLine, 3, 17, filePath));
                    }
                    else
                    {
                        timeoutSeconds = ts;
                    }

                    break;
                case "max-concurrency":
                    if (!TryCoerceInt(value, out var mc))
                    {
                        diagnostics.Add(Diag("network.max-concurrency must be an integer", DomLine, 3, 15, filePath));
                    }
                    else
                    {
                        maxConcurrency = mc;
                    }

                    break;
                case "github":
                    if (AsMap(value) is { } gm)
                    {
                        github = ParseNetworkGitHub(gm, diagnostics, filePath);
                    }

                    break;
                default:
                    diagnostics.Add(Diag($"unknown network key '{key}'", DomLine, 3, key.Length, filePath));
                    break;
            }
        }

        return new NetworkConfig
        {
            OnError = onError,
            TimeoutSeconds = timeoutSeconds,
            MaxConcurrency = maxConcurrency,
            GitHub = github,
        };
    }

    private static GitHubNetworkConfig ParseNetworkGitHub(Dictionary<string, object?> map, List<Diagnostic> diagnostics, string filePath)
    {
        string? ghesApiUrl = null;
        var ghesFallback = false;
        foreach (var (key, value) in map)
        {
            if (key == "ghes-api-url")
            {
                ghesApiUrl = Unquote(ScalarToString(value));
            }
            else if (key == "ghes-fallback")
            {
                if (!TryCoerceBool(value, out var gf))
                {
                    diagnostics.Add(Diag("network.github.ghes-fallback must be true or false", DomLine, 5, 14, filePath));
                }
                else
                {
                    ghesFallback = gf;
                }
            }
            else
            {
                diagnostics.Add(Diag($"unknown network.github key '{key}'", DomLine, 5, key.Length, filePath));
            }
        }

        return new GitHubNetworkConfig { GhesApiUrl = ghesApiUrl, GhesFallback = ghesFallback };
    }

    private static void AddExclusion(
        object? itemObj,
        List<LintExclusion> exclusions,
        List<Diagnostic> diagnostics,
        string filePath)
    {
        if (AsMap(itemObj) is not { } item)
        {
            diagnostics.Add(Diag("exclusion entry must be a mapping", DomLine, 3, 1, filePath));
            return;
        }

        string? files = null;
        IReadOnlyList<string> rulesList = [];
        IReadOnlyList<string> jobsList = [];

        foreach (var (key, value) in item)
        {
            if (key == "files")
            {
                files = Unquote(ScalarToString(value));
            }
            else if (key == "rules")
            {
                rulesList = ParseStringList(value, "rules", diagnostics, filePath);
            }
            else if (key == "jobs")
            {
                jobsList = ParseStringList(value, "jobs", diagnostics, filePath);
            }
            else
            {
                diagnostics.Add(Diag($"unknown exclusion field '{key}'", DomLine, 5, key.Length, filePath));
            }
        }

        if (string.IsNullOrWhiteSpace(files))
        {
            diagnostics.Add(Diag("exclusion files is required", DomLine, 3, 1, filePath));
            return;
        }

        if (rulesList.Count == 0)
        {
            diagnostics.Add(Diag("exclusion rules is required", DomLine, 3, 1, filePath));
            return;
        }

        exclusions.Add(new LintExclusion(files, rulesList, jobsList.Count > 0 ? jobsList : null));
    }

    private static Dictionary<string, object?>? AsMap(object? o)
    {
        if (o is null)
        {
            return null;
        }

        if (o is Dictionary<string, object?> d0)
        {
            return d0;
        }

        if (o is Dictionary<string, object> d1)
        {
            var r = new Dictionary<string, object?>(d1.Count, StringComparer.Ordinal);
            foreach (var p in d1)
            {
                r[p.Key] = p.Value;
            }

            return r;
        }

        if (o is IDictionary idict)
        {
            var r = new Dictionary<string, object?>(idict.Count, StringComparer.Ordinal);
            foreach (DictionaryEntry e in idict)
            {
                var k = e.Key?.ToString();
                if (!string.IsNullOrEmpty(k))
                {
                    r[k] = e.Value;
                }
            }

            return r;
        }

        return null;
    }

    private static List<object?>? AsList(object? o)
    {
        if (o is null)
        {
            return null;
        }

        if (o is List<object?> L0)
        {
            return L0;
        }

        if (o is object[] arr)
        {
            return [..arr];
        }

        if (o is IList il)
        {
            var r = new List<object?>(il.Count);
            for (var i = 0; i < il.Count; i++)
            {
                r.Add(il[i]);
            }

            return r;
        }

        return null;
    }

    private static string ScalarToString(object? o) => o switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        uint ui => ui.ToString(CultureInfo.InvariantCulture),
        ulong ul => ul.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        _ => o.ToString() ?? string.Empty,
    };

    private static bool TryCoerceBool(object? o, out bool value)
    {
        switch (o)
        {
            case bool b:
                value = b;
                return true;
            case string s:
                return TryParseBool(s, out value);
            default:
                value = default;
                return false;
        }
    }

    private static bool TryParseBool(string value, out bool result)
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

    private static bool TryCoerceInt(object? o, out int value)
    {
        switch (o)
        {
            case int i:
                value = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                value = (int)l;
                return true;
            case uint ui when ui <= int.MaxValue:
                value = (int)ui;
                return true;
            case string s:
                return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                value = default;
                return false;
        }
    }

    private static bool TryParseSeverity(string value, out DiagnosticSeverity severity)
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

    private static string Unquote(string value)
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

    private static Diagnostic Diag(string message, int line, int column, int length, string filePath)
    {
        var safeLength = Math.Max(length, 1);
        return new Diagnostic(
            DiagnosticSeverity.Error,
            message,
            new TextRange(0, safeLength, line, column, line, column + safeLength),
            FilePath: filePath);
    }
}

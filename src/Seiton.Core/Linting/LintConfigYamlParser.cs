using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using VYaml.Parser;

namespace Seiton.Core.Linting;

/// <summary>
/// Parses lint configuration YAML into <see cref="LintConfigParseResult"/>.
/// VYaml dynamic deserialization is used internally; the untyped DOM never leaves this class.
/// </summary>
internal static class LintConfigYamlParser
{
    private const int DomLine = 1;

    private static readonly FixConfig DefaultFix = new();
    private static readonly NetworkConfig DefaultNetwork = new();

    private sealed class YamlDomParseLimiter
    {
        private int _depth;
        private int _units;

        public void EnterCompound()
        {
            if (++_depth > LintConfigResourceLimits.MaxYamlNestDepth)
            {
                throw new InvalidOperationException(
                    $"lint config YAML exceeds maximum nesting depth ({LintConfigResourceLimits.MaxYamlNestDepth})");
            }

            RecordStructuralUnit();
        }

        public void LeaveCompound() => _depth--;

        public void RecordLeaf() => RecordStructuralUnit();

        private void RecordStructuralUnit()
        {
            if (++_units > LintConfigResourceLimits.MaxYamlDomUnits)
            {
                throw new InvalidOperationException(
                    $"lint config YAML exceeds maximum structural size ({LintConfigResourceLimits.MaxYamlDomUnits} units)");
            }
        }
    }

    /// <summary>
    /// Single source of truth for flag↔YAML key name mapping.
    /// When adding a new rule-specific key, add a row here and a corresponding case in AddRule().
    /// </summary>
    private static readonly (RuleKeyFlags Flag, string KeyName)[] RuleKeyFlagEntries =
    [
        (RuleKeyFlags.Events, "events"),
        (RuleKeyFlags.KnownHostedLabels, "known-hosted-labels"),
        (RuleKeyFlags.PublicRegistries, "public-registries"),
        (RuleKeyFlags.UntrustedTriggers, "untrusted-triggers"),
        (RuleKeyFlags.OutputCommands, "output-commands"),
        (RuleKeyFlags.AssumeEvents, "assume-events"),
        (RuleKeyFlags.Allow, "allow"),
        (RuleKeyFlags.Deny, "deny"),
        (RuleKeyFlags.MaxStepEnvSecrets, "max-step-env-secrets"),
        (RuleKeyFlags.MaxJobSecrets, "max-job-secrets"),
        (RuleKeyFlags.IgnoreActions, "ignore-actions"),
        (RuleKeyFlags.FixMapping, "fix-mapping"),
    ];

    /// <summary>Parses lint configuration YAML bytes into a <see cref="LintConfigParseResult"/>.</summary>
    public static LintConfigParseResult Parse(ReadOnlyMemory<byte> utf8Yaml, string filePath)
    {
        Dictionary<string, object?> root;
        try
        {
            root = ParseYamlDom(utf8Yaml) ?? new Dictionary<string, object?>();
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
                new OutputConfig(),
                [d]);
        }

        return Convert(root, filePath);
    }

    /// <summary>
    /// Builds an untyped DOM (Dictionary/List/string/bool/int/etc.) from YAML bytes
    /// using VYaml's pull parser. This is AOT-safe unlike <c>YamlSerializer.Deserialize</c>.
    /// </summary>
    private static Dictionary<string, object?>? ParseYamlDom(ReadOnlyMemory<byte> utf8Yaml)
    {
        // YamlParser.FromBytes requires Memory<byte>. When callers pass array-backed ReadOnlyMemory
        // from LintConfigLibrary (same backing array as LintConfig.Utf8Yaml), parse in-place —
        // VYaml does not mutate the UTF-8 source (same invariant as workflow VYamlStreamAdapter).
        Memory<byte> parserMemory;
        byte[]? poolBuffer = null;
        if (MemoryMarshal.TryGetArray(utf8Yaml, out var segment) && segment.Array is not null)
        {
            parserMemory = segment.Array.AsMemory(segment.Offset, segment.Count);
        }
        else
        {
            poolBuffer = ArrayPool<byte>.Shared.Rent(utf8Yaml.Length);
            utf8Yaml.Span.CopyTo(poolBuffer.AsSpan(0, utf8Yaml.Length));
            parserMemory = poolBuffer.AsMemory(0, utf8Yaml.Length);
        }

        YamlParser parser;
        try
        {
            parser = YamlParser.FromBytes(parserMemory);
        }
        catch
        {
            if (poolBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(poolBuffer);
            }

            throw;
        }

        try
        {
            // VYaml event sequence: StreamStart → DocumentStart → content → DocumentEnd → StreamEnd
            // Advance past StreamStart
            if (!parser.Read() || parser.CurrentEventType == ParseEventType.StreamEnd)
            {
                return null;
            }

            // Advance past DocumentStart
            if (!parser.Read() || parser.CurrentEventType == ParseEventType.StreamEnd)
            {
                return null;
            }

            // Advance to first content event (MappingStart, SequenceStart, or Scalar)
            if (!parser.Read() || parser.CurrentEventType is ParseEventType.DocumentEnd or ParseEventType.StreamEnd)
            {
                return null;
            }

            var limiter = new YamlDomParseLimiter();
            var result = ReadValue(ref parser, limiter);
            return result as Dictionary<string, object?>;
        }
        finally
        {
            if (poolBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(poolBuffer);
            }
        }
    }

    private static object? ReadValue(ref YamlParser parser, YamlDomParseLimiter limiter)
    {
        return parser.CurrentEventType switch
        {
            ParseEventType.MappingStart => ReadMapping(ref parser, limiter),
            ParseEventType.SequenceStart => ReadSequence(ref parser, limiter),
            ParseEventType.Scalar => ReadScalar(ref parser, limiter),
            _ => null,
        };
    }

    private static Dictionary<string, object?> ReadMapping(ref YamlParser parser, YamlDomParseLimiter limiter)
    {
        // Skip MappingStart
        parser.Read();
        limiter.EnterCompound();
        try
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (parser.CurrentEventType != ParseEventType.MappingEnd)
            {
                var key = ReadMappingKeyScalar(ref parser, limiter);
                parser.Read();
                var value = ReadValue(ref parser, limiter);
                if (key is not null)
                {
                    map[key] = value;
                }
            }

            // Skip MappingEnd
            parser.Read();
            return map;
        }
        finally
        {
            limiter.LeaveCompound();
        }
    }

    private static List<object?> ReadSequence(ref YamlParser parser, YamlDomParseLimiter limiter)
    {
        // Skip SequenceStart
        parser.Read();
        limiter.EnterCompound();
        try
        {
            var list = new List<object?>();
            while (parser.CurrentEventType != ParseEventType.SequenceEnd)
            {
                list.Add(ReadValue(ref parser, limiter));
            }

            // Skip SequenceEnd
            parser.Read();
            return list;
        }
        finally
        {
            limiter.LeaveCompound();
        }
    }

    private static object? ReadScalar(ref YamlParser parser, YamlDomParseLimiter limiter)
    {
        limiter.RecordLeaf();
        var result = ReadScalarValue(ref parser);
        parser.Read();
        return result;
    }

    private static object? ReadScalarValue(ref YamlParser parser)
    {
        if (parser.IsNullScalar())
        {
            return null;
        }

        if (parser.TryGetScalarAsBool(out var boolValue))
        {
            return boolValue;
        }

        if (parser.TryGetScalarAsInt32(out var intValue))
        {
            return intValue;
        }

        if (parser.TryGetScalarAsInt64(out var longValue))
        {
            return longValue;
        }

        if (parser.TryGetScalarAsDouble(out var doubleValue))
        {
            return doubleValue;
        }

        return DecodeScalarStringUtf8(ref parser);
    }

    private static string DecodeScalarStringUtf8(ref YamlParser parser)
    {
        var utf8 = parser.GetScalarAsUtf8();
        return Encoding.UTF8.GetString(utf8);
    }

    private static string? ReadMappingKeyScalar(ref YamlParser parser, YamlDomParseLimiter limiter)
    {
        limiter.RecordLeaf();
        if (parser.IsNullScalar())
        {
            return null;
        }

        return DecodeScalarStringUtf8(ref parser);
    }

    private static LintConfigParseResult Convert(Dictionary<string, object?> root, string filePath)
    {
        var diagnostics = new List<Diagnostic>();
        var rules = new Dictionary<string, RuleConfig>(StringComparer.OrdinalIgnoreCase);
        var exclusions = new List<LintExclusion>();
        var fix = DefaultFix;
        var network = DefaultNetwork;

        foreach (var key in root.Keys)
        {
            if (key is not null
                && !string.Equals(key, "rules", StringComparison.Ordinal)
                && !string.Equals(key, "exclusions", StringComparison.Ordinal)
                && !string.Equals(key, "fix", StringComparison.Ordinal)
                && !string.Equals(key, "network", StringComparison.Ordinal)
                && !string.Equals(key, "output", StringComparison.Ordinal))
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

        var output = new OutputConfig();
        if (root.TryGetValue("output", out var outputObj) && outputObj is not null)
        {
            if (AsMap(outputObj) is not { } outputMap)
            {
                diagnostics.Add(Diag("output must be a mapping section", DomLine, 1, 6, filePath));
            }
            else
            {
                output = ParseOutput(outputMap, diagnostics, filePath);
            }
        }

        return new LintConfigParseResult(rules, exclusions, fix, network, output, diagnostics.ToArray());
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
        IReadOnlyList<IgnoreActionRule>? ignoreActions = null;
        int? maxStepEnvSecrets = null;
        int? maxJobSecrets = null;
        IReadOnlyDictionary<string, string>? fixMapping = null;
        var seenKeyFlags = RuleKeyFlags.None;

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
                    seenKeyFlags |= RuleKeyFlags.Events;
                    events = ToExtendableList(ParseExtendableList(value, diagnostics, filePath));
                    break;
                case "known-hosted-labels":
                    seenKeyFlags |= RuleKeyFlags.KnownHostedLabels;
                    knownHostedLabels = ToExtendableList(ParseExtendableList(value, diagnostics, filePath));
                    break;
                case "public-registries":
                    seenKeyFlags |= RuleKeyFlags.PublicRegistries;
                    publicRegistries = ToExtendableList(ParseExtendableList(value, diagnostics, filePath));
                    break;
                case "untrusted-triggers":
                    seenKeyFlags |= RuleKeyFlags.UntrustedTriggers;
                    untrustedTriggers = ToExtendableList(ParseExtendableList(value, diagnostics, filePath));
                    break;
                case "output-commands":
                    seenKeyFlags |= RuleKeyFlags.OutputCommands;
                    outputCommands = ToExtendableList(ParseExtendableList(value, diagnostics, filePath));
                    break;
                case "assume-events":
                    seenKeyFlags |= RuleKeyFlags.AssumeEvents;
                    assumeEvents = NullIfEmpty(ParseStringList(value, "assume-events", diagnostics, filePath));
                    break;
                case "allow":
                    seenKeyFlags |= RuleKeyFlags.Allow;
                    allow = NullIfEmpty(ParseStringList(value, "allow", diagnostics, filePath));
                    break;
                case "deny":
                    seenKeyFlags |= RuleKeyFlags.Deny;
                    deny = NullIfEmpty(ParseStringList(value, "deny", diagnostics, filePath));
                    break;
                case "ignore-actions":
                    seenKeyFlags |= RuleKeyFlags.IgnoreActions;
                    ignoreActions = ParseRuleIgnoreActions(value, diagnostics, filePath);
                    break;
                case "max-step-env-secrets":
                    seenKeyFlags |= RuleKeyFlags.MaxStepEnvSecrets;
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
                    seenKeyFlags |= RuleKeyFlags.MaxJobSecrets;
                    if (!TryCoerceInt(value, out var mj) || mj < 0)
                    {
                        diagnostics.Add(Diag("max-job-secrets must be a non-negative integer", DomLine, 5, 17, filePath));
                    }
                    else
                    {
                        maxJobSecrets = mj;
                    }

                    break;
                case "fix-mapping":
                    seenKeyFlags |= RuleKeyFlags.FixMapping;
                    fixMapping = ParseFixMapping(value, diagnostics, filePath);
                    break;
                default:
                    diagnostics.Add(Diag($"unknown rule option '{key}'", DomLine, 5, key.Length, filePath));
                    break;
            }
        }

        ValidateAllowedKeys(ruleId, seenKeyFlags, DomLine, diagnostics, filePath);

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
            IgnoreActions = ignoreActions,
            MaxStepEnvSecrets = maxStepEnvSecrets,
            MaxJobSecrets = maxJobSecrets,
            FixMapping = fixMapping,
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

        var result = new string[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            result[i] = Unquote(ScalarToString(list[i]));
        }

        return result;
    }

    private static IReadOnlyList<string> ParseScalarStringList(
        object? value,
        string keyName,
        List<Diagnostic> diagnostics,
        string filePath,
        out bool allEntriesScalar)
    {
        allEntriesScalar = true;
        if (AsList(value) is not { } list)
        {
            diagnostics.Add(Diag($"{keyName} must be a YAML list", DomLine, 5, keyName.Length, filePath));
            return [];
        }

        var result = new List<string>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            if (!IsScalarValue(list[i]))
            {
                allEntriesScalar = false;
                diagnostics.Add(Diag($"{keyName} entries must be scalar values", DomLine, 5, keyName.Length, filePath));
                continue;
            }

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
        RuleKeyFlags seenFlags,
        int lineNumber,
        List<Diagnostic> diagnostics,
        string filePath)
    {
        if (seenFlags == RuleKeyFlags.None)
        {
            return;
        }

        if (!RuleCatalog.TryResolveRuleId(ruleId, out var resolvedRuleId))
        {
            return;
        }

        if (!RuleCatalog.TryGetAllowedConfigKeys(resolvedRuleId, out var allowedFlags))
        {
            return;
        }

        var disallowed = seenFlags & ~allowedFlags;
        if (disallowed == RuleKeyFlags.None)
        {
            return;
        }

        for (var i = 0; i < RuleKeyFlagEntries.Length; i++)
        {
            var (flag, keyName) = RuleKeyFlagEntries[i];
            if ((disallowed & flag) != 0)
            {
                diagnostics.Add(Diag(
                    $"rule '{resolvedRuleId.ToId()}' does not accept '{keyName}' config key",
                    lineNumber,
                    3,
                    keyName.Length,
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

    private static readonly IReadOnlyList<string> DefaultExcludeBranches = ["main", "master"];

    private static FixPinningConfig ParseFixPinning(Dictionary<string, object?> map, List<Diagnostic> diagnostics, string filePath)
    {
        var enableNetwork = false;
        var hasEnableNetwork = false;
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
                        hasEnableNetwork = true;
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
            HasEnableNetwork = hasEnableNetwork,
            MinAgeDays = minAgeDays,
            ExcludeBranches = excludeBranches.Count > 0 ? excludeBranches : DefaultExcludeBranches,
            IgnoreActions = ignoreActions,
        };
    }

    private static readonly IReadOnlyList<string> DefaultExcludeImages = ["scratch"];
    private static readonly IReadOnlyList<string> DefaultExcludeTags = ["latest"];

    private static FixImagesConfig ParseFixImages(Dictionary<string, object?> map, List<Diagnostic> diagnostics, string filePath)
    {
        var enableNetwork = false;
        var hasEnableNetwork = false;
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
                        hasEnableNetwork = true;
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
            HasEnableNetwork = hasEnableNetwork,
            ExcludeImages = excludeImages.Count > 0 ? excludeImages : DefaultExcludeImages,
            ExcludeTags = excludeTags.Count > 0 ? excludeTags : DefaultExcludeTags,
            IgnoreImages = ignoreImages,
        };
    }

    private static IReadOnlyList<IgnoreActionRule>? ParseRuleIgnoreActions(
        object? value,
        List<Diagnostic> diagnostics,
        string filePath)
    {
        if (AsList(value) is not { } list)
        {
            diagnostics.Add(Diag("ignore-actions must be a YAML list", DomLine, 5, 14, filePath));
            return null;
        }

        if (list.Count == 0)
        {
            return null;
        }

        var result = new List<IgnoreActionRule>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];

            if (AsMap(item) is { } map)
            {
                string? owner = null;
                IReadOnlyList<string>? refs = null;
                var ownerKeyPresent = false;
                var ownerValueValid = true;
                var refsKeyPresent = false;
                var refsValueIsList = false;
                var refsEntriesValid = true;
                foreach (var (ik, iv) in map)
                {
                    if (ik == "owner")
                    {
                        ownerKeyPresent = true;
                        if (!IsScalarValue(iv))
                        {
                            ownerValueValid = false;
                            diagnostics.Add(Diag("ignore-actions owner must be a scalar value", DomLine, 5, 14, filePath));
                        }
                        else
                        {
                            owner = Unquote(ScalarToString(iv));
                        }
                    }
                    else if (ik == "refs")
                    {
                        refsKeyPresent = true;
                        refsValueIsList = AsList(iv) is not null;
                        if (refsValueIsList)
                        {
                            refs = ParseScalarStringList(iv, "refs", diagnostics, filePath, out refsEntriesValid);
                        }
                        else
                        {
                            refs = ParseStringList(iv, "refs", diagnostics, filePath);
                        }
                    }
                    else
                    {
                        diagnostics.Add(Diag($"unknown ignore-actions key '{ik}'", DomLine, 5, ik.Length, filePath));
                    }
                }

                if (!ownerKeyPresent)
                {
                    diagnostics.Add(Diag("ignore-actions requires 'owner' key", DomLine, 5, 14, filePath));
                    continue;
                }

                if (!ownerValueValid)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(owner))
                {
                    diagnostics.Add(Diag("ignore-actions owner must not be empty", DomLine, 5, 14, filePath));
                    continue;
                }

                if (refsKeyPresent && !refsValueIsList)
                {
                    continue;
                }

                if (refsKeyPresent && !refsEntriesValid)
                {
                    continue;
                }

                if (refsKeyPresent && refsValueIsList && refs is { Count: 0 })
                {
                    diagnostics.Add(Diag("ignore-actions requires non-empty 'refs' list when 'refs' is present", DomLine, 5, 14, filePath));
                    continue;
                }

                result.Add(new IgnoreActionRule(owner, refs));
                continue;
            }

            diagnostics.Add(Diag("ignore-actions item must be a mapping with owner and optional refs", DomLine, 5, 14, filePath));
        }

        return result.Count > 0 ? result : null;
    }

    private static IReadOnlyDictionary<string, string>? ParseFixMapping(
        object? value,
        List<Diagnostic> diagnostics,
        string filePath)
    {
        if (AsMap(value) is not { } map)
        {
            diagnostics.Add(Diag("fix-mapping must be a YAML mapping", DomLine, 5, 11, filePath));
            return null;
        }

        if (map.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, string>(map.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in map)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                diagnostics.Add(Diag("fix-mapping key must not be empty or whitespace", DomLine, 5, 11, filePath));
                continue;
            }

            if (val is null)
            {
                diagnostics.Add(Diag($"fix-mapping value for key '{key}' must not be null", DomLine, 5, 11, filePath));
                continue;
            }

            var valStr = ScalarToString(val);
            if (string.IsNullOrWhiteSpace(valStr))
            {
                diagnostics.Add(Diag($"fix-mapping value for key '{key}' must not be empty or whitespace", DomLine, 5, 11, filePath));
                continue;
            }

            result[key.Trim()] = valStr.Trim();
        }

        return result.Count > 0 ? result : null;
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
        var maxConcurrency = LintConfigResourceLimits.DefaultNetworkMaxConcurrency;
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

    private static OutputConfig ParseOutput(Dictionary<string, object?> map, List<Diagnostic> diagnostics, string filePath)
    {
        var sortOrder = DiagnosticSortOrder.Location;

        foreach (var (key, value) in map)
        {
            switch (key)
            {
                case "sort-order":
                    var sv = ScalarToString(value);
                    if (string.Equals(sv, "location", StringComparison.OrdinalIgnoreCase))
                    {
                        sortOrder = DiagnosticSortOrder.Location;
                    }
                    else if (string.Equals(sv, "rule", StringComparison.OrdinalIgnoreCase))
                    {
                        sortOrder = DiagnosticSortOrder.Rule;
                    }
                    else
                    {
                        diagnostics.Add(Diag("output.sort-order must be 'location' or 'rule'", DomLine, 3, 10, filePath));
                    }

                    break;
                default:
                    diagnostics.Add(Diag($"unknown output key '{key}'", DomLine, 3, key.Length, filePath));
                    break;
            }
        }

        return new OutputConfig { SortOrder = sortOrder };
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

        string? file = null;
        IReadOnlyList<string>? rulesList = null;
        bool rulesKeyPresent = false;
        IReadOnlyList<string> jobsList = [];

        foreach (var (key, value) in item)
        {
            if (key == "file")
            {
                file = Unquote(ScalarToString(value));
            }
            else if (key == "rules")
            {
                rulesKeyPresent = true;
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

        if (string.IsNullOrWhiteSpace(file))
        {
            diagnostics.Add(Diag("exclusion file is required", DomLine, 3, 1, filePath));
            return;
        }

        // rules omitted → null (all rules); rules: [] → empty list (no-op, handled by normalizer)
        IReadOnlyList<string>? finalRules = rulesKeyPresent ? (rulesList ?? []) : null;
        exclusions.Add(new LintExclusion(file, finalRules, jobsList.Count > 0 ? jobsList : null));
    }

    private static Dictionary<string, object?>? AsMap(object? o)
    {
        return o as Dictionary<string, object?>;
    }

    private static List<object?>? AsList(object? o)
    {
        return o as List<object?>;
    }

    private static bool IsScalarValue(object? o) => o is null
        or string
        or bool
        or int
        or long
        or uint
        or ulong
        or double
        or float
        or decimal;

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

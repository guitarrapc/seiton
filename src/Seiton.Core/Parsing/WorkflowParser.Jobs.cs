using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private enum JobNodeMappingKey : byte
    {
        RunsOn = 0,
        Name = 1,
        Needs = 2,
        Env = 3,
        Steps = 4,
        Uses = 5,
        If = 6,
        Permissions = 7,
        Environment = 8,
        Concurrency = 9,
        Outputs = 10,
        Defaults = 11,
        TimeoutMinutes = 12,
        ContinueOnError = 13,
        Strategy = 14,
        Container = 15,
        Services = 16,
        With = 17,
        Secrets = 18,
    }

    /// <summary>UTF-8 rows for <see cref="JobNodeMappingKey"/>; ordinal must match enum value and duplicate-tracking bit index.</summary>
    private readonly struct JobNodeKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 19;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "runs-on"u8,
            1 => "name"u8,
            2 => "needs"u8,
            3 => "env"u8,
            4 => "steps"u8,
            5 => "uses"u8,
            6 => "if"u8,
            7 => "permissions"u8,
            8 => "environment"u8,
            9 => "concurrency"u8,
            10 => "outputs"u8,
            11 => "defaults"u8,
            12 => "timeout-minutes"u8,
            13 => "continue-on-error"u8,
            14 => "strategy"u8,
            15 => "container"u8,
            16 => "services"u8,
            17 => "with"u8,
            18 => "secrets"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private static string JobNodeDuplicateKeyName(JobNodeMappingKey key) => key switch
    {
        JobNodeMappingKey.RunsOn => "runs-on",
        JobNodeMappingKey.Name => "name",
        JobNodeMappingKey.Needs => "needs",
        JobNodeMappingKey.Env => "env",
        JobNodeMappingKey.Steps => "steps",
        JobNodeMappingKey.Uses => "uses",
        JobNodeMappingKey.If => "if",
        JobNodeMappingKey.Permissions => "permissions",
        JobNodeMappingKey.Environment => "environment",
        JobNodeMappingKey.Concurrency => "concurrency",
        JobNodeMappingKey.Outputs => "outputs",
        JobNodeMappingKey.Defaults => "defaults",
        JobNodeMappingKey.TimeoutMinutes => "timeout-minutes",
        JobNodeMappingKey.ContinueOnError => "continue-on-error",
        JobNodeMappingKey.Strategy => "strategy",
        JobNodeMappingKey.Container => "container",
        JobNodeMappingKey.Services => "services",
        JobNodeMappingKey.With => "with",
        JobNodeMappingKey.Secrets => "secrets",
        _ => "job key",
    };

    private enum RunsOnMappingKey : byte
    {
        Labels = 0,
        Group = 1,
    }

    /// <summary>Mapping form of <c>runs-on</c>; ordinal = duplicate-tracking bit index.</summary>
    private readonly struct RunsOnKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "labels"u8,
            1 => "group"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private static string RunsOnDuplicateKeyName(RunsOnMappingKey key) => key switch
    {
        RunsOnMappingKey.Labels => "labels",
        RunsOnMappingKey.Group => "group",
        _ => "runs-on key",
    };

    private static Job ParseJobNode<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, TextPosition jobIdMark, StringNodeId jobIdNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNodeId nameNode = default;
        StringNodeId[]? needsNode = null;
        Runner? runsOnNode = null;
        Permissions? permissionsNode = null;
        Seiton.Core.Parsing.Ast.Environment? environmentNode = null;
        Concurrency? concurrencyNode = null;
        SliceMap<StringNodeId>? outputsNode = null;
        Env? envNode = null;
        Defaults? defaultsNode = null;
        StringNodeId ifNode = default;
        Step[]? stepsNode = null;
        FloatNodeId timeoutMinutesNode = default;
        Strategy? strategyNode = null;
        BoolNodeId continueOnErrorNode = default;
        Container? containerNode = null;
        Services? servicesNode = null;
        WorkflowCall? workflowCallNode = null;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new Job { Id = jobIdNode, Range = arena.GetStringRange(jobIdNode) };
        }

        string? stepsOnlyKeyInReusable = null;
        TextPosition stepsOnlyKeyInReusableMark = default;
        ulong seen = 0;

        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, $"job '{DecodeUtf8(source, jobId)}'"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<JobNodeKeyTable>(keyUtf8, out var jobKeyOrd))
            {
                var keyLen = keyUtf8.Length;
                var jobKey = (JobNodeMappingKey)jobKeyOrd;
                reader.Read();
                if (!TrySetBit(ref seen, jobKeyOrd))
                {
                    AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: {JobNodeDuplicateKeyName(jobKey)}", keyMark);
                    if (!reader.End) reader.SkipCurrentNode();
                    continue;
                }

                switch (jobKey)
                {
                    case JobNodeMappingKey.RunsOn:
                        if (stepsOnlyKeyInReusable is null)
                        {
                            stepsOnlyKeyInReusable = "runs-on";
                            stepsOnlyKeyInReusableMark = keyMark;
                        }

                        if (!reader.End)
                        {
                            runsOnNode = ParseRunsOnNode(ref reader, arena, diagnostics, source, jobId);
                        }

                        break;

                    case JobNodeMappingKey.Name:
                        if (!reader.End)
                        {
                            nameNode = ParseStringAndValidateExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobName, out var nameErr, out var nameMark, false);
                            if (nameErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' name must be scalar", nameMark);
                        }

                        break;

                    case JobNodeMappingKey.Needs:
                        if (!reader.End)
                        {
                            needsNode = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var needsErr, out var needsMark);
                            if (needsErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' needs must be scalar or sequence of scalar", needsMark);
                        }

                        break;

                    case JobNodeMappingKey.Env:
                        if (stepsOnlyKeyInReusable is null)
                        {
                            stepsOnlyKeyInReusable = "env";
                            stepsOnlyKeyInReusableMark = keyMark;
                        }

                        if (!reader.End)
                        {
                            envNode = ParseEnvNode(ref reader, arena, diagnostics, source, $"job '{DecodeUtf8(source, jobId)}' env must be mapping", ExpressionValidationContext.JobEnv);
                        }

                        break;

                    case JobNodeMappingKey.Steps:
                        if (stepsOnlyKeyInReusable is null)
                        {
                            stepsOnlyKeyInReusable = "steps";
                            stepsOnlyKeyInReusableMark = keyMark;
                        }

                        if (!reader.End)
                        {
                            if (reader.CurrentKind != YamlEventKind.SequenceStart)
                            {
                                AddError(diagnostics, $"job '{jobId}' steps must be sequence", reader.CurrentStart);
                                reader.SkipCurrentNode();
                            }
                            else
                            {
                                stepsNode = ParseSteps(ref reader, arena, diagnostics, source, jobId);
                            }
                        }

                        break;

                    case JobNodeMappingKey.Uses:
                        if (!reader.End)
                        {
                            var usesNode = ParseString(ref reader, arena, out var usesErr, out var usesMark);
                            if (usesErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' uses must be scalar", usesMark);
                            workflowCallNode = new WorkflowCall
                            {
                                Uses = usesNode.HasValue ? usesNode : arena.AddString(default, false, default),
                                UsesKeyRange = BuildScalarLocation(keyMark, keyLen),
                                Inputs = workflowCallNode?.Inputs,
                                Secrets = workflowCallNode?.Secrets,
                                InheritSecrets = workflowCallNode?.InheritSecrets ?? false,
                            };
                        }

                        break;

                    case JobNodeMappingKey.If:
                        if (!reader.End)
                        {
                            ifNode = ParseExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobIf, out var ifErr, out var ifMark);
                            if (ifErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' if must be scalar", ifMark);
                        }

                        break;

                    case JobNodeMappingKey.Permissions:
                        if (!reader.End)
                        {
                            permissionsNode = ParsePermissionsNode(ref reader, arena, diagnostics, source, $"job '{DecodeUtf8(source, jobId)}' permissions must be scalar or mapping");
                        }

                        break;

                    case JobNodeMappingKey.Environment:
                        if (stepsOnlyKeyInReusable is null)
                        {
                            stepsOnlyKeyInReusable = "environment";
                            stepsOnlyKeyInReusableMark = keyMark;
                        }

                        if (!reader.End)
                        {
                            environmentNode = ParseEnvironmentNode(ref reader, arena, diagnostics, source, jobId);
                        }

                        break;

                    case JobNodeMappingKey.Concurrency:
                        if (!reader.End)
                        {
                            concurrencyNode = ParseConcurrencyNode(ref reader, arena, diagnostics, $"job '{DecodeUtf8(source, jobId)}' concurrency must be scalar or mapping", ExpressionValidationContext.JobConcurrency);
                        }

                        break;

                    case JobNodeMappingKey.Outputs:
                        if (stepsOnlyKeyInReusable is null)
                        {
                            stepsOnlyKeyInReusable = "outputs";
                            stepsOnlyKeyInReusableMark = keyMark;
                        }

                        if (!reader.End)
                        {
                            outputsNode = ParseOutputsNode(ref reader, arena, diagnostics, source, jobId);
                        }

                        break;

                    case JobNodeMappingKey.Defaults:
                        if (stepsOnlyKeyInReusable is null)
                        {
                            stepsOnlyKeyInReusable = "defaults";
                            stepsOnlyKeyInReusableMark = keyMark;
                        }

                        if (!reader.End)
                        {
                            defaultsNode = ParseDefaultsNode(ref reader, arena, diagnostics, $"job '{DecodeUtf8(source, jobId)}' defaults must be mapping", ExpressionValidationContext.JobDefaultsRun);
                        }

                        break;

                    case JobNodeMappingKey.TimeoutMinutes:
                        if (stepsOnlyKeyInReusable is null)
                        {
                            stepsOnlyKeyInReusable = "timeout-minutes";
                            stepsOnlyKeyInReusableMark = keyMark;
                        }

                        if (!reader.End)
                        {
                            timeoutMinutesNode = ParseFloatOrExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobTimeoutMinutes, out var tmErr, out var tmMark);
                            if (tmErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' timeout-minutes must be number or expression", tmMark);
                            if (timeoutMinutesNode.HasValue && !arena.GetFloatExpression(timeoutMinutesNode).HasValue && arena.GetFloatValue(timeoutMinutesNode) <= 0)
                            {
                                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' timeout-minutes must be greater than 0", keyMark);
                            }
                        }

                        break;

                    case JobNodeMappingKey.ContinueOnError:
                        if (stepsOnlyKeyInReusable is null)
                        {
                            stepsOnlyKeyInReusable = "continue-on-error";
                            stepsOnlyKeyInReusableMark = keyMark;
                        }

                        if (!reader.End)
                        {
                            continueOnErrorNode = ParseBoolOrExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobContinueOnError, out var coeErr, out var coeMark);
                            if (coeErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' continue-on-error must be bool or expression", coeMark);
                        }

                        break;

                    case JobNodeMappingKey.Strategy:
                        if (!reader.End)
                        {
                            if (reader.CurrentKind != YamlEventKind.MappingStart)
                            {
                                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy must be mapping", reader.CurrentStart);
                                reader.SkipCurrentNode();
                            }
                            else
                            {
                                strategyNode = ParseStrategy(ref reader, arena, diagnostics, source, jobId);
                            }
                        }

                        break;

                    case JobNodeMappingKey.Container:
                        if (stepsOnlyKeyInReusable is null)
                        {
                            stepsOnlyKeyInReusable = "container";
                            stepsOnlyKeyInReusableMark = keyMark;
                        }

                        if (!reader.End)
                        {
                            containerNode = ParseContainerLike(ref reader, arena, diagnostics, source, jobId, default, isService: false, requireImage: true);
                        }

                        break;

                    case JobNodeMappingKey.Services:
                        if (stepsOnlyKeyInReusable is null)
                        {
                            stepsOnlyKeyInReusable = "services";
                            stepsOnlyKeyInReusableMark = keyMark;
                        }

                        if (!reader.End)
                        {
                            servicesNode = ParseServices(ref reader, arena, diagnostics, source, jobId);
                        }

                        break;

                    case JobNodeMappingKey.With:
                        if (!reader.End)
                        {
                            var inputs = ParseWorkflowCallInputsNode(ref reader, arena, diagnostics, source, jobId);
                            if (workflowCallNode is not null)
                            {
                                workflowCallNode = new WorkflowCall
                                {
                                    Uses = workflowCallNode.Uses,
                                    UsesKeyRange = workflowCallNode.UsesKeyRange,
                                    Inputs = inputs,
                                    Secrets = workflowCallNode.Secrets,
                                    InheritSecrets = workflowCallNode.InheritSecrets,
                                };
                            }
                            else
                            {
                                workflowCallNode = new WorkflowCall
                                {
                                    Uses = arena.AddString(default, false, default),
                                    UsesKeyRange = null,
                                    Inputs = inputs,
                                    Secrets = null,
                                    InheritSecrets = false,
                                };
                            }
                        }

                        break;

                    case JobNodeMappingKey.Secrets:
                        if (!reader.End)
                        {
                            var secrets = ParseWorkflowCallSecretsNode(ref reader, arena, diagnostics, source, jobId, out var inheritSecrets);
                            if (workflowCallNode is not null)
                            {
                                workflowCallNode = new WorkflowCall
                                {
                                    Uses = workflowCallNode.Uses,
                                    UsesKeyRange = workflowCallNode.UsesKeyRange,
                                    Inputs = workflowCallNode.Inputs,
                                    Secrets = secrets,
                                    InheritSecrets = inheritSecrets,
                                };
                            }
                            else
                            {
                                workflowCallNode = new WorkflowCall
                                {
                                    Uses = arena.AddString(default, false, default),
                                    UsesKeyRange = null,
                                    Inputs = null,
                                    Secrets = secrets,
                                    InheritSecrets = inheritSecrets,
                                };
                            }
                        }

                        break;
                }

                continue;
            }

            var isKnownKey = IsKnownJobKey(keyUtf8);
            var hasStepsOnlyName = TryGetStepsOnlyReusableJobKeyName(keyUtf8, out var stepsOnlyKeyName);
            var unknownJobKey = !isKnownKey ? Encoding.UTF8.GetString(keyUtf8) : null;

            reader.Read();

            if (stepsOnlyKeyInReusable is null && hasStepsOnlyName)
            {
                stepsOnlyKeyInReusable = stepsOnlyKeyName;
                stepsOnlyKeyInReusableMark = keyMark;
            }

            if (isKnownKey)
            {
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            AddError(diagnostics, $"unexpected job key '{unknownJobKey}' in job '{DecodeUtf8(source, jobId)}'", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        var decodedJobId = DecodeUtf8(source, jobId);
        var hasUses = workflowCallNode is not null && arena.GetStringValue(workflowCallNode.Uses).Length > 0;
        var hasSteps = stepsNode is not null;
        var hasRunsOn = runsOnNode is not null;

        // spec §3.10.1: reusable workflow calls (`uses`) cannot also define `steps`
        if (hasUses && hasSteps)
        {
            AddError(diagnostics, $"job '{decodedJobId}' cannot have both uses and steps", jobIdMark);
        }

        // spec §3.10.1: reusable workflow calls (`uses`) cannot also define `runs-on`
        if (hasUses && hasRunsOn)
        {
            AddError(diagnostics, $"job '{decodedJobId}' cannot have both uses and runs-on", jobIdMark);
        }

        // spec §3.10 post-validation: normal jobs require `runs-on`
        if (!hasUses && !hasRunsOn)
        {
            AddError(diagnostics, $"job '{decodedJobId}' requires runs-on (or uses)", jobIdMark);
        }

        // spec §3.10 post-validation: normal jobs require `steps`
        if (!hasUses && !hasSteps)
        {
            AddError(diagnostics, $"job '{decodedJobId}' requires steps (or uses)", jobIdMark);
        }

        if (!hasUses && workflowCallNode is not null)
        {
            // spec §3.10 post-validation: `with` is only valid for reusable workflow calls via `uses`
            if (workflowCallNode.Inputs.HasValue && workflowCallNode.Inputs.Value.Count > 0)
            {
                AddError(diagnostics, $"job '{decodedJobId}' key 'with' requires uses", jobIdMark);
            }

            // spec §3.10 post-validation: `secrets` is only valid for reusable workflow calls via `uses`
            if ((workflowCallNode.Secrets.HasValue && workflowCallNode.Secrets.Value.Count > 0) || workflowCallNode.InheritSecrets)
            {
                AddError(diagnostics, $"job '{decodedJobId}' key 'secrets' requires uses", jobIdMark);
            }
        }

        // spec §3.10.1: steps-only keys are invalid when the job calls a reusable workflow via `uses`
        if (hasUses && stepsOnlyKeyInReusable is not null)
        {
            AddError(
                diagnostics,
                $"when job '{decodedJobId}' calls reusable workflow with uses, key '{stepsOnlyKeyInReusable}' is not allowed",
                stepsOnlyKeyInReusableMark);
        }

        return new Job
        {
            Id = jobIdNode,
            Name = nameNode,
            Needs = needsNode,
            RunsOn = runsOnNode,
            Permissions = permissionsNode,
            Environment = environmentNode,
            Concurrency = concurrencyNode,
            Outputs = outputsNode,
            Env = envNode,
            Defaults = defaultsNode,
            If = ifNode,
            Steps = stepsNode,
            TimeoutMinutes = timeoutMinutesNode,
            Strategy = strategyNode,
            ContinueOnError = continueOnErrorNode,
            Container = containerNode,
            Services = servicesNode,
            WorkflowCall = workflowCallNode,
            Range = arena.GetStringRange(jobIdNode),
        };
    }


    private static bool IsKnownJobKey(ReadOnlySpan<byte> keyUtf8)
    {
        return keyUtf8.SequenceEqual("name"u8)
            || keyUtf8.SequenceEqual("needs"u8)
            || keyUtf8.SequenceEqual("if"u8)
            || keyUtf8.SequenceEqual("permissions"u8)
            || keyUtf8.SequenceEqual("env"u8)
            || keyUtf8.SequenceEqual("defaults"u8)
            || keyUtf8.SequenceEqual("timeout-minutes"u8)
            || keyUtf8.SequenceEqual("strategy"u8)
            || keyUtf8.SequenceEqual("concurrency"u8)
            || keyUtf8.SequenceEqual("container"u8)
            || keyUtf8.SequenceEqual("services"u8)
            || keyUtf8.SequenceEqual("outputs"u8)
            || keyUtf8.SequenceEqual("secrets"u8)
            || keyUtf8.SequenceEqual("with"u8);
    }

    private static bool TryGetStepsOnlyReusableJobKeyName(ReadOnlySpan<byte> keyUtf8, out string keyName)
    {
        if (keyUtf8.SequenceEqual("runs-on"u8)) { keyName = "runs-on"; return true; }
        if (keyUtf8.SequenceEqual("environment"u8)) { keyName = "environment"; return true; }
        if (keyUtf8.SequenceEqual("outputs"u8)) { keyName = "outputs"; return true; }
        if (keyUtf8.SequenceEqual("env"u8)) { keyName = "env"; return true; }
        if (keyUtf8.SequenceEqual("defaults"u8)) { keyName = "defaults"; return true; }
        if (keyUtf8.SequenceEqual("steps"u8)) { keyName = "steps"; return true; }
        if (keyUtf8.SequenceEqual("timeout-minutes"u8)) { keyName = "timeout-minutes"; return true; }
        if (keyUtf8.SequenceEqual("continue-on-error"u8)) { keyName = "continue-on-error"; return true; }
        if (keyUtf8.SequenceEqual("container"u8)) { keyName = "container"; return true; }

        keyName = string.Empty;
        return false;
    }

    private static bool IsKnownStepKey(ReadOnlySpan<byte> keyUtf8)
    {
        return keyUtf8.SequenceEqual("name"u8)
            || keyUtf8.SequenceEqual("id"u8)
            || keyUtf8.SequenceEqual("if"u8)
            || keyUtf8.SequenceEqual("with"u8)
            || keyUtf8.SequenceEqual("env"u8)
            || keyUtf8.SequenceEqual("shell"u8)
            || keyUtf8.SequenceEqual("working-directory"u8)
            || keyUtf8.SequenceEqual("timeout-minutes"u8)
            || keyUtf8.SequenceEqual("continue-on-error"u8);
    }


    private static Runner? ParseRunsOnNode<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var section = $"job '{DecodeUtf8(source, jobId)}' runs-on";

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            StringNodeId[]? labels = null;
            StringNodeId labelsExpr = default;
            StringNodeId group = default;
            ulong seen = 0;

            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"{section} key must be scalar", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var keyMark = reader.CurrentStart;
                var keyUtf8 = reader.GetScalarUtf8();
                if (IsMergeKey(keyUtf8, keyMark, diagnostics, "runs-on"))
                {
                    reader.Read();
                    if (!reader.End) reader.SkipCurrentNode();
                    continue;
                }

                if (Utf8MappingDispatch.TryMatchFirstOrdered<RunsOnKeyTable>(keyUtf8, out var runsOnKeyOrd))
                {
                    var roKey = (RunsOnMappingKey)runsOnKeyOrd;
                    reader.Read();
                    if (!TrySetBit(ref seen, runsOnKeyOrd))
                    {
                        AddError(diagnostics, $"runs-on contains duplicate key: {RunsOnDuplicateKeyName(roKey)}", keyMark);
                        if (!reader.End) reader.SkipCurrentNode();
                        continue;
                    }

                    switch (roKey)
                    {
                        case RunsOnMappingKey.Labels:
                            if (!reader.End)
                            {
                                if (reader.CurrentKind == YamlEventKind.Scalar)
                                {
                                    var valueUtf8 = reader.GetScalarUtf8();
                                    if (ContainsExpression(valueUtf8))
                                    {
                                        labelsExpr = ParseStringAndValidateExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobRunsOn, out var lblExprErr, out var lblExprMark, parseWholeValueIfNoEmbedded: false);
                                        if (lblExprErr) AddError(diagnostics, $"{section}.labels must be scalar, sequence, or expression", lblExprMark);
                                    }
                                    else
                                    {
                                        labels = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var lblErr1, out var lblMark1);
                                        if (lblErr1) AddError(diagnostics, $"{section}.labels must be scalar, sequence, or expression", lblMark1);
                                    }
                                }
                                else
                                {
                                    labels = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var lblErr2, out var lblMark2);
                                    if (lblErr2) AddError(diagnostics, $"{section}.labels must be scalar, sequence, or expression", lblMark2);
                                }
                            }

                            break;

                        case RunsOnMappingKey.Group:
                            group = ParseStringAndValidateExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobRunsOn, out var grpErr, out var grpMark, parseWholeValueIfNoEmbedded: false);
                            if (grpErr) AddError(diagnostics, $"{section}.group must be scalar", grpMark);
                            break;
                    }

                    continue;
                }

                var unknownRunsOnKey = Encoding.UTF8.GetString(keyUtf8);
                reader.Read();
                AddError(diagnostics, $"unexpected runs-on key: {unknownRunsOnKey}", keyMark);
                if (!reader.End) reader.SkipCurrentNode();
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            if (labels is null && !labelsExpr.HasValue)
            {
                AddError(diagnostics, $"{section} requires labels", new TextPosition(0, 1, 1));
            }

            return new Runner
            {
                Labels = labels,
                LabelsExpr = labelsExpr,
                Group = group,
                Range = labelsExpr.HasValue ? arena.GetStringRange(labelsExpr) : group.HasValue ? arena.GetStringRange(group) : (labels is { Length: > 0 } ? arena.GetStringRange(labels[0]) : default),
            };
        }

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var scalarUtf8 = reader.GetScalarUtf8();
            if (ContainsExpression(scalarUtf8))
            {
                var expr = ParseStringAndValidateExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobRunsOn, out var roExprErr, out var roExprMark, parseWholeValueIfNoEmbedded: false);
                if (roExprErr) AddError(diagnostics, $"{section} must be scalar, sequence, or mapping", roExprMark);
                return new Runner
                {
                    LabelsExpr = expr,
                    Range = expr.HasValue ? arena.GetStringRange(expr) : default,
                };
            }
        }

        var labelsFallback = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var lblFbErr, out var lblFbMark);
        if (lblFbErr) AddError(diagnostics, $"{section} must be scalar, sequence, or mapping", lblFbMark);
        return new Runner
        {
            Labels = labelsFallback,
            Range = labelsFallback.Length > 0 ? arena.GetStringRange(labelsFallback[0]) : default,
        };
    }

    private static Seiton.Core.Parsing.Ast.Environment? ParseEnvironmentNode<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var name = ParseStringAndValidateExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobEnvironment, out var envNameErr, out var envNameMark, false);
            if (envNameErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment must be scalar or mapping", envNameMark);
            return !name.HasValue
                ? null
                : new Seiton.Core.Parsing.Ast.Environment
                {
                    Name = name,
                    Range = arena.GetStringRange(name),
                };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment must be scalar or mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        StringNodeId nameNode = default;
        StringNodeId urlNode = default;
        BoolNodeId deploymentNode = default;
        ulong seen = 0;

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "environment"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("name"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "environment contains duplicate key: name", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                nameNode = ParseString(ref reader, arena, out var envNErr, out var envNMark);
                if (envNErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment.name must be scalar", envNMark);
                continue;
            }

            if (keyUtf8.SequenceEqual("url"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "environment contains duplicate key: url", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                urlNode = ParseStringAndValidateExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobEnvironmentUrl, out var urlErr, out var urlMark, parseWholeValueIfNoEmbedded: false);
                if (urlErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment.url must be scalar", urlMark);
                continue;
            }

            if (keyUtf8.SequenceEqual("deployment"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, "environment contains duplicate key: deployment", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                deploymentNode = ParseBoolOrExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobEnvironment, out var depErr, out var depMark);
                if (depErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment.deployment must be bool or expression", depMark);
                continue;
            }

            var unknownEnvKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected environment key '{unknownEnvKey}' in job '{DecodeUtf8(source, jobId)}'", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        // spec §3.14 / §12: environment mapping form requires `name`
        if (!nameNode.HasValue)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment.name is required", jobId.Length > 0 ? new TextPosition(0, 1, 1) : new TextPosition(0, 1, 1));
            return default;
        }

        return new Seiton.Core.Parsing.Ast.Environment
        {
            Name = nameNode,
            Url = urlNode,
            Deployment = deploymentNode,
            Range = arena.GetStringRange(nameNode),
        };
    }

    private static SliceMap<StringNodeId>? ParseOutputsNode<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' outputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var outputs = new PooledBuffer<SliceMap<StringNodeId>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' outputs key must be scalar", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    continue;
                }

                var keySlice = reader.GetScalarSlice();
                var keyUtf8 = reader.GetScalarUtf8();
                var keyMark = reader.CurrentStart;
                if (!TryRegisterDynamicKey(
                    source,
                    keyUtf8,
                    keySlice.Offset,
                    keySlice.Length,
                    keyMark,
                    diagnostics,
                    keyStore,
                    ref keyCount,
                    caseSensitive: false,
                    "outputs"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var keyNode = arena.AddString(keySlice, reader.IsScalarQuoted(), BuildScalarLocation(reader.CurrentStart, keyUtf8.Length));
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                var value = ParseStringAndValidateExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobOutputs, out var outErr, out var outMark, parseWholeValueIfNoEmbedded: false);
                if (outErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' outputs.{Encoding.UTF8.GetString(keyUtf8)} must be scalar", outMark);
                outputs.Add(new SliceMap<StringNodeId>.Entry(keySlice, value.HasValue ? value : keyNode));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return new SliceMap<StringNodeId>(outputs.ToArray(), caseSensitive: false);
        }
        finally { outputs.Dispose(); }
    }

    private static SliceMap<WorkflowCallInput>? ParseWorkflowCallInputsNode<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' with must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var map = new PooledBuffer<SliceMap<WorkflowCallInput>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' with must be mapping", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    continue;
                }

                var nameMark = reader.CurrentStart;
                var nameSlice = reader.GetScalarSlice();
                var nameUtf8 = reader.GetScalarUtf8();
                if (!TryRegisterDynamicKey(source, nameUtf8, nameSlice.Offset, nameSlice.Length, nameMark, diagnostics, keyStore, ref keyCount, caseSensitive: false, "with"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var nameNode = arena.AddString(nameSlice, reader.IsScalarQuoted(), BuildScalarLocation(nameMark, nameUtf8.Length));
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                StringNodeId valueNode;
                try
                {
                    valueNode = ParseStringAndValidateExpression(ref reader, arena, diagnostics, ExpressionValidationContext.JobWith, out var withErr, out var withMark, parseWholeValueIfNoEmbedded: false);
                    if (withErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' with.{Encoding.UTF8.GetString(nameUtf8)} must be scalar", withMark);
                }
                catch
                {
                    AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' with.{Encoding.UTF8.GetString(nameUtf8)} must be scalar", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    valueNode = default;
                }

                if (valueNode.HasValue)
                {
                    map.Add(new SliceMap<WorkflowCallInput>.Entry(nameSlice, new WorkflowCallInput { Name = nameNode, Value = valueNode }));
                }
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return new SliceMap<WorkflowCallInput>(map.ToArray(), caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

    private static SliceMap<WorkflowCallSecret>? ParseWorkflowCallSecretsNode<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, out bool inheritSecrets)
        where TReader : IYamlStreamReader, allows ref struct
    {
        inheritSecrets = false;

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var valueUtf8 = reader.GetScalarUtf8();
            if (!valueUtf8.SequenceEqual("inherit"u8))
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' secrets scalar must be 'inherit'", reader.CurrentStart);
            }
            else
            {
                inheritSecrets = true;
            }
            reader.Read();
            return default;
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' secrets must be mapping or scalar 'inherit'", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var map = new PooledBuffer<SliceMap<WorkflowCallSecret>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' secrets must be mapping or scalar 'inherit'", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    continue;
                }

                var nameMark = reader.CurrentStart;
                var nameSlice = reader.GetScalarSlice();
                var nameUtf8 = reader.GetScalarUtf8();
                if (!TryRegisterDynamicKey(source, nameUtf8, nameSlice.Offset, nameSlice.Length, nameMark, diagnostics, keyStore, ref keyCount, caseSensitive: false, "secrets"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var nameNode = arena.AddString(nameSlice, reader.IsScalarQuoted(), BuildScalarLocation(nameMark, nameUtf8.Length));
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                var valueNode = ParseString(ref reader, arena, out var secErr, out var secMark, allowEmpty: false);
                if (secErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' secrets.{Encoding.UTF8.GetString(nameUtf8)} must be scalar", secMark);
                if (valueNode.HasValue)
                {
                    var valueUtf8 = arena.GetStringValue(valueNode);
                    var valueLocation = BuildLocationFromSourceSlice(source, arena.GetStringSlice(valueNode).Offset, valueUtf8.Length);
                    ValidateExpressionText(valueUtf8, valueLocation, ExpressionValidationContext.JobSecrets, diagnostics, parseWholeValueIfNoEmbedded: false);
                }

                if (valueNode.HasValue)
                {
                    map.Add(new SliceMap<WorkflowCallSecret>.Entry(nameSlice, new WorkflowCallSecret { Name = nameNode, Value = valueNode }));
                }
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return new SliceMap<WorkflowCallSecret>(map.ToArray(), caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

    private static void ParseJobSecrets<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var valueUtf8 = reader.GetScalarUtf8();
            if (!valueUtf8.SequenceEqual("inherit"u8))
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' secrets scalar must be 'inherit'", reader.CurrentStart);
            }
            reader.Read();
            return;
        }

        ParseStringMapping(ref reader, arena, diagnostics, source, $"job '{DecodeUtf8(source, jobId)}' secrets must be mapping or scalar 'inherit'");
    }

}

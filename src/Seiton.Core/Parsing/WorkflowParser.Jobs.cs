using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static readonly string RunsOnEmptyLabelMessage =
        $"\"runs-on\" label should not be empty. available labels are: hosted runners: {RunnerLabels.HostedLabelList}. larger runners: {RunnerLabels.LargerLabelList}. self-hosted presets: {RunnerLabels.SelfHostedPresetLabelList}. if it is a custom label for self-hosted runner, set list of labels in config file";

    private static readonly string RunsOnSectionEmptyMessage =
        $"\"runs-on\" section should not be empty. available labels are: hosted runners: {RunnerLabels.HostedLabelList}. larger runners: {RunnerLabels.LargerLabelList}. self-hosted presets: {RunnerLabels.SelfHostedPresetLabelList}. if it is a custom label for self-hosted runner, set list of labels in config file";

    private static readonly string LabelsSectionEmptyMessage =
        $"\"labels\" section should not be empty. available labels are: hosted runners: {RunnerLabels.HostedLabelList}. larger runners: {RunnerLabels.LargerLabelList}. self-hosted presets: {RunnerLabels.SelfHostedPresetLabelList}. if it is a custom label for self-hosted runner, set list of labels in config file";

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

    private static JobId ParseJobNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, TextPosition jobIdMark, StringNodeId jobIdNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNodeId nameNode = default;
        StringIdRange needsNode = default;
        RunnerId runsOnNode = default;
        PermissionsId permissionsNode = default;
        EnvironmentId environmentNode = default;
        ConcurrencyId concurrencyNode = default;
        NodeRange outputsNode = default;
        EnvId envNode = default;
        DefaultsId defaultsNode = default;
        StringNodeId ifNode = default;
        TextPosition ifKeyMark = default;
        StepIdRange stepsNode = default;
        FloatNodeId timeoutMinutesNode = default;
        StrategyId strategyNode = default;
        BoolNodeId continueOnErrorNode = default;
        ContainerId containerNode = default;
        ServicesId servicesNode = default;
        // The workflow-call pieces arrive across multiple job keys (uses/with/secrets),
        // so they accumulate in locals and become a single row at job construction.
        var hasWorkflowCall = false;
        StringNodeId wcUses = default;
        TextRange? wcUsesKeyRange = null;
        NodeRange wcInputs = default;
        TextRange? wcWithKeyRange = null;
        NodeRange wcSecrets = default;
        TextRange? wcSecretsKeyRange = null;
        var wcInheritSecrets = false;
        SnapshotId snapshotNode = default;
        TextPosition stepsKeyPos = default;
        TextPosition runsOnKeyPos = default;
        TextPosition withKeyPos = default;
        TextPosition secretsKeyPos = default;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}' must be object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return arena.AddJob(new JobData { Id = jobIdNode, Range = arena.GetStringRange(jobIdNode) });
        }

        ulong seen = 0;
        Span<long> jobKeyFirstMark = stackalloc long[ExpectedKeys.JobMappingKeyTable.KeyCount];

        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}' key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            // Inline merge-key check: passing an interpolated section name to IsMergeKey
            // would decode the job id and format the string for EVERY job key on clean
            // parses; build the message only when the merge key actually occurs.
            if (keyUtf8.SequenceEqual("<<"u8))
            {
                AddError(ref diagnostics, $"GitHub Actions does not support YAML merge key \"<<\". occurred in jobs.'{DecodeUtf8(source, jobId)}'", keyMark);
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<ExpectedKeys.JobMappingKeyTable>(keyUtf8, out var jobKeyOrd))
            {
                var keyLen = keyUtf8.Length;
                var jobKey = (ExpectedKeys.JobMappingKey)jobKeyOrd;
                reader.Read();
                if (!TrySetBit(ref seen, jobKeyOrd))
                {
                    var dupName = Encoding.UTF8.GetString(ExpectedKeys.JobMappingKeyTable.Utf8Key(jobKeyOrd));
                    var jobIdText = DecodeUtf8(source, jobId);
                    var prevMark = jobKeyFirstMark[jobKeyOrd];
                    var prevLine = (int)(prevMark >> 32);
                    var prevCol = (int)(prevMark & 0xFFFFFFFF);
                    AddError(ref diagnostics, $"key \"{dupName}\" is duplicated in \"{jobIdText}\" job. previously defined at line:{prevLine},col:{prevCol}", keyMark);
                    if (!reader.End) reader.SkipCurrentNode();
                    continue;
                }

                if (jobKeyOrd < jobKeyFirstMark.Length)
                {
                    jobKeyFirstMark[jobKeyOrd] = ((long)keyMark.Line << 32) | (uint)keyMark.Col;
                }

                switch (jobKey)
                {
                    case ExpectedKeys.JobMappingKey.RunsOn:
                        runsOnKeyPos = keyMark;
                        if (!reader.End)
                        {
                            runsOnNode = ParseRunsOnNode(ref reader, arena, ref diagnostics, source, jobId);
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Name:
                        if (!reader.End)
                        {
                            nameNode = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobName, out var nameErr, out var nameMark, false);
                            if (nameErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.name must be string", nameMark);
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Needs:
                        if (!reader.End)
                        {
                            var needsSeqMark = reader.CurrentStart;
                            needsNode = ParseStringOrStringSequence(ref reader, arena, ref diagnostics, out var needsErr, out var needsMark);
                            if (needsErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.needs must be string or array of strings", needsMark);
                            else if (needsNode is { Count: 0 }) AddError(ref diagnostics, "\"needs\" section should not be empty", needsSeqMark);
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Env:
                        if (!reader.End)
                        {
                            envNode = ParseEnvNode(ref reader, arena, ref diagnostics, source, $"jobs.'{DecodeUtf8(source, jobId)}'.env must be object", ExpressionValidationContext.JobEnv, $"jobs.'{DecodeUtf8(source, jobId)}'.env");
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Steps:
                        stepsKeyPos = keyMark;
                        if (!reader.End)
                        {
                            if (reader.CurrentKind != YamlEventKind.SequenceStart)
                            {
                                var nodeKind = reader.CurrentKind == YamlEventKind.Scalar ? "scalar" : "mapping";
                                var tagStr = reader.CurrentKind == YamlEventKind.Scalar
                                    ? reader.GetScalarTag() switch
                                    {
                                        ScalarTag.Null => " with \"!!null\" tag",
                                        ScalarTag.Bool => " with \"!!bool\" tag",
                                        ScalarTag.Int => " with \"!!int\" tag",
                                        ScalarTag.Float => " with \"!!float\" tag",
                                        _ => "",
                                    }
                                    : "";
                                AddError(ref diagnostics, $"\"steps\" section must be sequence node but got {nodeKind} node{tagStr}", reader.CurrentStart);
                                reader.SkipCurrentNode();
                            }
                            else
                            {
                                var stepPathPrefix = jobId.Length > 0
                                    ? $"jobs.'{DecodeUtf8(source, jobId)}'.steps"
                                    : "steps";
                                stepsNode = ParseSteps(ref reader, arena, ref diagnostics, source, stepPathPrefix, StepParseContext.WorkflowJobStep);
                            }
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Uses:
                        if (!reader.End)
                        {
                            var usesNode = ParseString(ref reader, arena, out var usesErr, out var usesMark);
                            if (usesErr)
                            {
                                var usesMsg = usesNode.HasValue
                                    ? $"jobs.'{DecodeUtf8(source, jobId)}'.uses must be string and should not be empty"
                                    : $"jobs.'{DecodeUtf8(source, jobId)}'.uses must be string";
                                AddError(ref diagnostics, usesMsg, usesMark);
                            }

                            hasWorkflowCall = true;
                            wcUses = usesNode.HasValue ? usesNode : arena.AddString(default, false, default);
                            wcUsesKeyRange = BuildScalarLocation(keyMark, keyLen);
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.If:
                        ifKeyMark = keyMark;
                        if (!reader.End)
                        {
                            ifNode = ParseExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobIf, out var ifErr, out var ifMark);
                            if (ifErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.if must be string", ifMark);
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Permissions:
                        if (!reader.End)
                        {
                            permissionsNode = ParsePermissionsNode(ref reader, arena, ref diagnostics, source, $"jobs.'{DecodeUtf8(source, jobId)}'.permissions must be string or object");
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Environment:
                        if (!reader.End)
                        {
                            environmentNode = ParseEnvironmentNode(ref reader, arena, ref diagnostics, source, jobId, keyMark);
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Concurrency:
                        if (!reader.End)
                        {
                            var jobIdForConcurrency = DecodeUtf8(source, jobId);
                            concurrencyNode = ParseConcurrencyNode(ref reader, arena, ref diagnostics, $"jobs.'{jobIdForConcurrency}'.concurrency must be string or object", ExpressionValidationContext.JobConcurrency, keyMark, sectionContext: $"jobs.'{jobIdForConcurrency}'");
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Outputs:
                        if (!reader.End)
                        {
                            outputsNode = ParseOutputsNode(ref reader, arena, ref diagnostics, source, jobId);
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Defaults:
                        if (!reader.End)
                        {
                            var jobIdForDefaults = DecodeUtf8(source, jobId);
                            defaultsNode = ParseDefaultsNode(ref reader, arena, ref diagnostics, $"jobs.'{jobIdForDefaults}'.defaults must be object", ExpressionValidationContext.JobDefaultsRun, sectionContext: $"jobs.'{jobIdForDefaults}'");
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.TimeoutMinutes:
                        if (!reader.End)
                        {
                            timeoutMinutesNode = ParseFloatOrExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobTimeoutMinutes, out var tmErr, out var tmMark);
                            if (tmErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.timeout-minutes must be number or expression", tmMark);
                            if (timeoutMinutesNode.HasValue && !arena.GetFloatExpression(timeoutMinutesNode).HasValue && arena.GetFloatValue(timeoutMinutesNode) <= 0)
                            {
                                AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.timeout-minutes must be greater than 0", keyMark);
                            }
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.ContinueOnError:
                        if (!reader.End)
                        {
                            continueOnErrorNode = ParseBoolOrExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobContinueOnError, out var coeErr, out var coeMark);
                            if (coeErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.continue-on-error must be bool or expression", coeMark);
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Strategy:
                        if (!reader.End)
                        {
                            if (reader.CurrentKind != YamlEventKind.MappingStart)
                            {
                                AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy must be object", reader.CurrentStart);
                                reader.SkipCurrentNode();
                            }
                            else
                            {
                                strategyNode = ParseStrategy(ref reader, arena, ref diagnostics, source, jobId);
                            }
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Container:
                        if (!reader.End)
                        {
                            containerNode = ParseContainerLike(ref reader, arena, ref diagnostics, source, jobId, default, isService: false, requireImage: true, keyMark);
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Services:
                        if (!reader.End)
                        {
                            servicesNode = ParseServices(ref reader, arena, ref diagnostics, source, jobId);
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.With:
                        withKeyPos = keyMark;
                        if (!reader.End)
                        {
                            var inputs = ParseWorkflowCallInputsNode(ref reader, arena, ref diagnostics, source, jobId);
                            if (!hasWorkflowCall)
                            {
                                hasWorkflowCall = true;
                                wcUses = arena.AddString(default, false, default);
                            }

                            wcInputs = inputs;
                            wcWithKeyRange = BuildScalarLocation(withKeyPos, 4);
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Secrets:
                        secretsKeyPos = keyMark;
                        if (!reader.End)
                        {
                            var secrets = ParseWorkflowCallSecretsNode(ref reader, arena, ref diagnostics, source, jobId, out var inheritSecrets);
                            if (!hasWorkflowCall)
                            {
                                hasWorkflowCall = true;
                                wcUses = arena.AddString(default, false, default);
                            }

                            wcSecrets = secrets;
                            wcSecretsKeyRange = BuildScalarLocation(secretsKeyPos, 7);
                            wcInheritSecrets = inheritSecrets;
                        }

                        break;

                    case ExpectedKeys.JobMappingKey.Snapshot:
                        if (!reader.End)
                        {
                            snapshotNode = ParseSnapshotNode(ref reader, arena, ref diagnostics, source, jobId);
                        }

                        break;
                }

                continue;
            }

            var unknownJobKey = Encoding.UTF8.GetString(keyUtf8);
            var keySlice = reader.GetScalarSlice();

            reader.Read();

            var jobSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknownJobKey, Generated.ExpectedKeys.JobKeys);
            var jobMessage = jobSuggestion is not null
                ? $"jobs.'{DecodeUtf8(source, jobId)}' has unexpected key \"{unknownJobKey}\" for \"job\" section. did you mean \"{jobSuggestion}\"? expected one of {Generated.ExpectedKeys.JobKeys}"
                : $"jobs.'{DecodeUtf8(source, jobId)}' has unexpected key \"{unknownJobKey}\" for \"job\" section. expected one of {Generated.ExpectedKeys.JobKeys}";
            var jobFix = jobSuggestion is not null
                ? new DiagnosticFix($"replace '{unknownJobKey}' with '{jobSuggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, jobSuggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, jobMessage, keyMark, jobFix);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        var mappingEndMark = jobIdMark;
        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            mappingEndMark = reader.CurrentStart;
            reader.Read();
        }

        var decodedJobId = DecodeUtf8(source, jobId);
        var hasUsesKey = hasWorkflowCall && wcUsesKeyRange is not null;
        var hasUsesValue = hasUsesKey && arena.GetStringValue(wcUses).Length > 0;
        var hasSteps = stepsNode.HasValue;
        var hasRunsOn = runsOnNode.HasValue;

        // spec §3.10.1: reusable workflow calls (`uses`) cannot also define `steps`
        if (hasUsesValue && hasSteps)
        {
            AddError(ref diagnostics, $"jobs.'{decodedJobId}' cannot have both uses and steps", stepsKeyPos);
        }

        // spec §3.10.1: reusable workflow calls (`uses`) cannot also define `runs-on`
        if (hasUsesValue && hasRunsOn)
        {
            AddError(ref diagnostics, $"jobs.'{decodedJobId}' cannot have both uses and runs-on", runsOnKeyPos);
        }

        // spec §3.10 post-validation: normal jobs require `runs-on`
        if (!hasUsesKey && !hasRunsOn)
        {
            AddError(ref diagnostics, $"\"runs-on\" section is missing in jobs.'{decodedJobId}'", jobIdMark);
        }

        // spec §3.10 post-validation: normal jobs require `steps`
        if (!hasUsesKey && !hasSteps)
        {
            AddError(ref diagnostics, $"\"steps\" section is missing in jobs.'{decodedJobId}'", jobIdMark);
        }

        if (!hasUsesKey && hasWorkflowCall)
        {
            // spec §3.10 post-validation: `with` is only valid for reusable workflow calls via `uses`
            if (wcInputs.HasValue && wcInputs.Count > 0)
            {
                AddError(ref diagnostics, $"jobs.'{decodedJobId}' key 'with' requires uses", withKeyPos);
            }

            // spec §3.10 post-validation: `secrets` is only valid for reusable workflow calls via `uses`
            if ((wcSecrets.HasValue && wcSecrets.Count > 0) || wcInheritSecrets)
            {
                AddError(ref diagnostics, $"jobs.'{decodedJobId}' key 'secrets' requires uses", secretsKeyPos);
            }
        }

        return arena.AddJob(new JobData
        {
            Id = jobIdNode,
            Name = nameNode,
            Needs = needsNode,
            RunsOn = runsOnNode,
            RunsOnKeyRange = runsOnNode.HasValue ? BuildScalarLocation(runsOnKeyPos, 7) : null,
            Permissions = permissionsNode,
            Environment = environmentNode,
            Concurrency = concurrencyNode,
            Outputs = outputsNode,
            Env = envNode,
            Defaults = defaultsNode,
            If = ifNode,
            IfKeyRange = ifNode.HasValue ? BuildScalarLocation(ifKeyMark, 2) : null,
            Steps = stepsNode,
            StepsKeyRange = stepsNode.HasValue ? BuildScalarLocation(stepsKeyPos, 5) : null,
            TimeoutMinutes = timeoutMinutesNode,
            Strategy = strategyNode,
            ContinueOnError = continueOnErrorNode,
            Container = containerNode,
            Services = servicesNode,
            WorkflowCall = hasWorkflowCall
                ? arena.AddWorkflowCall(new WorkflowCallData
                {
                    Uses = wcUses,
                    UsesKeyRange = wcUsesKeyRange,
                    Inputs = wcInputs,
                    WithKeyRange = wcWithKeyRange,
                    Secrets = wcSecrets,
                    SecretsKeyRange = wcSecretsKeyRange,
                    InheritSecrets = wcInheritSecrets,
                })
                : default,
            Snapshot = snapshotNode,
            Range = BuildCompositeLocation(jobIdMark, mappingEndMark),
        });
    }

    private enum SnapshotMappingKey : byte
    {
        Version = 0,
        ImageName = 1,
        If = 2,
    }

    private readonly struct SnapshotKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 3;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "version"u8,
            1 => "image-name"u8,
            2 => "if"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private static string SnapshotDuplicateKeyName(SnapshotMappingKey key) => key switch
    {
        SnapshotMappingKey.Version => "version",
        SnapshotMappingKey.ImageName => "image-name",
        SnapshotMappingKey.If => "if",
        _ => "snapshot key",
    };

    private static SnapshotId ParseSnapshotNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        string? section = null;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, $"{SectionName(source, jobId, ".snapshot", ref section)} must be object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return arena.AddSnapshot(default);
        }

        var snapshotMark = reader.CurrentStart;
        StringNodeId versionNode = default;
        StringNodeId imageNameNode = default;
        StringNodeId ifNode = default;
        TextPosition ifKeyMark = default;
        ulong seen = 0;

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, $"{SectionName(source, jobId, ".snapshot", ref section)} key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (keyUtf8.SequenceEqual("<<"u8))
            {
                AddError(ref diagnostics, $"GitHub Actions does not support YAML merge key \"<<\". occurred in {SectionName(source, jobId, ".snapshot", ref section)}", keyMark);
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<SnapshotKeyTable>(keyUtf8, out var snapOrd))
            {
                var snapKey = (SnapshotMappingKey)snapOrd;
                reader.Read();
                if (!TrySetBit(ref seen, snapOrd))
                {
                    AddError(ref diagnostics, $"{SectionName(source, jobId, ".snapshot", ref section)} contains duplicate key: {SnapshotDuplicateKeyName(snapKey)}", keyMark);
                    if (!reader.End) reader.SkipCurrentNode();
                    continue;
                }

                switch (snapKey)
                {
                    case SnapshotMappingKey.Version:
                        if (!reader.End)
                        {
                            versionNode = ParseString(ref reader, arena, out var vErr, out var vMark);
                            if (vErr) AddError(ref diagnostics, $"{SectionName(source, jobId, ".snapshot", ref section)}.version must be string", vMark);
                        }

                        break;

                    case SnapshotMappingKey.ImageName:
                        if (!reader.End)
                        {
                            imageNameNode = ParseString(ref reader, arena, out var inErr, out var inMark);
                            if (inErr) AddError(ref diagnostics, $"{SectionName(source, jobId, ".snapshot", ref section)}.image-name must be string", inMark);
                        }

                        break;

                    case SnapshotMappingKey.If:
                        ifKeyMark = keyMark;
                        if (!reader.End)
                        {
                            ifNode = ParseExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobSnapshotIf, out var ifErr, out var ifMark);
                            if (ifErr) AddError(ref diagnostics, $"{SectionName(source, jobId, ".snapshot", ref section)}.if must be string", ifMark);
                        }

                        break;
                }

                continue;
            }

            var keySlice = reader.GetScalarSlice();
            var unknownSnapKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var snapSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknownSnapKey, Generated.ExpectedKeys.SnapshotKeys);
            var snapMessage = snapSuggestion is not null
                ? $"{SectionName(source, jobId, ".snapshot", ref section)} has unexpected key \"{unknownSnapKey}\" for \"snapshot\" section. did you mean \"{snapSuggestion}\"? expected one of {Generated.ExpectedKeys.SnapshotKeys}"
                : $"{SectionName(source, jobId, ".snapshot", ref section)} has unexpected key \"{unknownSnapKey}\" for \"snapshot\" section. expected one of {Generated.ExpectedKeys.SnapshotKeys}";
            var snapFix = snapSuggestion is not null
                ? new DiagnosticFix($"replace '{unknownSnapKey}' with '{snapSuggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, snapSuggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, snapMessage, keyMark, snapFix);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        // image-name is required
        if (!imageNameNode.HasValue)
        {
            AddError(ref diagnostics, "\"snapshot\" section must have \"image-name\" configuration", snapshotMark);
        }

        return arena.AddSnapshot(new SnapshotData
        {
            Version = versionNode,
            ImageName = imageNameNode,
            If = ifNode,
            IfKeyRange = ifNode.HasValue ? BuildScalarLocation(ifKeyMark, 2) : null,
        });
    }


    private static RunnerId ParseRunsOnNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        string? section = null;

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            StringIdRange labels = default;
            StringNodeId labelsExpr = default;
            StringNodeId group = default;
            ulong seen = 0;
            var hasUnknownKey = false;
            var mappingStartMark = reader.CurrentStart;

            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(ref diagnostics, $"{SectionName(source, jobId, ".runs-on", ref section)} key must be string", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var keyMark = reader.CurrentStart;
                var keyUtf8 = reader.GetScalarUtf8();
                if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "runs-on"))
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
                        AddError(ref diagnostics, $"runs-on contains duplicate key: {RunsOnDuplicateKeyName(roKey)}", keyMark);
                        if (!reader.End) reader.SkipCurrentNode();
                        continue;
                    }

                    switch (roKey)
                    {
                        case RunsOnMappingKey.Labels:
                            if (!reader.End)
                            {
                                if (reader.CurrentKind == YamlEventKind.MappingStart)
                                {
                                    AddError(ref diagnostics, $"{SectionName(source, jobId, ".runs-on", ref section)}.labels must be string or array, got object", reader.CurrentStart);
                                    reader.SkipCurrentNode();
                                }
                                else if (reader.CurrentKind == YamlEventKind.Scalar)
                                {
                                    var valueUtf8 = reader.GetScalarUtf8();
                                    if (ContainsExpression(valueUtf8))
                                    {
                                        labelsExpr = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobRunsOn, out var lblExprErr, out var lblExprMark, parseWholeValueIfNoEmbedded: false);
                                        if (lblExprErr) AddError(ref diagnostics, $"{SectionName(source, jobId, ".runs-on", ref section)}.labels must be string, array, or expression", lblExprMark);
                                    }
                                    else
                                    {
                                        labels = ParseStringOrStringSequence(ref reader, arena, ref diagnostics, out var lblErr1, out var lblMark1);
                                        if (lblErr1)
                                        {
                                            if (labels.Count > 0)
                                                AddError(ref diagnostics, RunsOnEmptyLabelMessage, lblMark1);
                                            else
                                                AddError(ref diagnostics, $"{SectionName(source, jobId, ".runs-on", ref section)}.labels must be string, array, or expression", lblMark1);
                                        }
                                    }
                                }
                                else
                                {
                                    var lblSeqMark = reader.CurrentStart;
                                    labels = ParseStringOrStringSequence(ref reader, arena, ref diagnostics, out var lblErr2, out var lblMark2, allowElemEmpty: true, emptyElementMessage: RunsOnEmptyLabelMessage);
                                    if (lblErr2)
                                    {
                                        AddError(ref diagnostics, $"{SectionName(source, jobId, ".runs-on", ref section)}.labels must be string, array, or expression", lblMark2);
                                    }
                                    else if (labels.Count == 0)
                                    {
                                        AddError(ref diagnostics, LabelsSectionEmptyMessage, lblSeqMark);
                                    }
                                }
                            }

                            break;

                        case RunsOnMappingKey.Group:
                            if (!reader.End && reader.CurrentKind != YamlEventKind.Scalar)
                            {
                                var grpNodeType = reader.CurrentKind == YamlEventKind.SequenceStart ? "array" : "object";
                                AddError(ref diagnostics, $"{SectionName(source, jobId, ".runs-on", ref section)}.group must be string, got {grpNodeType}", reader.CurrentStart);
                                reader.SkipCurrentNode();
                            }
                            else if (!reader.End && reader.GetScalarUtf8().Length == 0)
                            {
                                AddError(ref diagnostics, "\"runs-on\" group should not be empty", reader.CurrentStart);
                                reader.Read();
                            }
                            else
                            {
                                group = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobRunsOn, out var grpErr, out var grpMark, parseWholeValueIfNoEmbedded: false);
                                if (grpErr) AddError(ref diagnostics, $"{SectionName(source, jobId, ".runs-on", ref section)}.group must be string", grpMark);
                            }
                            break;
                    }

                    continue;
                }

                var keySlice = reader.GetScalarSlice();
                var unknownRunsOnKey = Encoding.UTF8.GetString(keyUtf8);
                reader.Read();
                var runsOnSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknownRunsOnKey, Generated.ExpectedKeys.RunsOnKeys);
                var runsOnMessage = runsOnSuggestion is not null
                    ? $"jobs.'{DecodeUtf8(source, jobId)}'.runs-on has unexpected key \"{unknownRunsOnKey}\" for \"runs-on\" section. did you mean \"{runsOnSuggestion}\"? expected one of {Generated.ExpectedKeys.RunsOnKeys}"
                    : $"jobs.'{DecodeUtf8(source, jobId)}'.runs-on has unexpected key \"{unknownRunsOnKey}\" for \"runs-on\" section. expected one of {Generated.ExpectedKeys.RunsOnKeys}";
                var runsOnFix = runsOnSuggestion is not null
                    ? new DiagnosticFix($"replace '{unknownRunsOnKey}' with '{runsOnSuggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, runsOnSuggestion)])
                    : (DiagnosticFix?)null;
                AddError(ref diagnostics, runsOnMessage, keyMark, runsOnFix);
                hasUnknownKey = true;
                if (!reader.End) reader.SkipCurrentNode();
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            if (!labels.HasValue && !labelsExpr.HasValue && !hasUnknownKey && (seen & (1UL << (int)RunsOnMappingKey.Group)) == 0 && (seen & (1UL << (int)RunsOnMappingKey.Labels)) == 0)
            {
                AddError(ref diagnostics, $"{SectionName(source, jobId, ".runs-on", ref section)} requires labels", mappingStartMark);
            }

            return arena.AddRunner(new RunnerData
            {
                Labels = labels,
                LabelsExpr = labelsExpr,
                Group = group,
                Range = labelsExpr.HasValue ? arena.GetStringRange(labelsExpr) : group.HasValue ? arena.GetStringRange(group) : (labels.Count > 0 ? arena.GetStringRange(arena.GetStringIdAt(labels, 0)) : default),
            });
        }

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var scalarUtf8 = reader.GetScalarUtf8();
            if (ContainsExpression(scalarUtf8))
            {
                var expr = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobRunsOn, out var roExprErr, out var roExprMark, parseWholeValueIfNoEmbedded: false);
                if (roExprErr) AddError(ref diagnostics, $"{SectionName(source, jobId, ".runs-on", ref section)} must be string, sequence, or mapping", roExprMark);
                return arena.AddRunner(new RunnerData
                {
                    LabelsExpr = expr,
                    Range = expr.HasValue ? arena.GetStringRange(expr) : default,
                });
            }
        }

        var fbSeqMark = reader.CurrentStart;
        var fbWasScalar = reader.CurrentKind == YamlEventKind.Scalar;
        var labelsFallback = ParseStringOrStringSequence(ref reader, arena, ref diagnostics, out var lblFbErr, out var lblFbMark, allowElemEmpty: true, emptyElementMessage: RunsOnEmptyLabelMessage);
        if (lblFbErr)
        {
            if (fbWasScalar)
                AddError(ref diagnostics, RunsOnEmptyLabelMessage, lblFbMark);
            else
                AddError(ref diagnostics, $"{SectionName(source, jobId, ".runs-on", ref section)} must be string, sequence, or mapping", lblFbMark);
        }
        else if (labelsFallback.Count == 0)
        {
            AddError(ref diagnostics, RunsOnSectionEmptyMessage, fbSeqMark);
        }
        return arena.AddRunner(new RunnerData
        {
            Labels = labelsFallback,
            Range = labelsFallback.Count > 0 ? arena.GetStringRange(arena.GetStringIdAt(labelsFallback, 0)) : default,
        });
    }

    private static EnvironmentId ParseEnvironmentNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, TextPosition environmentKeyMark)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var name = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobEnvironment, out var envNameErr, out var envNameMark, false);
            if (envNameErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.environment must be string or object", envNameMark);
            if (!name.HasValue)
            {
                return default;
            }

            return arena.AddEnvironment(new EnvironmentData
            {
                Name = name,
                Range = arena.GetStringRange(name),
            });
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.environment must be string or object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        StringNodeId nameNode = default;
        StringNodeId urlNode = default;
        BoolNodeId deploymentNode = default;
        ulong seen = 0;
        var mappingMark = reader.CurrentStart;

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.environment key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "environment"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("name"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(ref diagnostics, "environment contains duplicate key: name", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                nameNode = ParseString(ref reader, arena, out var envNErr, out var envNMark);
                if (envNErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.environment.name must be string", envNMark);
                continue;
            }

            if (keyUtf8.SequenceEqual("url"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(ref diagnostics, "environment contains duplicate key: url", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                urlNode = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobEnvironmentUrl, out var urlErr, out var urlMark, parseWholeValueIfNoEmbedded: false);
                if (urlErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.environment.url must be string", urlMark);
                continue;
            }

            if (keyUtf8.SequenceEqual("deployment"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(ref diagnostics, "environment contains duplicate key: deployment", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                deploymentNode = ParseBoolOrExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobEnvironment, out var depErr, out var depMark);
                if (depErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.environment.deployment must be bool or expression", depMark);
                continue;
            }

            var keySlice = reader.GetScalarSlice();
            var unknownEnvKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var envSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknownEnvKey, Generated.ExpectedKeys.EnvironmentKeys);
            var envMessage = envSuggestion is not null
                ? $"jobs.'{DecodeUtf8(source, jobId)}'.environment has unexpected key \"{unknownEnvKey}\" for \"environment\" section. did you mean \"{envSuggestion}\"? expected one of {Generated.ExpectedKeys.EnvironmentKeys}"
                : $"jobs.'{DecodeUtf8(source, jobId)}'.environment has unexpected key \"{unknownEnvKey}\" for \"environment\" section. expected one of {Generated.ExpectedKeys.EnvironmentKeys}";
            var envFix = envSuggestion is not null
                ? new DiagnosticFix($"replace '{unknownEnvKey}' with '{envSuggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, envSuggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, envMessage, keyMark, envFix);
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
            AddError(ref diagnostics, "name is missing in \"environment\" section", environmentKeyMark);
            return default;
        }

        return arena.AddEnvironment(new EnvironmentData
        {
            Name = nameNode,
            Url = urlNode,
            Deployment = deploymentNode,
            Range = arena.GetStringRange(nameNode),
        });
    }

    private static NodeRange ParseOutputsNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.outputs must be object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        // Output rows are appended contiguously: values are scalar parses only, so nested
        // parsing never touches the job-output table.
        var outputsFirst = arena.JobOutputCount;
        var outputCount = 0;
        Span<long> keyStore = stackalloc long[64];
        var keyCount = 0;
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.outputs key must be string", reader.CurrentStart);
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
                ref diagnostics,
                keyStore,
                ref keyCount,
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

            var value = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobOutputs, out var outErr, out var outMark, parseWholeValueIfNoEmbedded: false);
            if (outErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.outputs.{Encoding.UTF8.GetString(keyUtf8)} must be string", outMark);
            arena.AddJobOutput(new JobOutputData { Key = keySlice, Value = value.HasValue ? value : keyNode });
            outputCount++;
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new NodeRange(outputsFirst, outputCount);
    }

    private static NodeRange ParseWorkflowCallInputsNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.with must be object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        // Input rows are appended contiguously: values are scalars, so nested parsing
        // never touches the workflow-call input table.
        var inputsFirst = arena.WorkflowCallInputCount;
        var inputCount = 0;
        Span<long> keyStore = stackalloc long[64];
        var keyCount = 0;
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.with must be object", reader.CurrentStart);
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
            if (!TryRegisterDynamicKey(source, nameUtf8, nameSlice.Offset, nameSlice.Length, nameMark, ref diagnostics, keyStore, ref keyCount, "with"))
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
                valueNode = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobWith, out var withErr, out var withMark, parseWholeValueIfNoEmbedded: false);
                if (withErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.with.{Encoding.UTF8.GetString(nameUtf8)} must be string", withMark);
            }
            catch
            {
                AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.with.{Encoding.UTF8.GetString(nameUtf8)} must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                valueNode = default;
            }

            if (valueNode.HasValue)
            {
                arena.AddWorkflowCallInput(new WorkflowCallInputData
                {
                    Key = nameSlice,
                    Name = nameNode,
                    Value = valueNode,
                });
                inputCount++;
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new NodeRange(inputsFirst, inputCount);
    }

    private static NodeRange ParseWorkflowCallSecretsNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, out bool inheritSecrets)
        where TReader : IYamlStreamReader, allows ref struct
    {
        inheritSecrets = false;

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var valueUtf8 = reader.GetScalarUtf8();
            if (!valueUtf8.SequenceEqual("inherit"u8))
            {
                AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.secrets scalar must be 'inherit'", reader.CurrentStart);
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
            AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.secrets must be object or scalar 'inherit'", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        // Secret rows are appended contiguously: values are scalars, so nested parsing
        // never touches the workflow-call secret table.
        var secretsFirst = arena.WorkflowCallSecretCount;
        var secretCount = 0;
        Span<long> keyStore = stackalloc long[64];
        var keyCount = 0;
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.secrets must be object or scalar 'inherit'", reader.CurrentStart);
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
            if (!TryRegisterDynamicKey(source, nameUtf8, nameSlice.Offset, nameSlice.Length, nameMark, ref diagnostics, keyStore, ref keyCount, "secrets"))
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
            if (secErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.secrets.{Encoding.UTF8.GetString(nameUtf8)} must be string", secMark);
            if (valueNode.HasValue)
            {
                var valueUtf8 = arena.GetStringValue(valueNode);
                var valueLocation = BuildLocationFromSourceSlice(source, arena.GetStringSlice(valueNode).Offset, valueUtf8.Length);
                ValidateExpressionText(valueUtf8, valueLocation, ExpressionValidationContext.JobSecrets, ref diagnostics, parseWholeValueIfNoEmbedded: false);
            }

            if (valueNode.HasValue)
            {
                arena.AddWorkflowCallSecret(new WorkflowCallSecretData
                {
                    Key = nameSlice,
                    Name = nameNode,
                    Value = valueNode,
                });
                secretCount++;
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new NodeRange(secretsFirst, secretCount);
    }

}

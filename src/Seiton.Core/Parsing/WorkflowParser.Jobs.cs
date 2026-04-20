using System.Text;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static Job ParseJobNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, TextPosition jobIdMark, StringNode jobIdNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNode? nameNode = null;
        StringNode[]? needsNode = null;
        Runner? runsOnNode = null;
        Permissions? permissionsNode = null;
        Seiton.Core.Parsing.Ast.Environment? environmentNode = null;
        Concurrency? concurrencyNode = null;
        Dictionary<Utf8String, StringNode>? outputsNode = null;
        Env? envNode = null;
        Defaults? defaultsNode = null;
        StringNode? ifNode = null;
        Step[]? stepsNode = null;
        FloatNode? timeoutMinutesNode = null;
        Strategy? strategyNode = null;
        BoolNode? continueOnErrorNode = null;
        Container? containerNode = null;
        Services? servicesNode = null;
        WorkflowCall? workflowCallNode = null;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new Job { Id = jobIdNode, Range = jobIdNode.Range };
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

            if (keyUtf8.SequenceEqual("runs-on"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: runs-on", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "runs-on";
                    stepsOnlyKeyInReusableMark = keyMark;
                }

                if (!reader.End)
                {
                    runsOnNode = ParseRunsOnNode(ref reader, diagnostics, source, jobId);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("name"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: name", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    nameNode = ParseString(ref reader, out var nameErr, out var nameMark);
                    if (nameErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' name must be scalar", nameMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("needs"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: needs", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    needsNode = ParseStringOrStringSequence(ref reader, diagnostics, out var needsErr, out var needsMark);
                    if (needsErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' needs must be scalar or sequence of scalar", needsMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 3)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: env", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "env";
                    stepsOnlyKeyInReusableMark = keyMark;
                }

                if (!reader.End)
                {
                    envNode = ParseEnvNode(ref reader, diagnostics, source, $"job '{DecodeUtf8(source, jobId)}' env must be mapping", ExpressionValidationContext.Job);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("steps"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 4)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: steps", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
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
                        stepsNode = ParseSteps(ref reader, diagnostics, source, jobId);
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("uses"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 5)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: uses", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    var usesNode = ParseString(ref reader, out var usesErr, out var usesMark);
                    if (usesErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' uses must be scalar", usesMark);
                    workflowCallNode = new WorkflowCall
                    {
                        Uses = usesNode ?? new StringNode { Value = default, Quoted = false, Range = default },
                        UsesKeyRange = BuildScalarLocation(keyMark, keyUtf8.Length),
                        Inputs = workflowCallNode?.Inputs,
                        Secrets = workflowCallNode?.Secrets,
                        InheritSecrets = workflowCallNode?.InheritSecrets ?? false,
                    };
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("if"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 6)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: if", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    ifNode = ParseExpression(ref reader, diagnostics, ExpressionValidationContext.Job, out var ifErr, out var ifMark);
                    if (ifErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' if must be scalar", ifMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("permissions"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 7)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: permissions", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    permissionsNode = ParsePermissionsNode(ref reader, diagnostics, source, $"job '{DecodeUtf8(source, jobId)}' permissions must be scalar or mapping");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("environment"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 8)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: environment", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "environment";
                    stepsOnlyKeyInReusableMark = keyMark;
                }

                if (!reader.End)
                {
                    environmentNode = ParseEnvironmentNode(ref reader, diagnostics, source, jobId);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("concurrency"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 9)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: concurrency", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    concurrencyNode = ParseConcurrencyNode(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' concurrency must be scalar or mapping", ExpressionValidationContext.Job);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("outputs"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 10)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: outputs", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "outputs";
                    stepsOnlyKeyInReusableMark = keyMark;
                }

                if (!reader.End)
                {
                    outputsNode = ParseOutputsNode(ref reader, diagnostics, source, jobId);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("defaults"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 11)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: defaults", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "defaults";
                    stepsOnlyKeyInReusableMark = keyMark;
                }

                if (!reader.End)
                {
                    defaultsNode = ParseDefaultsNode(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' defaults must be mapping");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("timeout-minutes"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 12)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: timeout-minutes", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "timeout-minutes";
                    stepsOnlyKeyInReusableMark = keyMark;
                }

                if (!reader.End)
                {
                    timeoutMinutesNode = ParseFloat(ref reader, out var tmErr, out var tmMark);
                    if (tmErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' timeout-minutes must be number", tmMark);
                    if (timeoutMinutesNode is not null && timeoutMinutesNode.Value <= 0)
                    {
                        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' timeout-minutes must be greater than 0", keyMark);
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("continue-on-error"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 13)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: continue-on-error", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "continue-on-error";
                    stepsOnlyKeyInReusableMark = keyMark;
                }

                if (!reader.End)
                {
                    continueOnErrorNode = ParseBoolOrExpression(ref reader, diagnostics, ExpressionValidationContext.Job, out var coeErr, out var coeMark);
                    if (coeErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' continue-on-error must be bool or expression", coeMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("strategy"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 14)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: strategy", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    if (reader.CurrentKind != YamlEventKind.MappingStart)
                    {
                        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy must be mapping", reader.CurrentStart);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        strategyNode = ParseStrategy(ref reader, diagnostics, source, jobId);
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("container"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 15)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: container", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "container";
                    stepsOnlyKeyInReusableMark = keyMark;
                }

                if (!reader.End)
                {
                    containerNode = ParseContainerLike(ref reader, diagnostics, source, jobId, default, isService: false, requireImage: true);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("services"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 16)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: services", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "services";
                    stepsOnlyKeyInReusableMark = keyMark;
                }

                if (!reader.End)
                {
                    servicesNode = ParseServices(ref reader, diagnostics, source, jobId);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("with"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 17)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: with", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    var inputs = ParseWorkflowCallInputsNode(ref reader, diagnostics, source, jobId);
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
                            Uses = new StringNode { Value = default, Quoted = false, Range = default },
                            UsesKeyRange = null,
                            Inputs = inputs,
                            Secrets = null,
                            InheritSecrets = false,
                        };
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("secrets"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 18)) { AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' contains duplicate key: secrets", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    var secrets = ParseWorkflowCallSecretsNode(ref reader, diagnostics, source, jobId, out var inheritSecrets);
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
                            Uses = new StringNode { Value = default, Quoted = false, Range = default },
                            UsesKeyRange = null,
                            Inputs = null,
                            Secrets = secrets,
                            InheritSecrets = inheritSecrets,
                        };
                    }
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
        var hasUses = workflowCallNode is not null && workflowCallNode.Uses.Value.Length > 0;
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
            if (workflowCallNode.Inputs is not null && workflowCallNode.Inputs.Count > 0)
            {
                AddError(diagnostics, $"job '{decodedJobId}' key 'with' requires uses", jobIdMark);
            }

            // spec §3.10 post-validation: `secrets` is only valid for reusable workflow calls via `uses`
            if ((workflowCallNode.Secrets is not null && workflowCallNode.Secrets.Count > 0) || workflowCallNode.InheritSecrets)
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
            Range = jobIdNode.Range,
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


    private static Runner? ParseRunsOnNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var section = $"job '{DecodeUtf8(source, jobId)}' runs-on";

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            StringNode[]? labels = null;
            StringNode? labelsExpr = null;
            StringNode? group = null;
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

                if (keyUtf8.SequenceEqual("labels"u8))
                {
                    reader.Read();
                    if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "runs-on contains duplicate key: labels", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                    if (!reader.End)
                    {
                        if (reader.CurrentKind == YamlEventKind.Scalar)
                        {
                            var valueUtf8 = reader.GetScalarUtf8();
                            if (ContainsExpression(valueUtf8))
                            {
                                labelsExpr = ParseStringAndValidateExpression(ref reader, diagnostics, ExpressionValidationContext.Job, out var lblExprErr, out var lblExprMark, parseWholeValueIfNoEmbedded: false);
                                if (lblExprErr) AddError(diagnostics, $"{section}.labels must be scalar, sequence, or expression", lblExprMark);
                            }
                            else
                            {
                                labels = ParseStringOrStringSequence(ref reader, diagnostics, out var lblErr1, out var lblMark1);
                                if (lblErr1) AddError(diagnostics, $"{section}.labels must be scalar, sequence, or expression", lblMark1);
                            }
                        }
                        else
                        {
                            labels = ParseStringOrStringSequence(ref reader, diagnostics, out var lblErr2, out var lblMark2);
                            if (lblErr2) AddError(diagnostics, $"{section}.labels must be scalar, sequence, or expression", lblMark2);
                        }
                    }

                    continue;
                }

                if (keyUtf8.SequenceEqual("group"u8))
                {
                    reader.Read();
                    if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "runs-on contains duplicate key: group", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                    group = ParseStringAndValidateExpression(ref reader, diagnostics, ExpressionValidationContext.Job, out var grpErr, out var grpMark, parseWholeValueIfNoEmbedded: false);
                    if (grpErr) AddError(diagnostics, $"{section}.group must be scalar", grpMark);
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

            if (labels is null && labelsExpr is null)
            {
                AddError(diagnostics, $"{section} requires labels", new TextPosition(0, 1, 1));
            }

            return new Runner
            {
                Labels = labels,
                LabelsExpr = labelsExpr,
                Group = group,
                Range = labelsExpr?.Range ?? group?.Range ?? (labels is { Length: > 0 } ? labels[0].Range : default),
            };
        }

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var scalarUtf8 = reader.GetScalarUtf8();
            if (ContainsExpression(scalarUtf8))
            {
                var expr = ParseStringAndValidateExpression(ref reader, diagnostics, ExpressionValidationContext.Job, out var roExprErr, out var roExprMark, parseWholeValueIfNoEmbedded: false);
                if (roExprErr) AddError(diagnostics, $"{section} must be scalar, sequence, or mapping", roExprMark);
                return new Runner
                {
                    LabelsExpr = expr,
                    Range = expr?.Range ?? default,
                };
            }
        }

        var labelsFallback = ParseStringOrStringSequence(ref reader, diagnostics, out var lblFbErr, out var lblFbMark);
        if (lblFbErr) AddError(diagnostics, $"{section} must be scalar, sequence, or mapping", lblFbMark);
        return new Runner
        {
            Labels = labelsFallback,
            Range = labelsFallback.Length > 0 ? labelsFallback[0].Range : default,
        };
    }

    private static Seiton.Core.Parsing.Ast.Environment? ParseEnvironmentNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var name = ParseString(ref reader, out var envNameErr, out var envNameMark);
            if (envNameErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment must be scalar or mapping", envNameMark);
            return name is null
                ? null
                : new Seiton.Core.Parsing.Ast.Environment
                {
                    Name = name,
                    Range = name.Range,
                };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment must be scalar or mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        StringNode? nameNode = null;
        StringNode? urlNode = null;
        BoolNode? deploymentNode = null;
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
                nameNode = ParseString(ref reader, out var envNErr, out var envNMark);
                if (envNErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment.name must be scalar", envNMark);
                continue;
            }

            if (keyUtf8.SequenceEqual("url"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "environment contains duplicate key: url", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                urlNode = ParseStringAndValidateExpression(ref reader, diagnostics, ExpressionValidationContext.Job, out var urlErr, out var urlMark, parseWholeValueIfNoEmbedded: false);
                if (urlErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment.url must be scalar", urlMark);
                continue;
            }

            if (keyUtf8.SequenceEqual("deployment"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, "environment contains duplicate key: deployment", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                deploymentNode = ParseBoolOrExpression(ref reader, diagnostics, ExpressionValidationContext.Job, out var depErr, out var depMark);
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
        if (nameNode is null)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment.name is required", jobId.Length > 0 ? new TextPosition(0, 1, 1) : new TextPosition(0, 1, 1));
            return null;
        }

        return new Seiton.Core.Parsing.Ast.Environment
        {
            Name = nameNode,
            Url = urlNode,
            Deployment = deploymentNode,
            Range = nameNode.Range,
        };
    }

    private static Dictionary<Utf8String, StringNode>? ParseOutputsNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' outputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var outputs = new Dictionary<Utf8String, StringNode>();
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

            var keyNode = new StringNode { Value = keySlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(reader.CurrentStart, keyUtf8.Length) };
            var key = Utf8String.FromLowerAscii(keyUtf8);
            reader.Read();
            if (reader.End)
            {
                break;
            }

            var value = ParseStringAndValidateExpression(ref reader, diagnostics, ExpressionValidationContext.JobOutput, out var outErr, out var outMark, parseWholeValueIfNoEmbedded: false);
            if (outErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' outputs.{Encoding.UTF8.GetString(keyUtf8)} must be scalar", outMark);
            outputs[key] = value ?? keyNode;
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return outputs;
    }

    private static Dictionary<Utf8String, WorkflowCallInput>? ParseWorkflowCallInputsNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' with must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, WorkflowCallInput>();
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

            var key = Utf8String.FromLowerAscii(nameUtf8);
            var nameNode = new StringNode { Value = nameSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(nameMark, nameUtf8.Length) };
            reader.Read();
            if (reader.End)
            {
                break;
            }

            StringNode? valueNode;
            try
            {
                valueNode = ParseStringAndValidateExpression(ref reader, diagnostics, ExpressionValidationContext.Job, out var withErr, out var withMark, parseWholeValueIfNoEmbedded: false);
                if (withErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' with.{Encoding.UTF8.GetString(nameUtf8)} must be scalar", withMark);
            }
            catch
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' with.{Encoding.UTF8.GetString(nameUtf8)} must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                valueNode = null;
            }

            if (valueNode is not null)
            {
                map[key] = new WorkflowCallInput { Name = nameNode, Value = valueNode };
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
    }

    private static Dictionary<Utf8String, WorkflowCallSecret>? ParseWorkflowCallSecretsNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, out bool inheritSecrets)
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
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' secrets must be mapping or scalar 'inherit'", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, WorkflowCallSecret>();
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

            var key = Utf8String.FromLowerAscii(nameUtf8);
            var nameNode = new StringNode { Value = nameSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(nameMark, nameUtf8.Length) };
            reader.Read();
            if (reader.End)
            {
                break;
            }

            var valueNode = ParseString(ref reader, out var secErr, out var secMark, allowEmpty: false);
            if (secErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' secrets.{Encoding.UTF8.GetString(nameUtf8)} must be scalar", secMark);
            if (valueNode is not null)
            {
                var valueUtf8 = valueNode.Value.AsSpan(source);
                var valueLocation = BuildLocationFromSourceSlice(source, valueNode.Value.Offset, valueUtf8.Length);
                ValidateExpressionText(valueUtf8, valueLocation, ExpressionValidationContext.ReusableWorkflowCallSecrets, diagnostics, parseWholeValueIfNoEmbedded: false);
            }

            if (valueNode is not null)
            {
                map[key] = new WorkflowCallSecret { Name = nameNode, Value = valueNode };
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
    }

    private static void ParseJobSecrets<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
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

        ParseStringMapping(ref reader, diagnostics, source, $"job '{DecodeUtf8(source, jobId)}' secrets must be mapping or scalar 'inherit'");
    }

}

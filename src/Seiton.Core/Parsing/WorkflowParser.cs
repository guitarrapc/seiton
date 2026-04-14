using System.Text;
using System.Buffers.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static class WorkflowParser
{
    private delegate string? Utf8ScalarValidator(ReadOnlySpan<byte> valueUtf8);

    private enum MappingKeyComparison
    {
        CaseSensitive,
        AsciiCaseInsensitive,
    }

    private readonly struct OnEventInfo
    {
        public OnEventInfo(string name, bool isKnown, WebhookTypes.EventSpec spec)
        {
            Name = name;
            IsKnown = isKnown;
            Spec = spec;
        }

        public string Name { get; }

        public bool IsKnown { get; }

        public WebhookTypes.EventSpec Spec { get; }
    }

    public static ParseResult Parse(byte[] utf8Yaml, string filePath)
    {
        var reader = new VYamlStreamAdapter(utf8Yaml.AsMemory());
        return Parse(ref reader, utf8Yaml, filePath, utf8Yaml);
    }

    internal static ParseResult Parse(ref VYamlStreamAdapter reader, ReadOnlySpan<byte> source, string filePath, byte[]? sourceBytes = null)
    {
        var diagnostics = new List<Diagnostic>(16);

        reader.SkipHeader();

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "workflow root must be mapping", reader.CurrentStart);
            return new ParseResult(default, diagnostics.ToArray(), HasFatalError: true);
        }

        reader.Read(); // skip MappingStart

        StringNode? nameNode = null;
        StringNode? runNameNode = null;
        Permissions? permissionsNode = null;
        Env? envNode = null;
        Defaults? defaultsNode = null;
        Concurrency? concurrencyNode = null;
        var hasOn = false;
        var hasJobs = false;
        Event[] onEvents = [];
        Dictionary<Utf8String, Job> jobs = [];
        var workflowKeys = new HashSet<Utf8String>();

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "workflow key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                workflowKeys,
                MappingKeyComparison.CaseSensitive,
                "workflow"))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("name"u8))
            {
                reader.Read(); // consume key
                nameNode = ParseString(ref reader, diagnostics, "name must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("run-name"u8))
            {
                reader.Read(); // consume key
                runNameNode = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Workflow,
                    "run-name must be scalar",
                    parseWholeValueIfNoEmbedded: false);
                continue;
            }

            if (keyUtf8.SequenceEqual("on"u8))
            {
                reader.Read(); // consume key
                hasOn = true;
                if (!reader.End)
                {
                    if (reader.CurrentKind is not YamlEventKind.Scalar and not YamlEventKind.MappingStart and not YamlEventKind.SequenceStart)
                    {
                        AddError(diagnostics, "on must be scalar, mapping, or sequence", reader.CurrentStart);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        onEvents = ParseOnEvents(ref reader, diagnostics);
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("jobs"u8))
            {
                reader.Read(); // consume key
                hasJobs = true;
                if (!reader.End)
                {
                    if (reader.CurrentKind != YamlEventKind.MappingStart)
                    {
                        AddError(diagnostics, "jobs must be mapping", reader.CurrentStart);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        jobs = ParseJobsMapping(ref reader, diagnostics, source);
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    envNode = ParseEnvNode(
                        ref reader,
                        diagnostics,
                        source,
                        "workflow env must be mapping",
                        ExpressionValidationContext.Workflow);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("permissions"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    permissionsNode = ParsePermissionsNode(ref reader, diagnostics, source, "workflow permissions must be scalar or mapping");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("defaults"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    defaultsNode = ParseDefaultsNode(ref reader, diagnostics, "workflow defaults must be mapping");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("concurrency"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    concurrencyNode = ParseConcurrencyNode(ref reader, diagnostics, "workflow concurrency must be scalar or mapping", ExpressionValidationContext.Workflow);
                }
                continue;
            }

            var keyText = Encoding.UTF8.GetString(keyUtf8);
            reader.Read(); // consume key

            AddError(diagnostics, $"unexpected workflow key: {keyText}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (!hasOn)
        {
            AddError(diagnostics, "required key 'on' is missing", new TextPosition(0, 1, 1));
        }

        if (!hasJobs)
        {
            AddError(diagnostics, "required key 'jobs' is missing", new TextPosition(0, 1, 1));
        }

        var workflow = new Workflow
        {
            Name = nameNode,
            RunName = runNameNode,
            On = onEvents,
            Permissions = permissionsNode,
            Env = envNode,
            Defaults = defaultsNode,
            Concurrency = concurrencyNode,
            Jobs = jobs,
            Range = default,
        };

        return new ParseResult(workflow, diagnostics.ToArray(), HasFatalError: false);
    }

    private static Permissions? ParsePermissionsNode(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        string error)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var all = ParseString(ref reader, diagnostics, error);
            return all is null
                ? null
                : new Permissions
                {
                    All = all,
                    Range = all.Range,
                };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var scopes = new Dictionary<Utf8String, PermissionScope>();
        var keys = new HashSet<Utf8String>();
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keySlice = reader.GetScalarSlice();
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "permissions"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyNode = new StringNode
            {
                Value = keySlice,
                Quoted = reader.IsScalarQuoted(),
                Range = BuildScalarLocation(keyMark, keyUtf8.Length),
            };

            reader.Read(); // consume key
            if (reader.End)
            {
                break;
            }

            var valueNode = ParseString(ref reader, diagnostics, error);
            if (valueNode is null)
            {
                continue;
            }

            scopes[keySlice.ToUtf8String(source)] = new PermissionScope
            {
                Name = keyNode,
                Value = valueNode,
            };
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new Permissions
        {
            Scopes = scopes,
            Range = default,
        };
    }

    private static Env? ParseEnvNode(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        string error,
        ExpressionValidationContext expressionContext)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var expression = ParseStringAndValidateExpression(ref reader, diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
            return expression is null
                ? null
                : new Env
                {
                    Expression = expression,
                    Range = expression.Range,
                };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var vars = new Dictionary<Utf8String, EnvVar>();
        var keys = new HashSet<Utf8String>();
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keySlice = reader.GetScalarSlice();
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                error))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyNode = new StringNode
            {
                Value = keySlice,
                Quoted = reader.IsScalarQuoted(),
                Range = BuildScalarLocation(keyMark, keyUtf8.Length),
            };

            reader.Read(); // consume key
            if (reader.End)
            {
                break;
            }

            var valueNode = ParseStringAndValidateExpression(ref reader, diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
            if (valueNode is null)
            {
                continue;
            }

            vars[keySlice.ToUtf8String(source)] = new EnvVar
            {
                Name = keyNode,
                Value = valueNode,
            };
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new Env
        {
            Vars = vars,
            Range = default,
        };
    }

    private static Defaults? ParseDefaultsNode(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, string error)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        StringNode? shellNode = null;
        StringNode? workingDirectoryNode = null;
        var keys = new HashSet<Utf8String>();

        reader.Read(); // consume defaults mapping
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "workflow defaults"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var isRun = keyUtf8.SequenceEqual("run"u8);
            reader.Read(); // consume key
            if (reader.End)
            {
                break;
            }

            if (isRun)
            {
                if (reader.CurrentKind != YamlEventKind.MappingStart)
                {
                    AddError(diagnostics, "workflow defaults.run must be mapping", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    continue;
                }

                var runKeys = new HashSet<Utf8String>();
                reader.Read(); // consume run mapping
                while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    if (reader.CurrentKind != YamlEventKind.Scalar)
                    {
                        AddError(diagnostics, "workflow defaults.run must be mapping", reader.CurrentStart);
                        reader.SkipCurrentNode();
                        if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                        {
                            reader.SkipCurrentNode();
                        }
                        continue;
                    }

                    var runKeyMark = reader.CurrentStart;
                    var runKeyUtf8 = reader.GetScalarUtf8();
                    if (!TryRegisterMappingKey(
                        runKeyUtf8,
                        runKeyMark,
                        diagnostics,
                        runKeys,
                        MappingKeyComparison.AsciiCaseInsensitive,
                        "workflow defaults.run"))
                    {
                        reader.Read();
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                    }

                    var isShell = runKeyUtf8.SequenceEqual("shell"u8);
                    var isWorkingDirectory = runKeyUtf8.SequenceEqual("working-directory"u8);
                    reader.Read();
                    if (reader.End)
                    {
                        break;
                    }

                    if (isShell)
                    {
                        shellNode = ParseString(ref reader, diagnostics, "workflow defaults.run.shell must be scalar");
                        continue;
                    }

                    if (isWorkingDirectory)
                    {
                        workingDirectoryNode = ParseString(ref reader, diagnostics, "workflow defaults.run.working-directory must be scalar");
                        continue;
                    }

                    AddError(diagnostics, $"unexpected workflow defaults.run key: {Encoding.UTF8.GetString(runKeyUtf8)}", runKeyMark);
                    reader.SkipCurrentNode();
                }

                if (reader.CurrentKind == YamlEventKind.MappingEnd)
                {
                    reader.Read();
                }

                continue;
            }

            AddError(diagnostics, $"unexpected workflow defaults key: {Encoding.UTF8.GetString(keyUtf8)}", keyMark);
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new Defaults
        {
            Run = new DefaultsRun
            {
                Shell = shellNode,
                WorkingDirectory = workingDirectoryNode,
                Range = default,
            },
            Range = default,
        };
    }

    private static Concurrency? ParseConcurrencyNode(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        string error,
        ExpressionValidationContext expressionContext)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var group = ParseStringAndValidateExpression(ref reader, diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
            return group is null
                ? null
                : new Concurrency
                {
                    Group = group,
                    Range = group.Range,
                };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        StringNode? groupNode = null;
        BoolNode? cancelInProgressNode = null;
        var keys = new HashSet<Utf8String>();
        reader.Read(); // consume mapping
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "concurrency"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var isGroup = keyUtf8.SequenceEqual("group"u8);
            var isCancelInProgress = keyUtf8.SequenceEqual("cancel-in-progress"u8);
            reader.Read();
            if (reader.End)
            {
                break;
            }

            if (isGroup)
            {
                groupNode = ParseStringAndValidateExpression(ref reader, diagnostics, expressionContext, "workflow concurrency.group must be scalar", parseWholeValueIfNoEmbedded: false);
                continue;
            }

            if (isCancelInProgress)
            {
                cancelInProgressNode = ParseBoolOrExpression(ref reader, diagnostics, expressionContext, "workflow concurrency.cancel-in-progress must be bool or expression");
                continue;
            }

            AddError(diagnostics, $"unexpected workflow concurrency key: {Encoding.UTF8.GetString(keyUtf8)}", keyMark);
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (groupNode is null)
        {
            return null;
        }

        return new Concurrency
        {
            Group = groupNode,
            CancelInProgress = cancelInProgressNode,
            Range = default,
        };
    }

    private static BoolNode? ParseBoolOrExpression(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ExpressionValidationContext context,
        string errorMessage)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        var range = BuildScalarLocation(mark, valueUtf8.Length);

        if (TryParseBool(valueUtf8, tag, out var value))
        {
            var boolNode = new BoolNode
            {
                Value = value,
                Range = range,
            };
            reader.Read();
            return boolNode;
        }

        var expressionNode = ParseStringAndValidateExpression(ref reader, diagnostics, context, errorMessage, parseWholeValueIfNoEmbedded: false);
        if (expressionNode is null)
        {
            return null;
        }

        return new BoolNode
        {
            Value = false,
            Expression = expressionNode,
            Range = range,
        };
    }

    private static Dictionary<Utf8String, Job> ParseJobsMapping(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
    {
        var jobs = new Dictionary<Utf8String, Job>();
        var seenJobIds = new HashSet<Utf8String>();
        // current is MappingStart
        reader.Read();

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "job id must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var jobIdMark = reader.CurrentStart;
            var jobId = reader.GetScalarSlice();
            var jobIdUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                jobIdUtf8,
                jobIdMark,
                diagnostics,
                seenJobIds,
                MappingKeyComparison.AsciiCaseInsensitive,
                "jobs"))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var jobIdNode = new StringNode
            {
                Value = jobId,
                Quoted = reader.IsScalarQuoted(),
                Range = BuildScalarLocation(jobIdMark, jobIdUtf8.Length),
            };
            var jobKey = Utf8String.FromLowerAscii(jobIdUtf8);
            reader.Read(); // consume job id

            if (reader.End)
            {
                break;
            }

            var job = ParseJobNode(ref reader, diagnostics, source, jobId, jobIdMark, jobIdNode);
            jobs[jobKey] = job;
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return jobs;
    }

    private static Job ParseJobNode(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        Utf8Slice jobId,
        TextPosition jobIdMark,
        StringNode jobIdNode)
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

        var hasUses = false;
        var hasWith = false;
        var hasSecrets = false;
        string? stepsOnlyKeyInReusable = null;
        TextPosition stepsOnlyKeyInReusableMark = default;
        var keys = new HashSet<Utf8String>();

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
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                $"job '{DecodeUtf8(source, jobId)}'"))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("runs-on"u8))
            {
                reader.Read(); // consume key
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
                if (!reader.End)
                {
                    nameNode = ParseString(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' name must be scalar");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("needs"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    needsNode = ParseStringOrStringSequence(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' needs must be scalar or sequence of scalar");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    envNode = ParseEnvNode(
                        ref reader,
                        diagnostics,
                        source,
                        $"job '{DecodeUtf8(source, jobId)}' env must be mapping",
                        ExpressionValidationContext.Job);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("steps"u8))
            {
                reader.Read(); // consume key
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
                reader.Read(); // consume key
                hasUses = true;
                if (!reader.End)
                {
                    var usesNode = ParseString(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' uses must be scalar");
                    workflowCallNode = new WorkflowCall
                    {
                        Uses = usesNode ?? new StringNode { Value = default, Quoted = false, Range = default },
                        Inputs = workflowCallNode?.Inputs,
                        Secrets = workflowCallNode?.Secrets,
                        InheritSecrets = workflowCallNode?.InheritSecrets ?? false,
                    };
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("if"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    ifNode = ParseExpression(
                        ref reader,
                        diagnostics,
                        ExpressionValidationContext.Job,
                        $"job '{DecodeUtf8(source, jobId)}' if must be scalar");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("permissions"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    permissionsNode = ParsePermissionsNode(ref reader, diagnostics, source, $"job '{DecodeUtf8(source, jobId)}' permissions must be scalar or mapping");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("environment"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    environmentNode = ParseEnvironmentNode(ref reader, diagnostics, source, jobId);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("concurrency"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    concurrencyNode = ParseConcurrencyNode(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' concurrency must be scalar or mapping", ExpressionValidationContext.Job);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("outputs"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    outputsNode = ParseOutputsNode(ref reader, diagnostics, source, jobId);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("defaults"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    defaultsNode = ParseDefaultsNode(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' defaults must be mapping");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("timeout-minutes"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    timeoutMinutesNode = ParseFloat(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' timeout-minutes must be number");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("continue-on-error"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    continueOnErrorNode = ParseBoolOrExpression(ref reader, diagnostics, ExpressionValidationContext.Job, $"job '{DecodeUtf8(source, jobId)}' continue-on-error must be bool or expression");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("strategy"u8))
            {
                reader.Read(); // consume key
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
                reader.Read(); // consume key
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
                reader.Read(); // consume key
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
                reader.Read(); // consume key
                hasWith = true;
                if (!reader.End)
                {
                    var inputs = ParseWorkflowCallInputsNode(ref reader, diagnostics, source, jobId);
                    if (workflowCallNode is not null)
                    {
                        workflowCallNode = new WorkflowCall
                        {
                            Uses = workflowCallNode.Uses,
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
                reader.Read(); // consume key
                hasSecrets = true;
                if (!reader.End)
                {
                    var secrets = ParseWorkflowCallSecretsNode(ref reader, diagnostics, source, jobId, out var inheritSecrets);
                    if (workflowCallNode is not null)
                    {
                        workflowCallNode = new WorkflowCall
                        {
                            Uses = workflowCallNode.Uses,
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
            var keyText = isKnownKey ? string.Empty : Encoding.UTF8.GetString(keyUtf8);
            reader.Read(); // consume key

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

            AddError(diagnostics, $"unexpected job key '{keyText}' in job '{DecodeUtf8(source, jobId)}'", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (hasUses && stepsOnlyKeyInReusable is not null)
        {
            AddError(
                diagnostics,
                $"when job '{DecodeUtf8(source, jobId)}' calls reusable workflow with uses, key '{stepsOnlyKeyInReusable}' is not allowed",
                stepsOnlyKeyInReusableMark);
        }

        if (!hasUses && hasWith)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' key 'with' requires uses", jobIdMark);
        }

        if (!hasUses && hasSecrets)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' key 'secrets' requires uses", jobIdMark);
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
            WorkflowCall = hasUses ? workflowCallNode : null,
            Range = jobIdNode.Range,
        };
    }

    private static Step[] ParseSteps(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        var steps = new List<Step>(8);
        reader.Read(); // consume SequenceStart

        var stepIndex = 0;
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            stepIndex++;
            var step = ParseStep(ref reader, diagnostics, source, jobId, stepIndex);
            if (step is not null)
            {
                steps.Add(step);
            }
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }

        return steps.ToArray();
    }

    private static Step? ParseStep(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, int stepIndex)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var hasRun = false;
        var hasUses = false;
        StringNode? idNode = null;
        StringNode? ifNode = null;
        StringNode? nameNode = null;
        Env? envNode = null;
        BoolNode? continueOnErrorNode = null;
        FloatNode? timeoutMinutesNode = null;
        StringNode? runNode = null;
        StringNode? usesNode = null;
        StringNode? shellNode = null;
        StringNode? workingDirectoryNode = null;
        Dictionary<Utf8String, StringNode>? withInputs = null;
        StringNode? dockerEntrypoint = null;
        StringNode? dockerArgs = null;

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("run"u8))
            {
                reader.Read();
                hasRun = true;
                if (!reader.End)
                {
                    runNode = ParseStringAndValidateExpression(
                        ref reader,
                        diagnostics,
                        ExpressionValidationContext.Step,
                        $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] run must be scalar",
                        parseWholeValueIfNoEmbedded: false);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("uses"u8))
            {
                reader.Read();
                hasUses = true;
                if (!reader.End)
                {
                    usesNode = ParseString(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] uses must be scalar");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("name"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    nameNode = ParseString(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] name must be scalar");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("id"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    idNode = ParseString(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] id must be scalar");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("if"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    ifNode = ParseExpression(
                        ref reader,
                        diagnostics,
                        ExpressionValidationContext.Step,
                        $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] if must be scalar");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("with"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    withInputs = ParseStepWithInputsNode(
                        ref reader,
                        diagnostics,
                        source,
                        jobId,
                        stepIndex,
                        out var entrypoint,
                        out var args);
                    dockerEntrypoint = entrypoint;
                    dockerArgs = args;
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("shell"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    shellNode = ParseString(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] shell must be scalar");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("working-directory"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    workingDirectoryNode = ParseStringAndValidateExpression(
                        ref reader,
                        diagnostics,
                        ExpressionValidationContext.Step,
                        $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] working-directory must be scalar",
                        parseWholeValueIfNoEmbedded: false);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("timeout-minutes"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    timeoutMinutesNode = ParseFloat(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] timeout-minutes must be number");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("continue-on-error"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    continueOnErrorNode = ParseBoolOrExpression(
                        ref reader,
                        diagnostics,
                        ExpressionValidationContext.Step,
                        $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] continue-on-error must be bool or expression");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    if (reader.CurrentKind != YamlEventKind.MappingStart)
                    {
                        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] env must be mapping", reader.CurrentStart);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        envNode = ParseEnvNode(
                            ref reader,
                            diagnostics,
                            source,
                            $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] env must be mapping",
                            ExpressionValidationContext.Step);
                    }
                }
                continue;
            }

            reader.Read();

            if (IsKnownStepKey(keyUtf8))
            {
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var key = Encoding.UTF8.GetString(keyUtf8);
            AddError(diagnostics, $"unexpected step key '{key}' in job '{DecodeUtf8(source, jobId)}' step[{stepIndex}]", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (hasRun && hasUses)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] cannot have both run and uses", reader.CurrentStart);
        }

        if (!hasRun && !hasUses)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] requires run or uses", reader.CurrentStart);
        }

        StepExec exec;
        if (hasRun)
        {
            exec = new ExecRun
            {
                Kind = StepExecKind.Run,
                Run = runNode ?? new StringNode { Value = default, Quoted = false, Range = default },
                Shell = shellNode,
                WorkingDirectory = workingDirectoryNode,
                Range = runNode?.Range ?? default,
            };
        }
        else
        {
            exec = new ExecAction
            {
                Kind = StepExecKind.Action,
                Uses = usesNode ?? new StringNode { Value = default, Quoted = false, Range = default },
                Inputs = withInputs,
                Entrypoint = dockerEntrypoint,
                Args = dockerArgs,
                Range = usesNode?.Range ?? default,
            };
        }

        return new Step
        {
            Id = idNode,
            If = ifNode,
            Name = nameNode,
            Exec = exec,
            Env = envNode,
            ContinueOnError = continueOnErrorNode,
            TimeoutMinutes = timeoutMinutesNode,
            Range = exec.Range,
        };
    }

    private static Dictionary<Utf8String, StringNode>? ParseStepWithInputsNode(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        Utf8Slice jobId,
        int stepIndex,
        out StringNode? entrypoint,
        out StringNode? args)
    {
        entrypoint = null;
        args = null;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] with must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, StringNode>();
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] with must be mapping", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            var key = Utf8String.FromLowerAscii(keyUtf8);
            var isEntrypoint = keyUtf8.SequenceEqual("entrypoint"u8);
            var isArgs = keyUtf8.SequenceEqual("args"u8);

            reader.Read();
            if (reader.End)
            {
                break;
            }

            var value = ParseStringAndValidateExpression(
                ref reader,
                diagnostics,
                ExpressionValidationContext.Step,
                $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] with.{Encoding.UTF8.GetString(keyUtf8)} must be scalar",
                parseWholeValueIfNoEmbedded: false);

            if (value is null)
            {
                continue;
            }

            map[key] = value;
            if (isEntrypoint)
            {
                entrypoint = value;
            }
            else if (isArgs)
            {
                args = value;
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
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

    internal static StringNode? ParseString(
        IYamlStreamReader reader,
        List<Diagnostic> diagnostics,
        string errorMessage,
        bool allowEmpty = false)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        if (!allowEmpty && valueUtf8.Length == 0)
        {
            AddError(diagnostics, errorMessage, mark);
        }

        var node = new StringNode
        {
            Value = slice,
            Quoted = reader.IsScalarQuoted(),
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };

        reader.Read();
        return node;
    }

    internal static BoolNode? ParseBool(IYamlStreamReader reader, List<Diagnostic> diagnostics, string errorMessage)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        if (!TryParseBool(valueUtf8, tag, out var value))
        {
            AddError(diagnostics, errorMessage, mark);
            reader.Read();
            return null;
        }

        var node = new BoolNode
        {
            Value = value,
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };
        reader.Read();
        return node;
    }

    internal static IntNode? ParseInt(IYamlStreamReader reader, List<Diagnostic> diagnostics, string errorMessage)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        if (!TryParseInt64(valueUtf8, tag, out var value))
        {
            AddError(diagnostics, errorMessage, mark);
            reader.Read();
            return null;
        }

        var node = new IntNode
        {
            Value = value,
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };
        reader.Read();
        return node;
    }

    internal static FloatNode? ParseFloat(IYamlStreamReader reader, List<Diagnostic> diagnostics, string errorMessage)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        if (!TryParseDouble(valueUtf8, tag, out var value))
        {
            AddError(diagnostics, errorMessage, mark);
            reader.Read();
            return null;
        }

        var node = new FloatNode
        {
            Value = value,
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };
        reader.Read();
        return node;
    }

    internal static StringNode? ParseExpression(
        IYamlStreamReader reader,
        List<Diagnostic> diagnostics,
        ExpressionValidationContext context,
        string errorMessage)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        ValidateExpressionText(
            valueUtf8,
            BuildScalarLocation(mark, valueUtf8.Length),
            context,
            diagnostics,
            parseWholeValueIfNoEmbedded: true);

        var node = new StringNode
        {
            Value = slice,
            Quoted = reader.IsScalarQuoted(),
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };

        reader.Read();
        return node;
    }

    internal static StringNode? MayParseExpression(
        IYamlStreamReader reader,
        List<Diagnostic> diagnostics,
        ExpressionValidationContext context)
    {
        if (reader.End || reader.CurrentKind != YamlEventKind.Scalar)
        {
            return null;
        }

        var mark = reader.CurrentStart;
        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        var hasExpression = valueUtf8.IndexOf("${{"u8) >= 0;
        var node = new StringNode
        {
            Value = slice,
            Quoted = reader.IsScalarQuoted(),
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };

        if (hasExpression)
        {
            ValidateExpressionText(
                valueUtf8,
                BuildScalarLocation(mark, valueUtf8.Length),
                context,
                diagnostics,
                parseWholeValueIfNoEmbedded: false);
        }

        reader.Read();
        return hasExpression ? node : null;
    }

    internal static StringNode[] ParseStringOrStringSequence(
        IYamlStreamReader reader,
        List<Diagnostic> diagnostics,
        string errorMessage,
        bool allowEmpty = false,
        bool allowElemEmpty = false)
    {
        if (reader.End)
        {
            return [];
        }

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var single = ParseString(reader, diagnostics, errorMessage, allowEmpty);
            return single is null ? [] : [single];
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return [];
        }

        var list = new List<StringNode>(4);
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            var node = ParseString(reader, diagnostics, errorMessage, allowElemEmpty);
            if (node is not null)
            {
                list.Add(node);
            }
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }

        return list.ToArray();
    }

    private static StringNode? ParseString(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, string errorMessage, bool allowEmpty = false)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        if (!allowEmpty && valueUtf8.Length == 0)
        {
            AddError(diagnostics, errorMessage, mark);
        }

        var node = new StringNode
        {
            Value = slice,
            Quoted = reader.IsScalarQuoted(),
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };

        reader.Read();
        return node;
    }

    private static StringNode? ParseExpression(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ExpressionValidationContext context,
        string errorMessage)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        ValidateExpressionText(
            valueUtf8,
            BuildScalarLocation(mark, valueUtf8.Length),
            context,
            diagnostics,
            parseWholeValueIfNoEmbedded: true);

        var node = new StringNode
        {
            Value = slice,
            Quoted = reader.IsScalarQuoted(),
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };

        reader.Read();
        return node;
    }

    private static StringNode? ParseStringAndValidateExpression(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ExpressionValidationContext context,
        string errorMessage,
        bool parseWholeValueIfNoEmbedded)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        var range = BuildScalarLocation(mark, valueUtf8.Length);
        ValidateExpressionText(
            valueUtf8,
            range,
            context,
            diagnostics,
            parseWholeValueIfNoEmbedded);

        var node = new StringNode
        {
            Value = slice,
            Quoted = reader.IsScalarQuoted(),
            Range = range,
        };

        reader.Read();
        return node;
    }

    private static StringNode[] ParseStringOrStringSequence(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        string errorMessage,
        bool allowEmpty = false,
        bool allowElemEmpty = false)
    {
        if (reader.End)
        {
            return [];
        }

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var single = ParseString(ref reader, diagnostics, errorMessage, allowEmpty);
            return single is null ? [] : [single];
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return [];
        }

        var list = new List<StringNode>(4);
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            var node = ParseString(ref reader, diagnostics, errorMessage, allowElemEmpty);
            if (node is not null)
            {
                list.Add(node);
            }
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }

        return list.ToArray();
    }

    private static FloatNode? ParseFloat(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, string errorMessage)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        if (!TryParseDouble(valueUtf8, tag, out var value))
        {
            AddError(diagnostics, errorMessage, mark);
            reader.Read();
            return null;
        }

        var node = new FloatNode
        {
            Value = value,
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };
        reader.Read();
        return node;
    }

    private static IntNode? ParseInt(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, string errorMessage)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        if (!TryParseInt64(valueUtf8, tag, out var value))
        {
            AddError(diagnostics, errorMessage, mark);
            reader.Read();
            return null;
        }

        var node = new IntNode
        {
            Value = value,
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };
        reader.Read();
        return node;
    }

    private static bool TryParseBool(ReadOnlySpan<byte> valueUtf8, ScalarTag tag, out bool value)
    {
        if (tag == ScalarTag.Bool)
        {
            if (valueUtf8.SequenceEqual("true"u8))
            {
                value = true;
                return true;
            }

            if (valueUtf8.SequenceEqual("false"u8))
            {
                value = false;
                return true;
            }
        }

        value = false;
        return false;
    }

    private static bool TryParseInt64(ReadOnlySpan<byte> valueUtf8, ScalarTag tag, out long value)
    {
        if (tag is ScalarTag.Int or ScalarTag.Unknown)
        {
            if (Utf8Parser.TryParse(valueUtf8, out value, out var consumed) && consumed == valueUtf8.Length)
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryParseDouble(ReadOnlySpan<byte> valueUtf8, ScalarTag tag, out double value)
    {
        if (tag is ScalarTag.Float or ScalarTag.Int or ScalarTag.Unknown)
        {
            if (Utf8Parser.TryParse(valueUtf8, out value, out var consumed) && consumed == valueUtf8.Length)
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static Event[] ParseOnEvents(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var eventMark = reader.CurrentStart;
            var eventInfo = ReadOnEventInfo(ref reader); // try-catch inside for non-UTF8 scalars
            ValidateKnownOnEvent(in eventInfo, eventMark, diagnostics);
            Utf8Slice eventSlice;
            int eventByteLen;
            try { var u = reader.GetScalarUtf8(); eventSlice = reader.GetScalarSlice(); eventByteLen = u.Length; }
            catch { eventSlice = default; eventByteLen = 0; }
            var nameNode = new StringNode { Value = eventSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(eventMark, eventByteLen) };
            reader.Read();
            return [BuildSimpleEvent(in eventInfo, nameNode)];
        }

        if (reader.CurrentKind == YamlEventKind.SequenceStart)
        {
            reader.Read(); // consume SequenceStart
            var events = new List<Event>(4);
            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, "on sequence item must be scalar event name", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    continue;
                }

                var eventMark = reader.CurrentStart;
                var eventInfo = ReadOnEventInfo(ref reader);
                ValidateKnownOnEvent(in eventInfo, eventMark, diagnostics);
                Utf8Slice eventSlice;
                int eventByteLen;
                try { var u = reader.GetScalarUtf8(); eventSlice = reader.GetScalarSlice(); eventByteLen = u.Length; }
                catch { eventSlice = default; eventByteLen = 0; }
                var nameNode = new StringNode { Value = eventSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(eventMark, eventByteLen) };
                reader.Read();
                events.Add(BuildSimpleEvent(in eventInfo, nameNode));
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd) { reader.Read(); }
            return events.ToArray();
        }

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            reader.Read(); // consume MappingStart
            var events = new List<Event>(4);
            var keys = new HashSet<Utf8String>();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, "on mapping key must be scalar event name", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd) { reader.SkipCurrentNode(); }
                    continue;
                }

                var eventMark = reader.CurrentStart;
                var eventKeyUtf8 = reader.GetScalarUtf8();
                if (!TryRegisterMappingKey(
                    eventKeyUtf8,
                    eventMark,
                    diagnostics,
                    keys,
                    MappingKeyComparison.AsciiCaseInsensitive,
                    "on"))
                {
                    reader.Read();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var eventInfo = ReadOnEventInfo(ref reader);
                ValidateKnownOnEvent(in eventInfo, eventMark, diagnostics);
                Utf8Slice eventSlice;
                int eventByteLen;
                try { var u = reader.GetScalarUtf8(); eventSlice = reader.GetScalarSlice(); eventByteLen = u.Length; }
                catch { eventSlice = default; eventByteLen = 0; }
                var nameNode = new StringNode { Value = eventSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(eventMark, eventByteLen) };
                reader.Read(); // consume event key

                if (reader.End)
                {
                    events.Add(BuildSimpleEvent(in eventInfo, nameNode));
                    break;
                }

                if (IsSpecialOnEvent(in eventInfo))
                {
                    events.Add(ParseOnEventWithOptions(ref reader, diagnostics, in eventInfo, eventMark, nameNode));
                    continue;
                }

                if (reader.CurrentKind == YamlEventKind.MappingStart)
                {
                    events.Add(ParseOnEventWithOptions(ref reader, diagnostics, in eventInfo, eventMark, nameNode));
                    continue;
                }

                if (reader.CurrentKind is YamlEventKind.Scalar or YamlEventKind.SequenceStart)
                {
                    // Some events have null-like / scalar options value; accept and build stub
                    reader.SkipCurrentNode();
                    events.Add(BuildSimpleEvent(in eventInfo, nameNode));
                    continue;
                }

                AddError(diagnostics, $"on.{eventInfo.Name} must be scalar, sequence, or mapping", reader.CurrentStart);
                reader.SkipCurrentNode();
                events.Add(BuildSimpleEvent(in eventInfo, nameNode));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd) { reader.Read(); }
            return events.ToArray();
        }

        AddError(diagnostics, "on must be scalar, sequence, or mapping", reader.CurrentStart);
        reader.SkipCurrentNode();
        return [];
    }

    private static Event BuildSimpleEvent(in OnEventInfo eventInfo, StringNode nameNode)
    {
        if (eventInfo.IsKnown)
        {
            return eventInfo.Spec.Id switch
            {
                WebhookTypes.EventId.Schedule => new ScheduledEvent { EventName = nameNode, Range = nameNode.Range },
                WebhookTypes.EventId.WorkflowDispatch => new WorkflowDispatchEvent { EventName = nameNode, Range = nameNode.Range },
                WebhookTypes.EventId.WorkflowCall => new WorkflowCallEvent { EventName = nameNode, Range = nameNode.Range },
                WebhookTypes.EventId.RepositoryDispatch => new RepositoryDispatchEvent { EventName = nameNode, Range = nameNode.Range },
                _ => new WebhookEvent { EventName = nameNode, Hook = nameNode, Range = nameNode.Range },
            };
        }

        return new WebhookEvent { EventName = nameNode, Hook = nameNode, Range = nameNode.Range };
    }

    private static Event ParseOnEventWithOptions(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        in OnEventInfo eventInfo,
        TextPosition eventMark,
        StringNode nameNode)
    {
        if (!IsSpecialOnEvent(in eventInfo))
        {
            // Webhook event: build full AST with filters
            return ParseWebhookEventWithOptions(ref reader, diagnostics, in eventInfo, eventMark, nameNode);
        }

        return eventInfo.Spec.Id switch
        {
            WebhookTypes.EventId.Schedule => ParseScheduleEvent(ref reader, diagnostics, nameNode),
            WebhookTypes.EventId.WorkflowDispatch => ParseWorkflowDispatchEvent(ref reader, diagnostics, nameNode),
            WebhookTypes.EventId.WorkflowCall => ParseWorkflowCallEvent(ref reader, diagnostics, nameNode),
            WebhookTypes.EventId.RepositoryDispatch => ParseRepositoryDispatchEvent(ref reader, diagnostics, in eventInfo, nameNode),
            _ => BuildSimpleEvent(in eventInfo, nameNode),
        };
    }

    private static bool IsSpecialOnEvent(in OnEventInfo eventInfo)
    {
        return eventInfo.IsKnown
            && (eventInfo.Spec.Id == WebhookTypes.EventId.Schedule
                || eventInfo.Spec.Id == WebhookTypes.EventId.WorkflowDispatch
                || eventInfo.Spec.Id == WebhookTypes.EventId.WorkflowCall
                || eventInfo.Spec.Id == WebhookTypes.EventId.RepositoryDispatch);
    }

    private static ScheduledEvent ParseScheduleEvent(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        StringNode nameNode)
    {
        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, "on.schedule must be sequence", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new ScheduledEvent { EventName = nameNode, Schedules = [], Range = nameNode.Range };
        }

        var schedules = new List<ScheduleEntry>(2);
        reader.Read(); // consume SequenceStart

        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.MappingStart)
            {
                AddError(diagnostics, "on.schedule item must be mapping", reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            schedules.Add(ParseScheduleEntry(ref reader, diagnostics));
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }

        return new ScheduledEvent { EventName = nameNode, Schedules = schedules.ToArray(), Range = nameNode.Range };
    }

    private static ScheduleEntry ParseScheduleEntry(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics)
    {
        TextRange range = default;
        StringNode? cron = null;
        StringNode? timezone = null;
        var keys = new HashSet<Utf8String>();

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.schedule item key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.schedule"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("cron"u8))
            {
                reader.Read();
                cron = ParseString(ref reader, diagnostics, "on.schedule.cron must be scalar");
                if (cron is not null)
                {
                    range = cron.Range;
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("timezone"u8))
            {
                reader.Read();
                timezone = ParseString(ref reader, diagnostics, "on.schedule.timezone must be scalar");
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.schedule option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (cron is null)
        {
            AddError(diagnostics, "on.schedule item requires cron", reader.CurrentStart);
        }

        return new ScheduleEntry
        {
            Cron = cron,
            Timezone = timezone,
            Range = range,
        };
    }

    private static WorkflowDispatchEvent ParseWorkflowDispatchEvent(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        StringNode nameNode)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_dispatch must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowDispatchEvent { EventName = nameNode, Inputs = null, Range = nameNode.Range };
        }

        Dictionary<Utf8String, DispatchInput>? inputs = null;
        var keys = new HashSet<Utf8String>();
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_dispatch option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.workflow_dispatch"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("inputs"u8))
            {
                reader.Read();
                inputs = ParseWorkflowDispatchInputs(ref reader, diagnostics);
                continue;
            }

            var key = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"on.workflow_dispatch does not support option: {key}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new WorkflowDispatchEvent { EventName = nameNode, Inputs = inputs, Range = nameNode.Range };
    }

    private static Dictionary<Utf8String, DispatchInput>? ParseWorkflowDispatchInputs(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_dispatch.inputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, DispatchInput>();
        var keys = new HashSet<Utf8String>();
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_dispatch.inputs key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var idMark = reader.CurrentStart;
            var idSlice = reader.GetScalarSlice();
            var idUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                idUtf8,
                idMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.workflow_dispatch.inputs"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var idRange = BuildScalarLocation(idMark, idUtf8.Length);
            var key = Utf8String.FromLowerAscii(idUtf8);
            var nameNode = new StringNode { Value = idSlice, Quoted = reader.IsScalarQuoted(), Range = idRange };
            reader.Read(); // consume input id

            var input = ParseWorkflowDispatchInput(ref reader, diagnostics, nameNode);
            map[key] = input;
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
    }

    private static DispatchInput ParseWorkflowDispatchInput(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        StringNode nameNode)
    {
        StringNode? description = null;
        BoolNode? required = null;
        StringNode? defaultValue = null;
        DispatchInputType type = DispatchInputType.None;
        StringNode[]? options = null;
        var keys = new HashSet<Utf8String>();

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_dispatch input must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new DispatchInput { Name = nameNode, Description = description, Required = required, Default = defaultValue, Type = type, Options = options, Range = nameNode.Range };
        }

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_dispatch input option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.workflow_dispatch input"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("description"u8))
            {
                reader.Read();
                description = ParseString(ref reader, diagnostics, "on.workflow_dispatch input description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("required"u8))
            {
                reader.Read();
                required = ParseBoolNode(ref reader, diagnostics, "on.workflow_dispatch input required must be bool");
                continue;
            }

            if (keyUtf8.SequenceEqual("default"u8))
            {
                reader.Read();
                defaultValue = ParseString(ref reader, diagnostics, "on.workflow_dispatch input default must be scalar", allowEmpty: true);
                continue;
            }

            if (keyUtf8.SequenceEqual("type"u8))
            {
                reader.Read();
                type = ParseDispatchInputType(ref reader, diagnostics);
                continue;
            }

            if (keyUtf8.SequenceEqual("options"u8))
            {
                reader.Read();
                options = ParseStringOrStringSequence(ref reader, diagnostics, "on.workflow_dispatch input options must be scalar or sequence of scalar");
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.workflow_dispatch input option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new DispatchInput
        {
            Name = nameNode,
            Description = description,
            Required = required,
            Default = defaultValue,
            Type = type,
            Options = options,
            Range = nameNode.Range,
        };
    }

    private static DispatchInputType ParseDispatchInputType(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics)
    {
        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, "on.workflow_dispatch input type must be scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return DispatchInputType.None;
        }

        var valueUtf8 = reader.GetScalarUtf8();
        var type = valueUtf8.SequenceEqual("string"u8) ? DispatchInputType.String
            : valueUtf8.SequenceEqual("number"u8) ? DispatchInputType.Number
            : valueUtf8.SequenceEqual("boolean"u8) ? DispatchInputType.Boolean
            : valueUtf8.SequenceEqual("choice"u8) ? DispatchInputType.Choice
            : valueUtf8.SequenceEqual("environment"u8) ? DispatchInputType.Environment
            : DispatchInputType.None;

        if (type == DispatchInputType.None)
        {
            AddError(diagnostics, "on.workflow_dispatch input type must be one of string, number, boolean, choice, environment", reader.CurrentStart);
        }

        reader.Read();
        return type;
    }

    private static BoolNode? ParseBoolNode(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, string errorMessage)
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        if (!TryParseBool(valueUtf8, tag, out var value))
        {
            AddError(diagnostics, errorMessage, mark);
            reader.Read();
            return null;
        }

        var node = new BoolNode
        {
            Value = value,
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };
        reader.Read();
        return node;
    }

    private static WorkflowCallEvent ParseWorkflowCallEvent(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        StringNode nameNode)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEvent { EventName = nameNode, Inputs = null, Secrets = null, Outputs = null, Range = nameNode.Range };
        }

        WorkflowCallEventInput[]? inputs = null;
        Dictionary<Utf8String, WorkflowCallEventSecret>? secrets = null;
        Dictionary<Utf8String, WorkflowCallEventOutput>? outputs = null;
        var keys = new HashSet<Utf8String>();

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_call option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.workflow_call"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("inputs"u8))
            {
                reader.Read();
                inputs = ParseWorkflowCallInputs(ref reader, diagnostics);
                continue;
            }

            if (keyUtf8.SequenceEqual("secrets"u8))
            {
                reader.Read();
                secrets = ParseWorkflowCallSecrets(ref reader, diagnostics);
                continue;
            }

            if (keyUtf8.SequenceEqual("outputs"u8))
            {
                reader.Read();
                outputs = ParseWorkflowCallOutputs(ref reader, diagnostics);
                continue;
            }

            var key = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"on.workflow_call does not support option: {key}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new WorkflowCallEvent
        {
            EventName = nameNode,
            Inputs = inputs,
            Secrets = secrets,
            Outputs = outputs,
            Range = nameNode.Range,
        };
    }

    private static WorkflowCallEventInput[]? ParseWorkflowCallInputs(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call.inputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var list = new List<WorkflowCallEventInput>(4);
        var keys = new HashSet<Utf8String>();
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_call.inputs key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var idMark = reader.CurrentStart;
            var idSlice = reader.GetScalarSlice();
            var idUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                idUtf8,
                idMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.workflow_call.inputs"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var id = Utf8String.FromLowerAscii(idUtf8);
            var nameNode = new StringNode { Value = idSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(idMark, idUtf8.Length) };
            var idText = Encoding.UTF8.GetString(idUtf8);
            reader.Read();

            list.Add(ParseWorkflowCallInput(ref reader, diagnostics, nameNode, id, idText));
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return list.ToArray();
    }

    private static WorkflowCallEventInput ParseWorkflowCallInput(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        StringNode nameNode,
        Utf8String id,
        string idText)
    {
        StringNode? description = null;
        BoolNode? required = null;
        StringNode? defaultValue = null;
        var type = WorkflowCallInputType.Invalid;
        var hasType = false;
        var keys = new HashSet<Utf8String>();

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call input must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEventInput { Name = nameNode, Id = id, Description = description, Required = required, Default = defaultValue, Type = type, Range = nameNode.Range };
        }

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_call input option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.workflow_call input"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("description"u8))
            {
                reader.Read();
                description = ParseString(ref reader, diagnostics, "on.workflow_call input description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("required"u8))
            {
                reader.Read();
                required = ParseBoolNode(ref reader, diagnostics, "on.workflow_call input required must be bool");
                continue;
            }

            if (keyUtf8.SequenceEqual("default"u8))
            {
                reader.Read();
                defaultValue = ParseString(ref reader, diagnostics, "on.workflow_call input default must be scalar", allowEmpty: true);
                continue;
            }

            if (keyUtf8.SequenceEqual("type"u8))
            {
                reader.Read();
                type = ParseWorkflowCallInputType(ref reader, diagnostics);
                hasType = true;
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.workflow_call input option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (!hasType)
        {
            AddError(
                diagnostics,
                $"on.workflow_call.inputs.{idText}.type is required",
                new TextPosition(nameNode.Range.Start, nameNode.Range.StartLine, nameNode.Range.StartColumn));
        }

        return new WorkflowCallEventInput
        {
            Name = nameNode,
            Id = id,
            Description = description,
            Required = required,
            Default = defaultValue,
            Type = type,
            Range = nameNode.Range,
        };
    }

    private static WorkflowCallInputType ParseWorkflowCallInputType(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics)
    {
        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, "on.workflow_call input type must be scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return WorkflowCallInputType.Invalid;
        }

        var valueUtf8 = reader.GetScalarUtf8();
        var type = valueUtf8.SequenceEqual("boolean"u8) ? WorkflowCallInputType.Boolean
            : valueUtf8.SequenceEqual("number"u8) ? WorkflowCallInputType.Number
            : valueUtf8.SequenceEqual("string"u8) ? WorkflowCallInputType.String
            : WorkflowCallInputType.Invalid;
        if (type == WorkflowCallInputType.Invalid)
        {
            AddError(diagnostics, "on.workflow_call input type must be one of boolean, number, string", reader.CurrentStart);
        }

        reader.Read();
        return type;
    }

    private static Dictionary<Utf8String, WorkflowCallEventSecret>? ParseWorkflowCallSecrets(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call.secrets must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, WorkflowCallEventSecret>();
        var keys = new HashSet<Utf8String>();
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_call.secrets key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var idMark = reader.CurrentStart;
            var idSlice = reader.GetScalarSlice();
            var idUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                idUtf8,
                idMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.workflow_call.secrets"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var key = Utf8String.FromLowerAscii(idUtf8);
            var nameNode = new StringNode { Value = idSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(idMark, idUtf8.Length) };
            reader.Read();

            map[key] = ParseWorkflowCallSecret(ref reader, diagnostics, nameNode);
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
    }

    private static WorkflowCallEventSecret ParseWorkflowCallSecret(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        StringNode nameNode)
    {
        StringNode? description = null;
        BoolNode? required = null;
        var keys = new HashSet<Utf8String>();

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call secret must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEventSecret { Name = nameNode, Description = description, Required = required, Range = nameNode.Range };
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_call secret option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.workflow_call secret"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("description"u8))
            {
                reader.Read();
                description = ParseString(ref reader, diagnostics, "on.workflow_call secret description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("required"u8))
            {
                reader.Read();
                required = ParseBoolNode(ref reader, diagnostics, "on.workflow_call secret required must be bool");
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.workflow_call secret option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new WorkflowCallEventSecret
        {
            Name = nameNode,
            Description = description,
            Required = required,
            Range = nameNode.Range,
        };
    }

    private static Dictionary<Utf8String, WorkflowCallEventOutput>? ParseWorkflowCallOutputs(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call.outputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, WorkflowCallEventOutput>();
        var keys = new HashSet<Utf8String>();
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_call.outputs key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var idMark = reader.CurrentStart;
            var idSlice = reader.GetScalarSlice();
            var idUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                idUtf8,
                idMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.workflow_call.outputs"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var key = Utf8String.FromLowerAscii(idUtf8);
            var nameNode = new StringNode { Value = idSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(idMark, idUtf8.Length) };
            var idText = Encoding.UTF8.GetString(idUtf8);
            reader.Read();

            map[key] = ParseWorkflowCallOutput(ref reader, diagnostics, nameNode, idText);
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
    }

    private static WorkflowCallEventOutput ParseWorkflowCallOutput(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        StringNode nameNode,
        string idText)
    {
        StringNode? description = null;
        StringNode? value = null;
        var keys = new HashSet<Utf8String>();

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call output must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEventOutput { Name = nameNode, Description = description, Value = value, Range = nameNode.Range };
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_call output option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.workflow_call output"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("description"u8))
            {
                reader.Read();
                description = ParseString(ref reader, diagnostics, "on.workflow_call output description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("value"u8))
            {
                reader.Read();
                value = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Workflow,
                    "on.workflow_call output value must be scalar",
                    parseWholeValueIfNoEmbedded: false);
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.workflow_call output option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (value is null)
        {
            AddError(
                diagnostics,
                $"on.workflow_call.outputs.{idText}.value is required",
                new TextPosition(nameNode.Range.Start, nameNode.Range.StartLine, nameNode.Range.StartColumn));
        }

        return new WorkflowCallEventOutput
        {
            Name = nameNode,
            Description = description,
            Value = value,
            Range = nameNode.Range,
        };
    }

    private static RepositoryDispatchEvent ParseRepositoryDispatchEvent(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        in OnEventInfo eventInfo,
        StringNode nameNode)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.repository_dispatch must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new RepositoryDispatchEvent { EventName = nameNode, Types = null, Range = nameNode.Range };
        }

        StringNode[]? types = null;
        var keys = new HashSet<Utf8String>();
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.repository_dispatch option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "on.repository_dispatch"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("types"u8))
            {
                reader.Read();
                types = ParseOnTypesNodes(ref reader, diagnostics, in eventInfo);
                continue;
            }

            var key = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"on.repository_dispatch does not support option: {key}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new RepositoryDispatchEvent { EventName = nameNode, Types = types, Range = nameNode.Range };
    }

    private static WebhookEvent ParseWebhookEventWithOptions(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        in OnEventInfo eventInfo,
        TextPosition eventMark,
        StringNode nameNode)
    {
        var hasBranches = false;
        var hasBranchesIgnore = false;
        var hasTags = false;
        var hasTagsIgnore = false;
        var hasPaths = false;
        var hasPathsIgnore = false;

        StringNode[]? types = null;
        WebhookEventFilter? branches = null;
        WebhookEventFilter? branchesIgnore = null;
        WebhookEventFilter? tags = null;
        WebhookEventFilter? tagsIgnore = null;
        WebhookEventFilter? paths = null;
        WebhookEventFilter? pathsIgnore = null;
        StringNode[]? workflows = null;
        var keys = new HashSet<Utf8String>();

        reader.Read(); // consume MappingStart

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"on.{eventInfo.Name} option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd) { reader.SkipCurrentNode(); }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keySlice = reader.GetScalarSlice();
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                $"on.{eventInfo.Name}"))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            // Pre-compute key identity before advancing reader (spans may be invalidated after Read)
            var isTypes = keyUtf8.SequenceEqual("types"u8);
            var isBranches = keyUtf8.SequenceEqual("branches"u8);
            var isBranchesIgnore = keyUtf8.SequenceEqual("branches-ignore"u8);
            var isTags = keyUtf8.SequenceEqual("tags"u8);
            var isTagsIgnore = keyUtf8.SequenceEqual("tags-ignore"u8);
            var isPaths = keyUtf8.SequenceEqual("paths"u8);
            var isPathsIgnore = keyUtf8.SequenceEqual("paths-ignore"u8);
            var isWorkflows = keyUtf8.SequenceEqual("workflows"u8);
            var isOptionNotAllowed = eventInfo.IsKnown && !eventInfo.Spec.IsOptionAllowed(keyUtf8);

            // Decode unknown key string while span is still valid (diagnostic path only)
            string? unknownKeyText = (!isTypes && !isBranches && !isBranchesIgnore && !isTags && !isTagsIgnore
                && !isPaths && !isPathsIgnore && !isWorkflows)
                ? Encoding.UTF8.GetString(keyUtf8)
                : null;

            reader.Read(); // consume key - after this keyUtf8 may be invalid

            if (reader.End) { break; }

            if (isTypes)
            {
                if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeOptionSupported())
                {
                    AddError(diagnostics, $"on.{eventInfo.Name}.types is not supported", keyMark);
                    reader.SkipCurrentNode();
                    continue;
                }

                types = ParseOnTypesNodes(ref reader, diagnostics, in eventInfo);
                continue;
            }

            if (isOptionNotAllowed)
            {
                var key = unknownKeyText ?? string.Empty;
                AddError(diagnostics, $"on.{eventInfo.Name} does not support option: {key}", keyMark);
                if (!reader.End) { reader.SkipCurrentNode(); }
                continue;
            }

            if (isBranches)
            {
                hasBranches = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "branches"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.branches must be scalar or sequence of scalar");
                branches = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isBranchesIgnore)
            {
                hasBranchesIgnore = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "branches-ignore"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.branches-ignore must be scalar or sequence of scalar");
                branchesIgnore = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isTags)
            {
                hasTags = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "tags"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.tags must be scalar or sequence of scalar");
                tags = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isTagsIgnore)
            {
                hasTagsIgnore = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "tags-ignore"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.tags-ignore must be scalar or sequence of scalar");
                tagsIgnore = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isPaths)
            {
                hasPaths = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "paths"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.paths must be scalar or sequence of scalar");
                paths = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isPathsIgnore)
            {
                hasPathsIgnore = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "paths-ignore"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.paths-ignore must be scalar or sequence of scalar");
                pathsIgnore = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isWorkflows)
            {
                workflows = ParseStringOrStringSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.workflows must be scalar or sequence of scalar");
                continue;
            }

            AddError(diagnostics, $"unexpected on.{eventInfo.Name} option: {unknownKeyText}", keyMark);
            if (!reader.End) { reader.SkipCurrentNode(); }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd) { reader.Read(); }

        if (hasBranches && hasBranchesIgnore)
        {
            AddError(diagnostics, $"on.{eventInfo.Name} cannot use both branches and branches-ignore", eventMark);
        }

        if (hasTags && hasTagsIgnore)
        {
            AddError(diagnostics, $"on.{eventInfo.Name} cannot use both tags and tags-ignore", eventMark);
        }

        if (hasPaths && hasPathsIgnore)
        {
            AddError(diagnostics, $"on.{eventInfo.Name} cannot use both paths and paths-ignore", eventMark);
        }

        return new WebhookEvent
        {
            EventName = nameNode,
            Hook = nameNode,
            Types = types,
            Branches = branches,
            BranchesIgnore = branchesIgnore,
            Tags = tags,
            TagsIgnore = tagsIgnore,
            Paths = paths,
            PathsIgnore = pathsIgnore,
            Workflows = workflows,
            Range = nameNode.Range,
        };
    }

    private static StringNode[] ParseOnTypesNodes(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, in OnEventInfo eventInfo)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var mark = reader.CurrentStart;
            var slice = reader.GetScalarSlice();
            var valueUtf8 = reader.GetScalarUtf8();
            if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeAllowed(valueUtf8))
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", mark);
            }

            var node = new StringNode { Value = slice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(mark, valueUtf8.Length) };
            reader.Read();
            return [node];
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, $"on.{eventInfo.Name}.types must be scalar or sequence of scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return [];
        }

        reader.Read();
        var list = new List<StringNode>(4);
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types must be scalar or sequence of scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            var mark = reader.CurrentStart;
            var slice = reader.GetScalarSlice();
            var valueUtf8 = reader.GetScalarUtf8();
            if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeAllowed(valueUtf8))
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", mark);
            }

            list.Add(new StringNode { Value = slice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(mark, valueUtf8.Length) });
            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd) { reader.Read(); }
        return list.ToArray();
    }

    private static void ParseOnEventOptions(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, in OnEventInfo eventInfo, TextPosition eventMark)
    {
        var hasBranches = false;
        var hasBranchesIgnore = false;
        var hasTags = false;
        var hasTagsIgnore = false;
        var hasPaths = false;
        var hasPathsIgnore = false;

        reader.Read(); // consume MappingStart

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"on.{eventInfo.Name} option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("types"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeOptionSupported())
                {
                    AddError(diagnostics, $"on.{eventInfo.Name}.types is not supported", keyMark);
                    reader.SkipCurrentNode();
                    continue;
                }

                ParseOnTypes(ref reader, diagnostics, in eventInfo);
                continue;
            }

            if (eventInfo.IsKnown && !eventInfo.Spec.IsOptionAllowed(keyUtf8))
            {
                var key = Encoding.UTF8.GetString(keyUtf8);
                reader.Read();
                AddError(diagnostics, $"on.{eventInfo.Name} does not support option: {key}", keyMark);
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("branches"u8))
            {
                reader.Read();
                hasBranches = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.branches must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("branches-ignore"u8))
            {
                reader.Read();
                hasBranchesIgnore = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.branches-ignore must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("tags"u8))
            {
                reader.Read();
                hasTags = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.tags must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("tags-ignore"u8))
            {
                reader.Read();
                hasTagsIgnore = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.tags-ignore must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("paths"u8))
            {
                reader.Read();
                hasPaths = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.paths must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("paths-ignore"u8))
            {
                reader.Read();
                hasPathsIgnore = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.paths-ignore must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("workflows"u8))
            {
                reader.Read();
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.workflows must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("inputs"u8) || keyUtf8.SequenceEqual("secrets"u8) || keyUtf8.SequenceEqual("outputs"u8))
            {
                var key = keyUtf8.SequenceEqual("inputs"u8)
                    ? "inputs"
                    : keyUtf8.SequenceEqual("secrets"u8)
                        ? "secrets"
                        : "outputs";
                reader.Read();
                if (reader.CurrentKind != YamlEventKind.MappingStart)
                {
                    AddError(diagnostics, $"on.{eventInfo.Name}.{key} must be mapping", reader.CurrentStart);
                }
                reader.SkipCurrentNode();
                continue;
            }

            if (reader.End)
            {
                break;
            }

            var unknownKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.{eventInfo.Name} option: {unknownKey}", keyMark);
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (hasBranches && hasBranchesIgnore)
        {
            AddError(diagnostics, $"on.{eventInfo.Name} cannot use both branches and branches-ignore", eventMark);
        }

        if (hasTags && hasTagsIgnore)
        {
            AddError(diagnostics, $"on.{eventInfo.Name} cannot use both tags and tags-ignore", eventMark);
        }

        if (hasPaths && hasPathsIgnore)
        {
            AddError(diagnostics, $"on.{eventInfo.Name} cannot use both paths and paths-ignore", eventMark);
        }
    }

    private static void ParseScalarOrScalarSequence(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        string error,
        Utf8ScalarValidator? scalarValidator = null)
    {
        if (scalarValidator is null)
        {
            _ = ParseStringOrStringSequence(ref reader, diagnostics, error);
            return;
        }

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var validationError = scalarValidator(reader.GetScalarUtf8());
            if (validationError is not null)
            {
                AddError(diagnostics, validationError, reader.CurrentStart);
            }

            reader.Read();
            return;
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            var validationError = scalarValidator(reader.GetScalarUtf8());
            if (validationError is not null)
            {
                AddError(diagnostics, validationError, reader.CurrentStart);
            }

            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }
    }

    private static Strategy ParseStrategy(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        Matrix? matrix = null;
        BoolNode? failFast = null;
        IntNode? maxParallel = null;
        var keys = new HashSet<Utf8String>();

        reader.Read(); // consume MappingStart

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "strategy"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("matrix"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                matrix = ParseMatrix(ref reader, diagnostics, source, jobId);
                continue;
            }

            if (keyUtf8.SequenceEqual("fail-fast"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    failFast = ParseBoolOrExpression(ref reader, diagnostics, ExpressionValidationContext.Job, $"job '{DecodeUtf8(source, jobId)}' strategy.fail-fast must be bool or expression");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("max-parallel"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    maxParallel = ParseInt(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.max-parallel must be integer");
                }
                continue;
            }

            var key = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected strategy key '{key}' in job '{DecodeUtf8(source, jobId)}'", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new Strategy
        {
            Matrix = matrix,
            FailFast = failFast,
            MaxParallel = maxParallel,
            Range = default,
        };
    }

    private static Matrix? ParseMatrix(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var expression = ParseStringAndValidateExpression(
                ref reader,
                diagnostics,
                ExpressionValidationContext.Job,
                $"job '{DecodeUtf8(source, jobId)}' strategy.matrix must be scalar or mapping",
                parseWholeValueIfNoEmbedded: false);
            return new Matrix { Expression = expression, Range = expression?.Range ?? default };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix must be scalar or mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        MatrixCombinations[]? include = null;
        MatrixCombinations[]? exclude = null;
        Dictionary<Utf8String, MatrixRow>? rows = null;
        var keys = new HashSet<Utf8String>();

        reader.Read(); // consume matrix mapping
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyUtf8 = reader.GetScalarUtf8();
            var keySlice = reader.GetScalarSlice();
            var keyMark = reader.CurrentStart;
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "strategy.matrix"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var isInclude = keyUtf8.SequenceEqual("include"u8);
            var isExclude = keyUtf8.SequenceEqual("exclude"u8);
            reader.Read();
            if (reader.End)
            {
                break;
            }

            if (isInclude || isExclude)
            {
                if (reader.CurrentKind is not YamlEventKind.SequenceStart and not YamlEventKind.Scalar)
                {
                    var keyTextForDiagnostic = isInclude ? "include" : "exclude";
                    AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{keyTextForDiagnostic} must be sequence or scalar", reader.CurrentStart);
                }
                var combos = ParseMatrixCombinations(ref reader, diagnostics, source, jobId, isInclude ? "include" : "exclude");
                if (isInclude)
                {
                    include = combos;
                }
                else
                {
                    exclude = combos;
                }
                continue;
            }

            if (reader.CurrentKind is not YamlEventKind.SequenceStart and not YamlEventKind.Scalar)
            {
                var keyTextForDiagnostic = Encoding.UTF8.GetString(keyUtf8);
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{keyTextForDiagnostic} must be sequence or scalar", reader.CurrentStart);
            }

            var rowName = new StringNode
            {
                Value = keySlice,
                Quoted = false,
                Range = BuildScalarLocation(keyMark, keyUtf8.Length),
            };
            StringNode? rowExpr = null;
            IReadOnlyList<RawYamlValue>? rowValues = null;
            if (reader.CurrentKind == YamlEventKind.Scalar)
            {
                var valueNode = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Job,
                    $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{Encoding.UTF8.GetString(keyUtf8)} must be sequence or scalar",
                    parseWholeValueIfNoEmbedded: false);
                rowExpr = valueNode;
                rowValues = valueNode is null ? [] : [new RawYamlString { Value = valueNode }];
            }
            else if (reader.CurrentKind == YamlEventKind.SequenceStart)
            {
                rowValues = ParseRawYamlArray(ref reader, diagnostics, source, jobId, keyUtf8);
            }
            else
            {
                reader.SkipCurrentNode();
            }

            rows ??= new Dictionary<Utf8String, MatrixRow>();
            rows[Utf8String.FromLowerAscii(keyUtf8)] = new MatrixRow
            {
                Name = rowName,
                Expression = rowExpr,
                Values = rowValues,
            };
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new Matrix
        {
            Include = include,
            Exclude = exclude,
            Rows = rows,
            Range = default,
        };
    }

    private static MatrixCombinations[] ParseMatrixCombinations(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        Utf8Slice jobId,
        string section)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var expr = ParseStringAndValidateExpression(
                ref reader,
                diagnostics,
                ExpressionValidationContext.Job,
                $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{section} must be sequence or scalar",
                parseWholeValueIfNoEmbedded: false);
            return
            [
                new MatrixCombinations
                {
                    Expression = expr,
                    Entries = null,
                }
            ];
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{section} must be sequence or scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return [];
        }

        var entries = new List<IReadOnlyDictionary<Utf8String, RawYamlValue>>();
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.MappingStart)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{section} item must be mapping", reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            entries.Add(ParseRawYamlObject(ref reader, diagnostics, source, jobId));
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }

        return
        [
            new MatrixCombinations
            {
                Entries = entries,
            }
        ];
    }

    private static IReadOnlyList<RawYamlValue> ParseRawYamlArray(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        Utf8Slice jobId,
        ReadOnlySpan<byte> rowNameUtf8)
    {
        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{Encoding.UTF8.GetString(rowNameUtf8)} must be sequence or scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return [];
        }

        var values = new List<RawYamlValue>();
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            values.Add(ParseRawYamlValue(ref reader, diagnostics, source, jobId));
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }

        return values;
    }

    private static RawYamlValue ParseRawYamlValue(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        Utf8Slice jobId)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var node = ParseString(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' matrix value must be scalar, mapping, or sequence", allowEmpty: true)
                ?? new StringNode { Value = default, Quoted = false, Range = default };
            return new RawYamlString { Value = node };
        }

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            return new RawYamlObject
            {
                Properties = ParseRawYamlObject(ref reader, diagnostics, source, jobId),
            };
        }

        if (reader.CurrentKind == YamlEventKind.SequenceStart)
        {
            return new RawYamlArray
            {
                Items = ParseRawYamlArray(ref reader, diagnostics, source, jobId, "matrix"u8),
            };
        }

        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' matrix value has unsupported shape", reader.CurrentStart);
        reader.SkipCurrentNode();
        return new RawYamlString { Value = new StringNode { Value = default, Quoted = false, Range = default } };
    }

    private static IReadOnlyDictionary<Utf8String, RawYamlValue> ParseRawYamlObject(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        Utf8Slice jobId)
    {
        var map = new Dictionary<Utf8String, RawYamlValue>();
        var keys = new HashSet<Utf8String>();
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' matrix object key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "matrix object"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var key = Utf8String.FromLowerAscii(keyUtf8);
            reader.Read();
            if (reader.End)
            {
                break;
            }

            map[key] = ParseRawYamlValue(ref reader, diagnostics, source, jobId);
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
    }

    private static Services? ParseServices(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' services must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, Service>();
        var keys = new HashSet<Utf8String>();

        reader.Read(); // consume services mapping
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' services key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var serviceName = reader.GetScalarSlice();
            var serviceNameUtf8 = reader.GetScalarUtf8();
            var serviceMark = reader.CurrentStart;
            if (!TryRegisterMappingKey(
                serviceNameUtf8,
                serviceMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "services"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var serviceNameNode = new StringNode
            {
                Value = serviceName,
                Quoted = reader.IsScalarQuoted(),
                Range = BuildScalarLocation(reader.CurrentStart, serviceNameUtf8.Length),
            };
            reader.Read();
            if (reader.End)
            {
                break;
            }

            var container = ParseContainerLike(ref reader, diagnostics, source, jobId, serviceName, isService: true, requireImage: true);
            if (container is not null)
            {
                map[Utf8String.FromLowerAscii(serviceNameUtf8)] = new Service
                {
                    Name = serviceNameNode,
                    Container = container,
                    Range = serviceNameNode.Range,
                };
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new Services
        {
            ServiceMap = map,
            Range = default,
        };
    }

    private static Container? ParseContainerLike(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        Utf8Slice jobId,
        Utf8Slice serviceName,
        bool isService,
        bool requireImage)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var scalarImage = ParseString(ref reader, diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} must be scalar or mapping");
            if (scalarImage is null)
            {
                return null;
            }

            return new Container
            {
                Image = scalarImage,
                Range = scalarImage.Range,
            };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} must be scalar or mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var hasImage = false;
        StringNode? image = null;
        Credentials? credentials = null;
        Env? env = null;
        StringNode[]? ports = null;
        StringNode[]? volumes = null;
        StringNode? options = null;
        var keys = new HashSet<Utf8String>();
        reader.Read(); // consume mapping

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                FormatContainerSectionName(source, jobId, serviceName, isService)))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("image"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                hasImage = true;
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.image must be scalar", reader.CurrentStart);
                }
                image = ParseString(ref reader, diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.image must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("credentials"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                credentials = ParseCredentials(ref reader, diagnostics, source, jobId, serviceName, isService);
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                if (reader.CurrentKind != YamlEventKind.MappingStart)
                {
                    AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.env must be mapping", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    continue;
                }
                env = ParseEnvNode(
                    ref reader,
                    diagnostics,
                    source,
                    $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.env must be mapping",
                    ExpressionValidationContext.Job);
                continue;
            }

            if (keyUtf8.SequenceEqual("ports"u8) || keyUtf8.SequenceEqual("volumes"u8))
            {
                var optionKey = keyUtf8.SequenceEqual("ports"u8) ? "ports" : "volumes";
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                var values = ParseStringOrStringSequence(ref reader, diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.{optionKey} must be scalar or sequence of scalar");
                if (optionKey == "ports")
                {
                    ports = values;
                }
                else
                {
                    volumes = values;
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("options"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.options must be scalar", reader.CurrentStart);
                }
                options = ParseString(ref reader, diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.options must be scalar");
                continue;
            }

            var key = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            if (reader.End)
            {
                break;
            }

            AddError(diagnostics, $"unexpected {FormatContainerSectionName(source, jobId, serviceName, isService)} key: {key}", keyMark);
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (requireImage && !hasImage)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.image is required", new TextPosition(0, 1, 1));
        }

        return new Container
        {
            Image = image ?? new StringNode { Value = default, Quoted = false, Range = default },
            Credentials = credentials,
            Env = env,
            Ports = ports,
            Volumes = volumes,
            Options = options,
            Range = image?.Range ?? default,
        };
    }

    private static Credentials? ParseCredentials(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        Utf8Slice jobId,
        Utf8Slice serviceName,
        bool isService)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var hasUsername = false;
        var hasPassword = false;
        StringNode? username = null;
        StringNode? password = null;
        var keys = new HashSet<Utf8String>();
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var isUsername = keyUtf8.SequenceEqual("username"u8);
            var isPassword = keyUtf8.SequenceEqual("password"u8);
            var keyText = isUsername || isPassword ? null : Encoding.UTF8.GetString(keyUtf8);

            reader.Read();
            if (reader.End)
            {
                break;
            }

            if (isUsername)
            {
                hasUsername = true;
                username = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Job,
                    $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials.username must be scalar",
                    parseWholeValueIfNoEmbedded: false);
                continue;
            }
            else if (isPassword)
            {
                hasPassword = true;
                password = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Job,
                    $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials.password must be scalar",
                    parseWholeValueIfNoEmbedded: false);
                continue;
            }
            else
            {
                AddError(diagnostics, $"unexpected {FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials key: {keyText}", keyMark);
            }

            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                var fieldName = isUsername
                    ? "username"
                    : isPassword
                        ? "password"
                        : keyText;
                AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials.{fieldName} must be scalar", reader.CurrentStart);
            }
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (!hasUsername || !hasPassword)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials requires both username and password", new TextPosition(0, 1, 1));
        }

        return new Credentials
        {
            Username = username,
            Password = password,
            Range = username?.Range ?? password?.Range ?? default,
        };
    }

    private static void ParseStringMapping(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        string error,
        ExpressionValidationContext? expressionContext = null)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        var keys = new HashSet<Utf8String>();
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.CaseSensitive,
                error))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            reader.Read();
            if (reader.End)
            {
                break;
            }

            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            if (expressionContext.HasValue)
            {
                var valueMark = reader.CurrentStart;
                var valueUtf8 = reader.GetScalarUtf8();
                ValidateExpressionText(
                    valueUtf8,
                    BuildScalarLocation(valueMark, valueUtf8.Length),
                    expressionContext.Value,
                    diagnostics,
                    parseWholeValueIfNoEmbedded: false);
                reader.Read();
                continue;
            }

            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }
    }

    private static Runner? ParseRunsOnNode(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        var section = $"job '{DecodeUtf8(source, jobId)}' runs-on";

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            StringNode[]? labels = null;
            StringNode? labelsExpr = null;
            StringNode? group = null;
            var keys = new HashSet<Utf8String>();

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
                if (!TryRegisterMappingKey(
                    keyUtf8,
                    keyMark,
                    diagnostics,
                    keys,
                    MappingKeyComparison.AsciiCaseInsensitive,
                    "runs-on"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var isLabels = keyUtf8.SequenceEqual("labels"u8);
                var isGroup = keyUtf8.SequenceEqual("group"u8);
                var unknownKey = isLabels || isGroup ? null : Encoding.UTF8.GetString(keyUtf8);

                reader.Read();
                if (reader.End)
                {
                    break;
                }

                if (isLabels)
                {
                    if (reader.CurrentKind == YamlEventKind.Scalar)
                    {
                        var valueUtf8 = reader.GetScalarUtf8();
                        if (ContainsExpression(valueUtf8))
                        {
                            labelsExpr = ParseStringAndValidateExpression(
                                ref reader,
                                diagnostics,
                                ExpressionValidationContext.Job,
                                $"{section}.labels must be scalar, sequence, or expression",
                                parseWholeValueIfNoEmbedded: false);
                        }
                        else
                        {
                            labels = ParseStringOrStringSequence(
                                ref reader,
                                diagnostics,
                                $"{section}.labels must be scalar, sequence, or expression");
                        }
                    }
                    else
                    {
                        labels = ParseStringOrStringSequence(
                            ref reader,
                            diagnostics,
                            $"{section}.labels must be scalar, sequence, or expression");
                    }

                    continue;
                }

                if (isGroup)
                {
                    group = ParseStringAndValidateExpression(
                        ref reader,
                        diagnostics,
                        ExpressionValidationContext.Job,
                        $"{section}.group must be scalar",
                        parseWholeValueIfNoEmbedded: false);
                    continue;
                }

                AddError(diagnostics, $"unexpected runs-on key: {unknownKey}", keyMark);
                reader.SkipCurrentNode();
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
                var expr = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Job,
                    $"{section} must be scalar, sequence, or mapping",
                    parseWholeValueIfNoEmbedded: false);
                return new Runner
                {
                    LabelsExpr = expr,
                    Range = expr?.Range ?? default,
                };
            }
        }

        var labelsFallback = ParseStringOrStringSequence(ref reader, diagnostics, $"{section} must be scalar, sequence, or mapping");
        return new Runner
        {
            Labels = labelsFallback,
            Range = labelsFallback.Length > 0 ? labelsFallback[0].Range : default,
        };
    }

    private static Seiton.Core.Parsing.Ast.Environment? ParseEnvironmentNode(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var name = ParseString(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment must be scalar or mapping");
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
        var keys = new HashSet<Utf8String>();

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
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "environment"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("name"u8))
            {
                reader.Read();
                nameNode = ParseString(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' environment.name must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("url"u8))
            {
                reader.Read();
                urlNode = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Job,
                    $"job '{DecodeUtf8(source, jobId)}' environment.url must be scalar",
                    parseWholeValueIfNoEmbedded: false);
                continue;
            }

            if (keyUtf8.SequenceEqual("deployment"u8))
            {
                reader.Read();
                deploymentNode = ParseBoolOrExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Job,
                    $"job '{DecodeUtf8(source, jobId)}' environment.deployment must be bool or expression");
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected environment key '{unknown}' in job '{DecodeUtf8(source, jobId)}'", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

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

    private static Dictionary<Utf8String, StringNode>? ParseOutputsNode(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' outputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var outputs = new Dictionary<Utf8String, StringNode>();
        var keys = new HashSet<Utf8String>();
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
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
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

            var value = ParseStringAndValidateExpression(
                ref reader,
                diagnostics,
                ExpressionValidationContext.Job,
                $"job '{DecodeUtf8(source, jobId)}' outputs.{Encoding.UTF8.GetString(keyUtf8)} must be scalar",
                parseWholeValueIfNoEmbedded: false);
            outputs[key] = value ?? keyNode;
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return outputs;
    }

    private static Dictionary<Utf8String, WorkflowCallInput>? ParseWorkflowCallInputsNode(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' with must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, WorkflowCallInput>();
        var keys = new HashSet<Utf8String>();
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
            if (!TryRegisterMappingKey(
                nameUtf8,
                nameMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "with"))
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
                valueNode = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Job,
                    $"job '{DecodeUtf8(source, jobId)}' with.{Encoding.UTF8.GetString(nameUtf8)} must be scalar",
                    parseWholeValueIfNoEmbedded: false);
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

    private static Dictionary<Utf8String, WorkflowCallSecret>? ParseWorkflowCallSecretsNode(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        Utf8Slice jobId,
        out bool inheritSecrets)
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
        var keys = new HashSet<Utf8String>();
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
            if (!TryRegisterMappingKey(
                nameUtf8,
                nameMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "secrets"))
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
                valueNode = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Job,
                    $"job '{DecodeUtf8(source, jobId)}' secrets.{Encoding.UTF8.GetString(nameUtf8)} must be scalar",
                    parseWholeValueIfNoEmbedded: false);
            }
            catch
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' secrets.{Encoding.UTF8.GetString(nameUtf8)} must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                valueNode = null;
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

    private static void ParseJobSecrets(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
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

        ParseStringMapping(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' secrets must be mapping or scalar 'inherit'");
    }

    private static void ParseConditionalExpression(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ExpressionValidationContext context,
        string shapeError)
    {
        _ = ParseExpression(ref reader, diagnostics, context, shapeError);
    }

    private static void ValidateExpressionText(
        ReadOnlySpan<byte> valueUtf8,
        TextRange valueLocation,
        ExpressionValidationContext context,
        List<Diagnostic> diagnostics,
        bool parseWholeValueIfNoEmbedded)
    {
        var hasEmbedded = false;
        var i = 0;
        while (i + 3 < valueUtf8.Length)
        {
            if (valueUtf8[i] == (byte)'$' && valueUtf8[i + 1] == (byte)'{' && valueUtf8[i + 2] == (byte)'{')
            {
                hasEmbedded = true;
                var exprStart = i + 3;
                var end = IndexOf(valueUtf8, exprStart, "}}"u8);
                if (end < 0)
                {
                    break;
                }

                var trimmed = TrimAsciiWhiteSpace(valueUtf8, exprStart, end - exprStart);
                if (trimmed.Length > 0)
                {
                    var expressionUtf8 = valueUtf8.Slice(trimmed.Offset, trimmed.Length);
                    var expressionLocation = ShiftLocation(valueLocation, trimmed.Offset, trimmed.Length);
                    ParseAndValidateExpression(expressionUtf8, expressionLocation, context, diagnostics);
                }

                i = end + 2;
                continue;
            }

            i++;
        }

        if (!hasEmbedded && parseWholeValueIfNoEmbedded)
        {
            var trimmed = TrimAsciiWhiteSpace(valueUtf8, 0, valueUtf8.Length);
            if (trimmed.Length <= 0)
            {
                return;
            }

            ParseAndValidateExpression(
                valueUtf8.Slice(trimmed.Offset, trimmed.Length),
                ShiftLocation(valueLocation, trimmed.Offset, trimmed.Length),
                context,
                diagnostics);
        }
    }

    private static void ParseAndValidateExpression(
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context,
        List<Diagnostic> diagnostics)
    {
        var parseResult = ExpressionParser.Parse(expressionUtf8);
        for (var i = 0; i < parseResult.Diagnostics.Length; i++)
        {
            var parseDiagnostic = parseResult.Diagnostics[i];
            diagnostics.Add(new Diagnostic(
                parseDiagnostic.Severity,
                $"expression parse error: {parseDiagnostic.Message}",
                ShiftLocation(expressionLocation, parseDiagnostic.Location.Start, parseDiagnostic.Location.Length)));
        }

        var semanticDiagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expressionUtf8,
            expressionLocation,
            context);
        for (var i = 0; i < semanticDiagnostics.Length; i++)
        {
            diagnostics.Add(semanticDiagnostics[i]);
        }
    }

    private static bool ContainsExpression(ReadOnlySpan<byte> valueUtf8)
    {
        for (var i = 0; i + 2 < valueUtf8.Length; i++)
        {
            if (valueUtf8[i] == (byte)'$'
                && valueUtf8[i + 1] == (byte)'{'
                && valueUtf8[i + 2] == (byte)'{')
            {
                return true;
            }
        }

        return false;
    }

    private static TextRange BuildScalarLocation(TextPosition mark, int length)
    {
        var safeLength = length <= 0 ? 1 : length;
        return new TextRange(
            Start: mark.Position,
            Length: safeLength,
            StartLine: mark.Line,
            StartColumn: mark.Col,
            EndLine: mark.Line,
            EndColumn: mark.Col + safeLength - 1);
    }

    private static TextRange ShiftLocation(TextRange baseLocation, int relativeOffset, int length)
    {
        var safeLength = length <= 0 ? 1 : length;
        return new TextRange(
            Start: baseLocation.Start + relativeOffset,
            Length: safeLength,
            StartLine: baseLocation.StartLine,
            StartColumn: baseLocation.StartColumn + relativeOffset,
            EndLine: baseLocation.EndLine,
            EndColumn: baseLocation.StartColumn + relativeOffset + safeLength - 1);
    }

    private static Utf8Slice TrimAsciiWhiteSpace(ReadOnlySpan<byte> source, int offset, int length)
    {
        if (length <= 0)
        {
            return new Utf8Slice(offset, 0);
        }

        var start = offset;
        var end = offset + length - 1;

        while (start <= end && IsAsciiWhiteSpace(source[start]))
        {
            start++;
        }

        while (end >= start && IsAsciiWhiteSpace(source[end]))
        {
            end--;
        }

        if (end < start)
        {
            return new Utf8Slice(offset, 0);
        }

        return new Utf8Slice(start, end - start + 1);
    }

    private static int IndexOf(ReadOnlySpan<byte> source, int start, ReadOnlySpan<byte> pattern)
    {
        if (pattern.IsEmpty || start >= source.Length)
        {
            return -1;
        }

        for (var i = start; i <= source.Length - pattern.Length; i++)
        {
            if (source.Slice(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsAsciiWhiteSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool TryRegisterMappingKey(
        ReadOnlySpan<byte> keyUtf8,
        TextPosition keyMark,
        List<Diagnostic> diagnostics,
        HashSet<Utf8String> keys,
        MappingKeyComparison comparison,
        string mappingName)
    {
        if (keyUtf8.SequenceEqual("<<"u8))
        {
            AddError(diagnostics, $"{mappingName} does not support merge key '<<'", keyMark);
            return false;
        }

        var normalizedKey = comparison == MappingKeyComparison.CaseSensitive
            ? new Utf8String(keyUtf8)
            : Utf8String.FromLowerAscii(keyUtf8);
        if (keys.Add(normalizedKey))
        {
            return true;
        }

        AddError(diagnostics, $"{mappingName} contains duplicate key: {Encoding.UTF8.GetString(keyUtf8)}", keyMark);
        return false;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> source, Utf8Slice slice)
    {
        return Encoding.UTF8.GetString(slice.AsSpan(source));
    }

    private static string FormatContainerSectionName(ReadOnlySpan<byte> source, Utf8Slice jobId, Utf8Slice serviceName, bool isService)
    {
        var jobIdText = DecodeUtf8(source, jobId);
        if (!isService)
        {
            return $"job '{jobIdText}' container";
        }

        return $"job '{jobIdText}' service '{DecodeUtf8(source, serviceName)}'";
    }

    private static void AddError(List<Diagnostic> diagnostics, string message, TextPosition mark)
    {
        var location = new TextRange(
            Start: mark.Position,
            Length: 0,
            StartLine: mark.Line,
            StartColumn: mark.Col,
            EndLine: mark.Line,
            EndColumn: mark.Col);

        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location));
    }

    private static void ValidateKnownOnEvent(in OnEventInfo eventInfo, TextPosition eventMark, List<Diagnostic> diagnostics)
    {
        if (!eventInfo.IsKnown)
        {
            AddError(diagnostics, $"unknown event in on: {eventInfo.Name}", eventMark);
        }
    }

    private static OnEventInfo ReadOnEventInfo(ref VYamlStreamAdapter reader)
    {
        try
        {
            var eventNameUtf8 = reader.GetScalarUtf8();
            if (WebhookTypes.TryGet(eventNameUtf8, out var knownEventName, out var knownSpec))
            {
                return new OnEventInfo(knownEventName, isKnown: true, knownSpec);
            }

            return new OnEventInfo(Encoding.UTF8.GetString(eventNameUtf8), isKnown: false, default);
        }
        catch
        {
            // Fall back to scalar string for odd scalar representations.
        }

        return new OnEventInfo(reader.GetScalarString() ?? string.Empty, isKnown: false, default);
    }

    private static void ParseOnTypes(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, in OnEventInfo eventInfo)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var valueUtf8 = reader.GetScalarUtf8();
            if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeAllowed(valueUtf8))
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", reader.CurrentStart);
            }

            reader.Read();
            return;
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, $"on.{eventInfo.Name}.types must be scalar or sequence of scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types must be scalar or sequence of scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            var valueUtf8 = reader.GetScalarUtf8();
            if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeAllowed(valueUtf8))
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", reader.CurrentStart);
            }

            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }
    }
}

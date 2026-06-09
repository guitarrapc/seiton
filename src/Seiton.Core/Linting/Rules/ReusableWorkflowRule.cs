using System.Globalization;
using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates reusable workflow call semantics (<c>uses:</c>/<c>with:</c>/<c>secrets:</c> at job level).</summary>
public sealed class ReusableWorkflowRule() : RuleBase(RuleId.ReusableWorkflow)
{
    private const int LookupHashSetThreshold = 8;

    private readonly Dictionary<string, LocalWorkflowContract?> localWorkflowContracts = new(StringComparer.OrdinalIgnoreCase);

    public override string Name => "Reusable Workflow Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        localWorkflowContracts.Clear();
    }

    public override void VisitJobPre(Job job)
    {
        var workflowCall = job.WorkflowCall;
        if (workflowCall is null)
        {
            return;
        }

        var jobId = Decode(Arena.GetStringSlice(job.Id));
        var hasUses = HasNodeValue(workflowCall.Uses, Arena);

        if (!hasUses)
        {
            if (workflowCall.Inputs is not null && workflowCall.Inputs.Value.Count > 0)
            {
                AddJobError(job, $"jobs.'{jobId}' key 'with' requires uses", workflowCall.WithKeyRange ?? BuildJobLocation(job));
            }

            if ((workflowCall.Secrets is not null && workflowCall.Secrets.Value.Count > 0) || workflowCall.InheritSecrets)
            {
                AddJobError(job, $"jobs.'{jobId}' key 'secrets' requires uses", workflowCall.SecretsKeyRange ?? BuildJobLocation(job));
            }

            return;
        }

        ReportIfPresent(job, job.RunsOn is not null, "runs-on", jobId, job.RunsOnKeyRange);
        ReportIfPresent(job, job.Environment is not null, "environment", jobId, null);
        ReportIfPresent(job, job.Outputs is not null && job.Outputs.Value.Count > 0, "outputs", jobId, null);
        ReportIfPresent(job, job.Env is not null, "env", jobId, null);
        ReportIfPresent(job, job.Defaults is not null, "defaults", jobId, null);
        ReportIfPresent(job, job.Steps is not null && job.Steps.Count > 0, "steps", jobId, job.StepsKeyRange);
        ReportIfPresent(job, job.TimeoutMinutes.HasValue, "timeout-minutes", jobId, null);
        ReportIfPresent(job, job.ContinueOnError.HasValue, "continue-on-error", jobId, null);
        ReportIfPresent(job, job.Container is not null, "container", jobId, null);
        ReportIfPresent(job, job.Services is not null, "services", jobId, null);

        ValidateReusableWorkflowUses(job, workflowCall, jobId);
    }

    private void ValidateReusableWorkflowUses(Job job, WorkflowCall workflowCall, string jobId)
    {
        var uses = Arena.GetStringValue(workflowCall.Uses);

        // Local workflow (starts with ./)
        if (uses.StartsWith("./"u8))
        {
            // Local paths must not contain @ref — validate format before contract
            if (uses.IndexOf((byte)'@') >= 0)
            {
                AddReusableWorkflowUsesFormatError(job, workflowCall);
                return;
            }

            ValidateLocalReusableWorkflowContract(job, workflowCall, jobId, uses);
            return;
        }

        // ../ prefix is not valid for reusable workflows (only ./ is allowed)
        if (uses.StartsWith("../"u8))
        {
            AddReusableWorkflowUsesFormatError(job, workflowCall);
            return;
        }

        // Remote workflow — validate format: owner/repo/path/to/workflow.yml@ref
        ValidateRemoteReusableWorkflowFormat(job, workflowCall, jobId, uses);
    }

    private void ValidateRemoteReusableWorkflowFormat(Job job, WorkflowCall workflowCall, string jobId, ReadOnlySpan<byte> uses)
    {
        // Must contain @ref
        var atIndex = uses.IndexOf((byte)'@');
        if (atIndex < 0 || atIndex == uses.Length - 1)
        {
            AddReusableWorkflowUsesFormatError(job, workflowCall);
            return;
        }

        // Count path segments before @ref — need at least 3 (owner/repo/path)
        var pathPart = uses[..atIndex];

        // Must not start with /
        if (pathPart.Length > 0 && pathPart[0] == (byte)'/')
        {
            AddReusableWorkflowUsesFormatError(job, workflowCall);
            return;
        }

        var slashCount = 0;
        foreach (var b in pathPart)
        {
            if (b == (byte)'/')
            {
                slashCount++;
            }
        }

        // Need at least 2 slashes: owner/repo/path (3 segments)
        if (slashCount < 2)
        {
            AddReusableWorkflowUsesFormatError(job, workflowCall);
        }
    }

    private void AddReusableWorkflowUsesFormatError(Job job, WorkflowCall workflowCall)
    {
        var usesStr = Decode(Arena.GetStringSlice(workflowCall.Uses));
        AddJobError(
            job,
            $"reusable workflow call \"{usesStr}\" at \"uses\" is not following the format \"owner/repo/path/to/workflow.yml@ref\" nor \"./path/to/workflow.yml\". see https://docs.github.com/en/actions/learn-github-actions/reusing-workflows for more details",
            BuildUsesLocation(workflowCall));
    }

    private void ValidateLocalReusableWorkflowContract(Job job, WorkflowCall workflowCall, string jobId, ReadOnlySpan<byte> uses)
    {
        if (Config.Utf8Yaml is null
            || string.IsNullOrEmpty(Config.FilePath)
            || !Path.IsPathFullyQualified(Config.FilePath)
            || !File.Exists(Config.FilePath))
        {
            return;
        }

        if (!TryResolveLocalWorkflowPath(uses, out var resolvedPath, out var relativePath, out var invalidRefFormat))
        {
            if (invalidRefFormat)
            {
                AddJobError(
                    job,
                    $"jobs.'{jobId}'.uses local reusable workflow reference must not contain '@ref'",
                    BuildUsesLocation(workflowCall));
            }

            return;
        }

        var contract = GetLocalWorkflowContract(job, workflowCall, jobId, relativePath, resolvedPath);
        if (contract is null)
        {
            return;
        }

        ValidateWorkflowCallInputs(job, jobId, workflowCall, contract);
        ValidateWorkflowCallSecrets(job, jobId, workflowCall, contract);
    }

    private LocalWorkflowContract? GetLocalWorkflowContract(Job job, WorkflowCall workflowCall, string jobId, string relativePath, string resolvedPath)
    {
        if (localWorkflowContracts.TryGetValue(resolvedPath, out var cached))
        {
            return cached;
        }

        if (!File.Exists(resolvedPath))
        {
            AddJobError(job, $"jobs.'{jobId}' references local reusable workflow '{relativePath}' but the file does not exist", BuildUsesLocation(workflowCall));
            localWorkflowContracts[resolvedPath] = null;
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(resolvedPath);
        }
        catch
        {
            localWorkflowContracts[resolvedPath] = null;
            return null;
        }

        var parseResult = WorkflowParser.ParseDirect(bytes, resolvedPath, out var parseArena);
        if (parseResult.HasFatalError || parseResult.Workflow is null)
        {
            parseArena?.Dispose();
            AddJobError(job, $"jobs.'{jobId}' references local reusable workflow '{relativePath}' but it could not be parsed", BuildUsesLocation(workflowCall));
            localWorkflowContracts[resolvedPath] = null;
            return null;
        }

        WorkflowCallEvent? workflowCallEvent = null;
        for (var i = 0; i < parseResult.Workflow.On.Count; i++)
        {
            if (parseResult.Workflow.On[i] is WorkflowCallEvent current)
            {
                workflowCallEvent = current;
                break;
            }
        }

        if (workflowCallEvent is null)
        {
            parseArena?.Dispose();
            AddJobError(job, $"jobs.'{jobId}' references local workflow '{relativePath}' that does not declare on.workflow_call", BuildJobLocation(job));
            localWorkflowContracts[resolvedPath] = null;
            return null;
        }

        var ownedArena = parseArena!;
        var contract = LocalWorkflowContract.FromEvent(workflowCallEvent, bytes, ownedArena);
        ownedArena.Dispose();
        localWorkflowContracts[resolvedPath] = contract;
        return contract;
    }

    private void ValidateWorkflowCallInputs(Job job, string jobId, WorkflowCall workflowCall, LocalWorkflowContract contract)
    {
        HashSet<string>? providedInputNames = null;
        var useInputHashSet = workflowCall.Inputs is not null && workflowCall.Inputs.Value.Count > LookupHashSetThreshold;
        if (workflowCall.Inputs is not null)
        {
            if (useInputHashSet)
            {
                providedInputNames = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (var pair in workflowCall.Inputs.Value)
            {
                var inputName = Decode(pair.Key);
                providedInputNames?.Add(inputName);
                if (!contract.Inputs.TryGetValue(inputName, out var expected))
                {
                    AddJobError(
                        job,
                        $"jobs.'{jobId}' passes unknown reusable workflow input '{inputName}'",
                        Arena.GetStringRange(pair.Value.Name));
                    continue;
                }

                ValidateInputType(job, jobId, pair.Value, expected);
            }
        }

        foreach (var requiredInput in contract.RequiredInputs)
        {
            var hasRequiredInput = providedInputNames is not null
                ? providedInputNames.Contains(requiredInput)
                : (workflowCall.Inputs is not null && ContainsInput(workflowCall.Inputs.Value, requiredInput));

            if (hasRequiredInput)
            {
                continue;
            }

            AddJobError(
                job,
                $"jobs.'{jobId}' is missing required reusable workflow input '{requiredInput}'",
                BuildUsesLocation(workflowCall));
        }
    }

    private void ValidateInputType(Job job, string jobId, WorkflowCallInput providedInput, InputContract expected)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var value = providedInput.Value;
        if (ExpressionScanHelpers.ContainsExpressionMarker(value, Arena))
        {
            return;
        }

        var valueText = Decode(Arena.GetStringSlice(value));
        if (expected.Type == WorkflowCallInputType.Boolean)
        {
            if (IsBooleanLiteral(valueText))
            {
                return;
            }

            AddJobError(
                job,
                $"jobs.'{jobId}'.with '{expected.Name}' expects boolean but got '{valueText}'",
                Arena.GetStringRange(value));
            return;
        }

        if (expected.Type == WorkflowCallInputType.Number)
        {
            if (double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return;
            }

            AddJobError(
                job,
                $"jobs.'{jobId}'.with '{expected.Name}' expects number but got '{valueText}'",
                Arena.GetStringRange(value));
        }
    }

    private void ValidateWorkflowCallSecrets(Job job, string jobId, WorkflowCall workflowCall, LocalWorkflowContract contract)
    {
        HashSet<string>? providedSecretNames = null;
        var useSecretHashSet = workflowCall.Secrets is not null && workflowCall.Secrets.Value.Count > LookupHashSetThreshold;
        if (workflowCall.Secrets is not null)
        {
            if (useSecretHashSet)
            {
                providedSecretNames = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (var pair in workflowCall.Secrets.Value)
            {
                var secretName = Decode(pair.Key);
                providedSecretNames?.Add(secretName);
                if (contract.Secrets.Contains(secretName))
                {
                    continue;
                }

                AddJobError(
                    job,
                    $"jobs.'{jobId}' passes unknown reusable workflow secret '{secretName}'",
                    Arena.GetStringRange(pair.Value.Name));
            }
        }

        if (workflowCall.InheritSecrets)
        {
            return;
        }

        foreach (var requiredSecret in contract.RequiredSecrets)
        {
            var hasRequiredSecret = providedSecretNames is not null
                ? providedSecretNames.Contains(requiredSecret)
                : (workflowCall.Secrets is not null && ContainsSecret(workflowCall.Secrets.Value, requiredSecret));

            if (hasRequiredSecret)
            {
                continue;
            }

            AddJobError(
                job,
                $"jobs.'{jobId}' is missing required reusable workflow secret '{requiredSecret}'",
                BuildUsesLocation(workflowCall));
        }
    }

    private bool TryResolveLocalWorkflowPath(ReadOnlySpan<byte> uses, out string resolvedPath, out string relativePath, out bool invalidRefFormat)
    {
        resolvedPath = string.Empty;
        relativePath = string.Empty;
        invalidRefFormat = false;

        if (!uses.StartsWith("./"u8))
        {
            return false;
        }

        if (uses.IndexOf((byte)'@') >= 0)
        {
            invalidRefFormat = true;
            return false;
        }

        relativePath = DecodeAscii(uses); // Keep forward slashes for display in diagnostics
        var baseDirectory = ActionRefHelpers.ResolveLocalReferenceBaseDirectory(Config.FilePath!, relativePath);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return false;
        }

        var normalizedPath = ActionRefHelpers.NormalizeFullPath(baseDirectory, relativePath);
        if (normalizedPath is null)
        {
            return false;
        }

        resolvedPath = normalizedPath;
        return true;
    }

    private static bool IsBooleanLiteral(string value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeAscii(ReadOnlySpan<byte> utf8)
    {
        var chars = new char[utf8.Length];
        for (var i = 0; i < utf8.Length; i++)
        {
            chars[i] = (char)utf8[i];
        }

        return new string(chars);
    }

    private bool ContainsInput(SliceMap<WorkflowCallInput> providedInputs, string requiredInput)
    {
        foreach (var pair in providedInputs)
        {
            if (string.Equals(Decode(pair.Key), requiredInput, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsSecret(SliceMap<WorkflowCallSecret> providedSecrets, string requiredSecret)
    {
        foreach (var pair in providedSecrets)
        {
            if (string.Equals(Decode(pair.Key), requiredSecret, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void ReportIfPresent(Job job, bool present, string keyName, string jobId, TextRange? keyRange)
    {
        if (!present)
        {
            return;
        }

        AddJobError(job, $"when jobs.'{jobId}' calls reusable workflow with uses, key '{keyName}' is not allowed", keyRange ?? BuildJobLocation(job));
    }

    private sealed record InputContract(string Name, WorkflowCallInputType Type);

    private sealed class LocalWorkflowContract
    {
        public Dictionary<string, InputContract> Inputs { get; } = new(StringComparer.Ordinal);

        public HashSet<string> RequiredInputs { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Secrets { get; } = new(StringComparer.Ordinal);

        public HashSet<string> RequiredSecrets { get; } = new(StringComparer.Ordinal);

        public static LocalWorkflowContract FromEvent(WorkflowCallEvent workflowCallEvent, byte[] source, AstArena arena)
        {
            var contract = new LocalWorkflowContract();

            if (workflowCallEvent.Inputs is not null)
            {
                for (var i = 0; i < workflowCallEvent.Inputs.Count; i++)
                {
                    var input = workflowCallEvent.Inputs[i];
                    var inputName = Decode(input.Id);
                    contract.Inputs[inputName] = new InputContract(inputName, input.Type);

                    var hasDefault = input.Default.HasValue;
                    if (input.Required.HasValue && arena.GetBoolValue(input.Required) && !hasDefault)
                    {
                        contract.RequiredInputs.Add(inputName);
                    }
                }
            }

            if (workflowCallEvent.Secrets is not null)
            {
                foreach (var pair in workflowCallEvent.Secrets.Value)
                {
                    var secretName = Encoding.UTF8.GetString(pair.Key.AsSpan(source));
                    contract.Secrets.Add(secretName);
                    if (pair.Value.Required.HasValue && arena.GetBoolValue(pair.Value.Required))
                    {
                        contract.RequiredSecrets.Add(secretName);
                    }
                }
            }

            return contract;
        }
    }
}

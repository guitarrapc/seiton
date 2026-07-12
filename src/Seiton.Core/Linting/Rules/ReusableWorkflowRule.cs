using System.Globalization;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates reusable workflow call semantics (<c>uses:</c>/<c>with:</c>/<c>secrets:</c> at job level).</summary>
public sealed class ReusableWorkflowRule() : RuleBase(RuleId.ReusableWorkflow)
{
    private const int LookupHashSetThreshold = 8;

    private readonly Dictionary<string, LocalWorkflowContract?> localWorkflowContracts = new(StringComparer.OrdinalIgnoreCase);

    public override string Name => "Reusable Workflow Rule";

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        localWorkflowContracts.Clear();
    }

    public override void VisitJobPre(JobRef job)
    {
        var workflowCall = job.WorkflowCall;
        if (!workflowCall.HasValue)
        {
            return;
        }

        var jobId = job.Id.Decode();
        var hasUses = workflowCall.Uses.HasText;

        if (!hasUses)
        {
            if (workflowCall.Inputs.Count > 0)
            {
                AddJobError(job, $"jobs.'{jobId}' key 'with' requires uses", workflowCall.WithKeyRange ?? BuildJobLocation(job));
            }

            if (workflowCall.Secrets.Count > 0 || workflowCall.InheritSecrets)
            {
                AddJobError(job, $"jobs.'{jobId}' key 'secrets' requires uses", workflowCall.SecretsKeyRange ?? BuildJobLocation(job));
            }

            return;
        }

        ReportIfPresent(job, job.RunsOn.HasValue, "runs-on", jobId, job.RunsOnKeyRange);
        ReportIfPresent(job, job.Environment.HasValue, "environment", jobId, null);
        ReportIfPresent(job, job.Outputs.Count > 0, "outputs", jobId, null);
        ReportIfPresent(job, job.Env.HasValue, "env", jobId, null);
        ReportIfPresent(job, job.Defaults.HasValue, "defaults", jobId, null);
        ReportIfPresent(job, job.Steps.Count > 0, "steps", jobId, job.StepsKeyRange);
        ReportIfPresent(job, job.TimeoutMinutes.HasValue, "timeout-minutes", jobId, null);
        ReportIfPresent(job, job.ContinueOnError.HasValue, "continue-on-error", jobId, null);
        ReportIfPresent(job, job.Container.HasValue, "container", jobId, null);
        ReportIfPresent(job, job.Services.HasValue, "services", jobId, null);

        ValidateReusableWorkflowUses(job, workflowCall, jobId);
    }

    private void ValidateReusableWorkflowUses(JobRef job, WorkflowCallRef workflowCall, string jobId)
    {
        var uses = workflowCall.Uses.Value;

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

    private void ValidateRemoteReusableWorkflowFormat(JobRef job, WorkflowCallRef workflowCall, string jobId, ReadOnlySpan<byte> uses)
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

    private void AddReusableWorkflowUsesFormatError(JobRef job, WorkflowCallRef workflowCall)
    {
        var usesStr = workflowCall.Uses.Decode();
        AddJobError(
            job,
            $"reusable workflow call \"{usesStr}\" at \"uses\" is not following the format \"owner/repo/path/to/workflow.yml@ref\" nor \"./path/to/workflow.yml\". see https://docs.github.com/en/actions/learn-github-actions/reusing-workflows for more details",
            BuildUsesLocation(workflowCall));
    }

    private void ValidateLocalReusableWorkflowContract(JobRef job, WorkflowCallRef workflowCall, string jobId, ReadOnlySpan<byte> uses)
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

    private LocalWorkflowContract? GetLocalWorkflowContract(JobRef job, WorkflowCallRef workflowCall, string jobId, string relativePath, string resolvedPath)
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

        var ownedArena = parseArena!;
        var on = new EventRefList(ownedArena, parseResult.Workflow.On);
        WorkflowCallEventRef workflowCallEvent = default;
        for (var i = 0; i < on.Count; i++)
        {
            if (on[i].Kind == EventKind.WorkflowCall)
            {
                workflowCallEvent = on[i].AsWorkflowCall();
                break;
            }
        }

        if (!workflowCallEvent.HasValue)
        {
            ownedArena.Dispose();
            AddJobError(job, $"jobs.'{jobId}' references local workflow '{relativePath}' that does not declare on.workflow_call", BuildJobLocation(job));
            localWorkflowContracts[resolvedPath] = null;
            return null;
        }

        var contract = LocalWorkflowContract.FromEvent(workflowCallEvent);
        ownedArena.Dispose();
        localWorkflowContracts[resolvedPath] = contract;
        return contract;
    }

    private void ValidateWorkflowCallInputs(JobRef job, string jobId, WorkflowCallRef workflowCall, LocalWorkflowContract contract)
    {
        HashSet<string>? providedInputNames = null;
        var useInputHashSet = workflowCall.Inputs.Count > LookupHashSetThreshold;
        if (workflowCall.Inputs.HasValue)
        {
            if (useInputHashSet)
            {
                providedInputNames = new HashSet<string>(workflowCall.Inputs.Count, StringComparer.Ordinal);
            }

            foreach (var pair in workflowCall.Inputs)
            {
                var inputName = pair.Key.Decode();
                providedInputNames?.Add(inputName);
                if (!contract.Inputs.TryGetValue(inputName, out var expected))
                {
                    AddJobError(
                        job,
                        $"jobs.'{jobId}' passes unknown reusable workflow input '{inputName}'",
                        pair.Value.Name.Range);
                    continue;
                }

                ValidateInputType(job, jobId, pair.Value, expected);
            }
        }

        foreach (var requiredInput in contract.RequiredInputs)
        {
            var hasRequiredInput = providedInputNames is not null
                ? providedInputNames.Contains(requiredInput)
                : (workflowCall.Inputs.HasValue && ContainsInput(workflowCall.Inputs, requiredInput));

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

    private void ValidateInputType(JobRef job, string jobId, WorkflowCallInputRef providedInput, InputContract expected)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var value = providedInput.Value;
        if (ExpressionScanHelpers.ContainsExpressionMarker(value.Id, Arena))
        {
            return;
        }

        var valueText = value.Decode();
        if (expected.Type == WorkflowCallInputType.Boolean)
        {
            if (IsBooleanLiteral(valueText))
            {
                return;
            }

            AddJobError(
                job,
                $"jobs.'{jobId}'.with '{expected.Name}' expects boolean but got '{valueText}'",
                value.Range);
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
                value.Range);
        }
    }

    private void ValidateWorkflowCallSecrets(JobRef job, string jobId, WorkflowCallRef workflowCall, LocalWorkflowContract contract)
    {
        HashSet<string>? providedSecretNames = null;
        var useSecretHashSet = workflowCall.Secrets.Count > LookupHashSetThreshold;
        if (workflowCall.Secrets.HasValue)
        {
            if (useSecretHashSet)
            {
                providedSecretNames = new HashSet<string>(workflowCall.Secrets.Count, StringComparer.Ordinal);
            }

            foreach (var pair in workflowCall.Secrets)
            {
                var secretName = pair.Key.Decode();
                providedSecretNames?.Add(secretName);
                if (contract.Secrets.Contains(secretName))
                {
                    continue;
                }

                AddJobError(
                    job,
                    $"jobs.'{jobId}' passes unknown reusable workflow secret '{secretName}'",
                    pair.Value.Name.Range);
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
                : (workflowCall.Secrets.HasValue && ContainsSecret(workflowCall.Secrets, requiredSecret));

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

    private bool ContainsInput(WorkflowCallInputRefMap providedInputs, string requiredInput)
    {
        foreach (var pair in providedInputs)
        {
            if (string.Equals(pair.Key.Decode(), requiredInput, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsSecret(WorkflowCallSecretRefMap providedSecrets, string requiredSecret)
    {
        foreach (var pair in providedSecrets)
        {
            if (string.Equals(pair.Key.Decode(), requiredSecret, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void ReportIfPresent(JobRef job, bool present, string keyName, string jobId, TextRange? keyRange)
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

        public static LocalWorkflowContract FromEvent(WorkflowCallEventRef workflowCallEvent)
        {
            var contract = new LocalWorkflowContract();

            var inputs = workflowCallEvent.Inputs;
            for (var i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var inputName = Decode(input.Id);
                contract.Inputs[inputName] = new InputContract(inputName, input.Type);

                var hasDefault = input.Default.HasValue;
                if (input.Required.HasValue && input.Required.Value && !hasDefault)
                {
                    contract.RequiredInputs.Add(inputName);
                }
            }

            foreach (var pair in workflowCallEvent.Secrets)
            {
                var secretName = pair.Key.Decode();
                contract.Secrets.Add(secretName);
                if (pair.Value.Required.HasValue && pair.Value.Required.Value)
                {
                    contract.RequiredSecrets.Add(secretName);
                }
            }

            return contract;
        }
    }
}

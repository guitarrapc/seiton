using System.Globalization;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class ReusableWorkflowRule : RuleBase
{
    readonly Dictionary<string, LocalWorkflowContract?> localWorkflowContracts = new(StringComparer.OrdinalIgnoreCase);

    public override string Id => "reusable-workflow";

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

        var jobId = Decode(job.Id.Value);
        var hasUses = HasNodeValue(workflowCall.Uses);

        if (!hasUses)
        {
            if (workflowCall.Inputs is not null && workflowCall.Inputs.Count > 0)
            {
                AddJobError(job, $"job '{jobId}' key 'with' requires uses");
            }

            if ((workflowCall.Secrets is not null && workflowCall.Secrets.Count > 0) || workflowCall.InheritSecrets)
            {
                AddJobError(job, $"job '{jobId}' key 'secrets' requires uses");
            }

            return;
        }

        ReportIfPresent(job, job.RunsOn is not null, "runs-on", jobId);
        ReportIfPresent(job, job.Environment is not null, "environment", jobId);
        ReportIfPresent(job, job.Outputs is not null && job.Outputs.Count > 0, "outputs", jobId);
        ReportIfPresent(job, job.Env is not null, "env", jobId);
        ReportIfPresent(job, job.Defaults is not null, "defaults", jobId);
        ReportIfPresent(job, job.Steps is not null && job.Steps.Count > 0, "steps", jobId);
        ReportIfPresent(job, job.TimeoutMinutes is not null, "timeout-minutes", jobId);
        ReportIfPresent(job, job.ContinueOnError is not null, "continue-on-error", jobId);
        ReportIfPresent(job, job.Container is not null, "container", jobId);
        ReportIfPresent(job, job.Services is not null, "services", jobId);

        ValidateLocalReusableWorkflowContract(job, workflowCall, jobId);
    }

    void ValidateLocalReusableWorkflowContract(Job job, WorkflowCall workflowCall, string jobId)
    {
        if (Config.Utf8Yaml is null
            || string.IsNullOrEmpty(Config.FilePath)
            || !Path.IsPathFullyQualified(Config.FilePath)
            || !File.Exists(Config.FilePath))
        {
            return;
        }

        var uses = workflowCall.Uses.Value.AsSpan(Config.Utf8Yaml);
        if (!TryResolveLocalWorkflowPath(uses, out var resolvedPath, out var relativePath, out var invalidRefFormat))
        {
            if (invalidRefFormat)
            {
                AddJobError(
                    job,
                    $"job '{jobId}' local reusable workflow uses must not contain '@ref'",
                    workflowCall.Uses.Range);
            }

            return;
        }

        var contract = GetLocalWorkflowContract(job, jobId, relativePath, resolvedPath);
        if (contract is null)
        {
            return;
        }

        ValidateWorkflowCallInputs(job, jobId, workflowCall, contract);
        ValidateWorkflowCallSecrets(job, jobId, workflowCall, contract);
    }

    LocalWorkflowContract? GetLocalWorkflowContract(Job job, string jobId, string relativePath, string resolvedPath)
    {
        if (localWorkflowContracts.TryGetValue(resolvedPath, out var cached))
        {
            return cached;
        }

        if (!File.Exists(resolvedPath))
        {
            AddJobError(job, $"job '{jobId}' references local reusable workflow '{relativePath}' but the file does not exist", BuildJobLocation(job));
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

        var parseResult = WorkflowParser.Parse(bytes, resolvedPath);
        if (parseResult.HasFatalError || parseResult.Workflow is null)
        {
            AddJobError(job, $"job '{jobId}' references local reusable workflow '{relativePath}' but it could not be parsed", BuildJobLocation(job));
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
            AddJobError(job, $"job '{jobId}' references local workflow '{relativePath}' that does not declare on.workflow_call", BuildJobLocation(job));
            localWorkflowContracts[resolvedPath] = null;
            return null;
        }

        var contract = LocalWorkflowContract.FromEvent(workflowCallEvent);
        localWorkflowContracts[resolvedPath] = contract;
        return contract;
    }

    void ValidateWorkflowCallInputs(Job job, string jobId, WorkflowCall workflowCall, LocalWorkflowContract contract)
    {
        if (workflowCall.Inputs is not null)
        {
            foreach (var pair in workflowCall.Inputs)
            {
                var inputName = Decode(pair.Key);
                if (!contract.Inputs.TryGetValue(inputName, out var expected))
                {
                    AddJobError(
                        job,
                        $"job '{jobId}' passes unknown reusable workflow input '{inputName}'",
                        pair.Value.Name.Range);
                    continue;
                }

                ValidateInputType(job, jobId, pair.Value, expected);
            }
        }

        foreach (var requiredInput in contract.RequiredInputs)
        {
            if (workflowCall.Inputs is not null && ContainsInput(workflowCall.Inputs, requiredInput))
            {
                continue;
            }

            AddJobError(
                job,
                $"job '{jobId}' is missing required reusable workflow input '{requiredInput}'",
                workflowCall.Uses.Range);
        }
    }

    void ValidateInputType(Job job, string jobId, WorkflowCallInput providedInput, InputContract expected)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var value = providedInput.Value;
        var valueSpan = value.Value.AsSpan(Config.Utf8Yaml);
        if (value.Expression is not null || valueSpan.IndexOf("${{"u8) >= 0)
        {
            return;
        }

        var valueText = Decode(value.Value);
        if (expected.Type == WorkflowCallInputType.Boolean)
        {
            if (IsBooleanLiteral(valueText))
            {
                return;
            }

            AddJobError(
                job,
                $"job '{jobId}' input '{expected.Name}' expects boolean but got '{valueText}'",
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
                $"job '{jobId}' input '{expected.Name}' expects number but got '{valueText}'",
                value.Range);
        }
    }

    void ValidateWorkflowCallSecrets(Job job, string jobId, WorkflowCall workflowCall, LocalWorkflowContract contract)
    {
        if (workflowCall.Secrets is not null)
        {
            foreach (var pair in workflowCall.Secrets)
            {
                var secretName = Decode(pair.Key);
                if (contract.Secrets.Contains(secretName))
                {
                    continue;
                }

                AddJobError(
                    job,
                    $"job '{jobId}' passes unknown reusable workflow secret '{secretName}'",
                    pair.Value.Name.Range);
            }
        }

        if (workflowCall.InheritSecrets)
        {
            return;
        }

        foreach (var requiredSecret in contract.RequiredSecrets)
        {
            if (workflowCall.Secrets is not null && ContainsSecret(workflowCall.Secrets, requiredSecret))
            {
                continue;
            }

            AddJobError(
                job,
                $"job '{jobId}' is missing required reusable workflow secret '{requiredSecret}'",
                workflowCall.Uses.Range);
        }
    }

    bool TryResolveLocalWorkflowPath(ReadOnlySpan<byte> uses, out string resolvedPath, out string relativePath, out bool invalidRefFormat)
    {
        resolvedPath = string.Empty;
        relativePath = string.Empty;
        invalidRefFormat = false;

        if (!uses.StartsWith("./"u8) && !uses.StartsWith("../"u8))
        {
            return false;
        }

        if (uses.IndexOf((byte)'@') >= 0)
        {
            invalidRefFormat = true;
            return false;
        }

        relativePath = DecodeAscii(uses).Replace('/', Path.DirectorySeparatorChar);
        var baseDirectory = ResolveLocalReferenceBaseDirectory(Config.FilePath!, relativePath);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return false;
        }

        try
        {
            resolvedPath = Path.GetFullPath(Path.Combine(baseDirectory, TrimCurrentDirectoryPrefix(relativePath)));
        }
        catch
        {
            return false;
        }

        return true;
    }

    static bool ContainsInput(IReadOnlyDictionary<Utf8String, WorkflowCallInput> providedInputs, string requiredInput)
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

    static bool ContainsSecret(IReadOnlyDictionary<Utf8String, WorkflowCallSecret> providedSecrets, string requiredSecret)
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

    static bool IsBooleanLiteral(string value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    static string ResolveLocalReferenceBaseDirectory(string workflowFilePath, string localPath)
    {
        var workflowDirectory = Path.GetDirectoryName(workflowFilePath);
        if (string.IsNullOrEmpty(workflowDirectory))
        {
            return string.Empty;
        }

        if (localPath.StartsWith($".{Path.DirectorySeparatorChar}.github{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && TryGetRepositoryRoot(workflowFilePath, out var repositoryRoot))
        {
            return repositoryRoot;
        }

        return workflowDirectory;
    }

    static bool TryGetRepositoryRoot(string workflowFilePath, out string repositoryRoot)
    {
        var separator = Path.DirectorySeparatorChar;
        var marker = $"{separator}.github{separator}workflows{separator}";
        var index = workflowFilePath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            repositoryRoot = workflowFilePath[..index];
            return true;
        }

        var markerAtEnd = $"{separator}.github{separator}workflows";
        if (workflowFilePath.EndsWith(markerAtEnd, StringComparison.OrdinalIgnoreCase))
        {
            repositoryRoot = workflowFilePath[..^markerAtEnd.Length];
            return true;
        }

        repositoryRoot = string.Empty;
        return false;
    }

    static string TrimCurrentDirectoryPrefix(string path)
    {
        if (path.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return path.Substring(2);
        }

        return path;
    }

    static string DecodeAscii(ReadOnlySpan<byte> utf8)
    {
        var chars = new char[utf8.Length];
        for (var i = 0; i < utf8.Length; i++)
        {
            chars[i] = (char)utf8[i];
        }

        return new string(chars);
    }

    void ReportIfPresent(Job job, bool present, string keyName, string jobId)
    {
        if (!present)
        {
            return;
        }

        AddJobError(job, $"when job '{jobId}' calls reusable workflow with uses, key '{keyName}' is not allowed");
    }

    sealed record InputContract(string Name, WorkflowCallInputType Type);

    sealed class LocalWorkflowContract
    {
        public Dictionary<string, InputContract> Inputs { get; } = new(StringComparer.Ordinal);

        public HashSet<string> RequiredInputs { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Secrets { get; } = new(StringComparer.Ordinal);

        public HashSet<string> RequiredSecrets { get; } = new(StringComparer.Ordinal);

        public static LocalWorkflowContract FromEvent(WorkflowCallEvent workflowCallEvent)
        {
            var contract = new LocalWorkflowContract();

            if (workflowCallEvent.Inputs is not null)
            {
                for (var i = 0; i < workflowCallEvent.Inputs.Count; i++)
                {
                    var input = workflowCallEvent.Inputs[i];
                    var inputName = Decode(input.Id);
                    contract.Inputs[inputName] = new InputContract(inputName, input.Type);

                    var hasDefault = input.Default is not null;
                    if (input.Required?.Value == true && !hasDefault)
                    {
                        contract.RequiredInputs.Add(inputName);
                    }
                }
            }

            if (workflowCallEvent.Secrets is not null)
            {
                foreach (var pair in workflowCallEvent.Secrets)
                {
                    var secretName = Decode(pair.Key);
                    contract.Secrets.Add(secretName);
                    if (pair.Value.Required?.Value == true)
                    {
                        contract.RequiredSecrets.Add(secretName);
                    }
                }
            }

            return contract;
        }
    }
}

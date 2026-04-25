using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class GitHubAvailabilitySourceParser
{
    public AvailabilityModel Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("GitHub availability source snapshot not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<AvailabilitySnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (snapshot is null)
        {
            throw new InvalidDataException($"GitHub availability source snapshot is invalid: {path}");
        }

        return new AvailabilityModel(
            snapshot.WorkflowRoots ?? [],
            snapshot.WorkflowCallOutputRoots ?? [],
            snapshot.JobRoots ?? [],
            snapshot.JobOutputRoots ?? [],
            snapshot.ReusableWorkflowCallSecretsRoots ?? [],
            snapshot.StrategyRoots ?? [],
            snapshot.StepRoots ?? [],
            snapshot.StepIfRoots ?? []);
    }

    private sealed class AvailabilitySnapshot
    {
        public List<string>? WorkflowRoots { get; set; }
        public List<string>? WorkflowCallOutputRoots { get; set; }
        public List<string>? JobRoots { get; set; }
        public List<string>? JobOutputRoots { get; set; }
        public List<string>? ReusableWorkflowCallSecretsRoots { get; set; }
        public List<string>? StrategyRoots { get; set; }
        public List<string>? StepRoots { get; set; }
        public List<string>? StepIfRoots { get; set; }
    }
}

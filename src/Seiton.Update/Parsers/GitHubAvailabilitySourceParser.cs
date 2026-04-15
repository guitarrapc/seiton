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
            snapshot.JobRoots ?? [],
            snapshot.StepRoots ?? []);
    }

    sealed class AvailabilitySnapshot
    {
        public List<string>? WorkflowRoots { get; set; }
        public List<string>? JobRoots { get; set; }
        public List<string>? StepRoots { get; set; }
    }
}

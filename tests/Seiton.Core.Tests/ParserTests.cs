using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class ParserTests
{
	[Test]
	public async Task Parse_MinimalWorkflow_NoDiagnostics()
	{
				var yaml = "name: ci\non: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n";

		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "minimal.yml");

		await Assert.That(result.HasFatalError).IsFalse();
		await Assert.That(result.Workflow.HasOn).IsTrue();
		await Assert.That(result.Workflow.HasJobs).IsTrue();
		await Assert.That(result.Diagnostics).IsEmpty();
	}

	[Test]
	public async Task Parse_MissingRequiredKeys_ReportsErrors()
	{
		var yaml = "name: only-name";
		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "missing.yml");

		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("required key 'on' is missing", StringComparison.Ordinal))).IsTrue();
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("required key 'jobs' is missing", StringComparison.Ordinal))).IsTrue();
	}

	[Test]
	public async Task Parse_UnknownKey_ReportsUnexpectedKey()
	{
		var yaml = "on: push\njobs: {}\nfoobar: 1\n";

		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "unknown.yml");
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unexpected workflow key: foobar", StringComparison.Ordinal))).IsTrue();
	}

	[Test]
	public async Task Parse_OnTypeInvalid_ReportsError()
	{
		var yaml = "on: true\njobs: {}\n";

		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-type.yml");
		await Assert.That(result.Diagnostics).IsEmpty();

		var yaml2 = "on: &a ref\njobs: {}\n";

		var result2 = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml2), "on-type2.yml");
		await Assert.That(result2.Diagnostics).IsEmpty();
	}

	[Test]
	public async Task Parse_JobsTypeInvalid_ReportsError()
	{
		var yaml = "on: push\njobs: []\n";

		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "jobs-type.yml");
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("jobs must be mapping", StringComparison.Ordinal))).IsTrue();
	}

	[Test]
	public async Task Parse_CorpusSmoke_Actionlint_Ghalint_Zizmor_DoesNotThrow()
	{
		var root = FindRepoRoot();
		var files = EnumerateCorpusYamlFiles(root).ToArray();
		await Assert.That(files.Length).IsGreaterThan(0);

		var failures = new List<string>();
		foreach (var file in files)
		{
			try
			{
				var bytes = File.ReadAllBytes(file);
				_ = WorkflowParser.Parse(bytes, file);
			}
			catch (Exception ex)
			{
				failures.Add($"{file}: {ex.GetType().Name}: {ex.Message}");
			}
		}

		await Assert.That(failures).IsEmpty();
	}

	private static IEnumerable<string> EnumerateCorpusYamlFiles(string repoRoot)
	{
		var refsRoot = Path.Combine(repoRoot, ".references");
		var candidates = new[]
		{
			Path.Combine(refsRoot, "actionlint-main", ".github", "workflows"),
			Path.Combine(refsRoot, "ghalint-main", ".github", "workflows"),
			Path.Combine(refsRoot, "zizmor-main", ".github", "workflows"),
			Path.Combine(refsRoot, "ghalint-main"),
		};

		foreach (var dir in candidates)
		{
			if (!Directory.Exists(dir))
			{
				continue;
			}

			foreach (var file in Directory.EnumerateFiles(dir, "*.yml", SearchOption.AllDirectories))
			{
				yield return file;
			}

			foreach (var file in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories))
			{
				yield return file;
			}
		}
	}

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null)
		{
			if (File.Exists(Path.Combine(dir.FullName, "seiton.slnx")))
			{
				return dir.FullName;
			}

			dir = dir.Parent;
		}

		throw new InvalidOperationException("Could not locate repository root.");
	}
}

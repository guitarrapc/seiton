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
	public async Task Parse_OnSequenceItemNonScalar_ReportsError()
	{
		var yaml = "on:\n  - push\n  - [nested]\njobs: {}\n";
		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-seq.yml");
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on sequence item must be scalar event name", StringComparison.Ordinal))).IsTrue();
	}

	[Test]
	public async Task Parse_OnEventOptionsMutualExclusive_ReportsError()
	{
		var yaml = "on:\n  push:\n    branches: [main]\n    branches-ignore: [dev]\njobs: {}\n";
		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-exclusive.yml");
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("cannot use both branches and branches-ignore", StringComparison.Ordinal))).IsTrue();
	}

	[Test]
	public async Task Parse_OnEventOptionsTypeInvalid_ReportsError()
	{
		var yaml = "on:\n  pull_request:\n    types: { a: b }\njobs: {}\n";
		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-types-invalid.yml");
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on.pull_request.types must be scalar or sequence of scalar", StringComparison.Ordinal))).IsTrue();
	}

	[Test]
	public async Task Parse_OnEventUnknownOption_ReportsError()
	{
		var yaml = "on:\n  push:\n    unknown-filter: 1\njobs: {}\n";
		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-unknown-option.yml");
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unexpected on.push option: unknown-filter", StringComparison.Ordinal))).IsTrue();
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

	[Test]
	public async Task Parse_CorpusSmoke_ActionlintTestdata_DoesNotThrow()
	{
		var root = FindRepoRoot();
		var actionlintTestdata = Path.Combine(root, ".references", "actionlint-main", "testdata");
		if (!Directory.Exists(actionlintTestdata))
		{
			// Optional corpus in local checkout.
			return;
		}

		var allFiles = Directory.EnumerateFiles(actionlintTestdata, "*.yml", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(actionlintTestdata, "*.yaml", SearchOption.AllDirectories))
			.ToArray();

		var files = allFiles.Where(static f =>
		{
			var n = f.Replace('\\', '/');
			return !n.Contains("/testdata/err/", StringComparison.OrdinalIgnoreCase)
				&& !n.Contains("/broken/", StringComparison.OrdinalIgnoreCase)
				&& !n.Contains("broken_yaml", StringComparison.OrdinalIgnoreCase)
				&& !n.Contains("dangling_alias", StringComparison.OrdinalIgnoreCase);
		}).ToArray();

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

	[Test]
	public async Task Parse_CorpusSmoke_ActionlintBrokenFixtures_ContainParseFailures()
	{
		var root = FindRepoRoot();
		var actionlintTestdata = Path.Combine(root, ".references", "actionlint-main", "testdata");
		if (!Directory.Exists(actionlintTestdata))
		{
			return;
		}

		var files = Directory.EnumerateFiles(actionlintTestdata, "*.yml", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(actionlintTestdata, "*.yaml", SearchOption.AllDirectories))
			.Where(static f =>
			{
				var n = f.Replace('\\', '/');
				return n.Contains("/testdata/err/", StringComparison.OrdinalIgnoreCase)
					|| n.Contains("/broken/", StringComparison.OrdinalIgnoreCase)
					|| n.Contains("broken_yaml", StringComparison.OrdinalIgnoreCase)
					|| n.Contains("dangling_alias", StringComparison.OrdinalIgnoreCase);
			})
			.ToArray();

		await Assert.That(files.Length).IsGreaterThan(0);

		var failedCount = 0;
		foreach (var file in files)
		{
			try
			{
				_ = WorkflowParser.Parse(File.ReadAllBytes(file), file);
			}
			catch
			{
				failedCount++;
			}
		}

		await Assert.That(failedCount).IsGreaterThan(0);
	}

	[Test]
	public async Task Schema_Corpus_JsonFilesAreValid()
	{
		var root = FindRepoRoot();
		var candidates = new[]
		{
			Path.Combine(root, ".references", "ghalint-main", "json-schema", "ghalint.json"),
			Path.Combine(root, ".references", "zizmor-main", "crates", "zizmor", "src", "data", "github-workflow.json"),
			Path.Combine(root, ".references", "zizmor-main", "crates", "zizmor", "src", "data", "github-action.json"),
			Path.Combine(root, ".references", "zizmor-main", "crates", "zizmor", "src", "data", "dependabot-2.0.json"),
		};

		var existing = candidates.Where(File.Exists).ToArray();
		await Assert.That(existing.Length).IsGreaterThan(0);

		var invalid = new List<string>();
		foreach (var path in existing)
		{
			try
			{
				using var _ = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
			}
			catch (Exception ex)
			{
				invalid.Add($"{path}: {ex.Message}");
			}
		}

		await Assert.That(invalid).IsEmpty();
	}

	[Test]
	public async Task Parse_JobMissingRunsOn_ReportsError()
	{
		var yaml = "on: push\njobs:\n  build:\n    steps:\n      - run: echo hello\n";

		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-missing-runs-on.yml");
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("requires runs-on", StringComparison.Ordinal))).IsTrue();
	}

	[Test]
	public async Task Parse_JobWithUsesAndSteps_ReportsError()
	{
		var yaml = "on: push\njobs:\n  reuse:\n    uses: owner/repo/.github/workflows/reuse.yml@main\n    steps:\n      - run: echo hello\n";

		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-uses-steps.yml");
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("cannot have both uses and steps", StringComparison.Ordinal))).IsTrue();
	}

	[Test]
	public async Task Parse_StepWithoutRunOrUses_ReportsError()
	{
		var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - name: only-name\n";

		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-missing-run-uses.yml");
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("requires run or uses", StringComparison.Ordinal))).IsTrue();
	}

	[Test]
	public async Task Parse_StepWithRunAndUses_ReportsError()
	{
		var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n        uses: actions/checkout@v4\n";

		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-run-uses.yml");
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("cannot have both run and uses", StringComparison.Ordinal))).IsTrue();
	}

	[Test]
	public async Task Parse_JobMustBeMapping_ReportsError()
	{
		var yaml = "on: push\njobs:\n  build: []\n";

		var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-mapping.yml");
		await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("must be mapping", StringComparison.Ordinal))).IsTrue();
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

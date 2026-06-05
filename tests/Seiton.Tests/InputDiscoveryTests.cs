using Seiton.Cli;
using Seiton.Commands;

namespace Seiton.Tests;

public sealed class InputDiscoveryTests
{
    private static readonly VerboseLogger SilentLogger = VerboseLogger.Create(verbose: false, TextWriter.Null);

    [Test]
    public async Task ResolveFiles_NestedCiLayout_DoesNotIncludeParentActions()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteYaml(root, ".github/actions/parent-action/action.yaml", "name: parent\nruns: { using: composite, steps: [] }");
            var child = Path.Combine(root, "LogicLooper");
            WriteYaml(child, ".github/workflows/build.yaml", "on: push\njobs: { build: { runs-on: ubuntu-24.04, steps: [] } }");

            var files = InputDiscovery.ResolveFiles(
                [],
                includeActions: true,
                SilentLogger,
                startDirectory: child);

            await Assert.That(files).Count().IsEqualTo(1);
            await Assert.That(files[0]).IsEqualTo(Path.GetFullPath(Path.Combine(child, ".github/workflows/build.yaml")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ResolveFiles_NestedCiLayout_DoesNotIncludeParentWorkflows()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteYaml(root, ".github/workflows/parent.yaml", "on: push\njobs: { build: { runs-on: ubuntu-24.04, steps: [] } }");
            var child = Path.Combine(root, "LogicLooper");
            Directory.CreateDirectory(child);

            var files = InputDiscovery.ResolveFiles(
                [],
                includeActions: false,
                SilentLogger,
                startDirectory: child);

            await Assert.That(files).IsEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ResolveFiles_SubdirectoryWithoutGitHub_DoesNotWalkToParentWorkflows()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteYaml(root, ".github/workflows/ci.yaml", "on: push\njobs: { build: { runs-on: ubuntu-24.04, steps: [] } }");
            var subdir = Path.Combine(root, "packages", "foo");
            Directory.CreateDirectory(subdir);

            var files = InputDiscovery.ResolveFiles(
                [],
                includeActions: false,
                SilentLogger,
                startDirectory: subdir);

            await Assert.That(files).IsEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ResolveFiles_CwdHasWorkflowsAndActions_IncludesBothUnderCwd()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteYaml(root, ".github/workflows/ci.yaml", "on: push\njobs: { build: { runs-on: ubuntu-24.04, steps: [] } }");
            WriteYaml(root, ".github/actions/my-action/action.yaml", "name: my\nruns: { using: composite, steps: [] }");

            var files = InputDiscovery.ResolveFiles(
                [],
                includeActions: true,
                SilentLogger,
                startDirectory: root);

            await Assert.That(files).Count().IsEqualTo(2);
            await Assert.That(files.Any(path => path.EndsWith($"{Path.DirectorySeparatorChar}ci.yaml", StringComparison.OrdinalIgnoreCase))).IsTrue();
            await Assert.That(files.Any(path => path.Contains($"{Path.DirectorySeparatorChar}.github{Path.DirectorySeparatorChar}actions{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ResolveFiles_ParentAlsoHasWorkflows_UsesOnlyCwdWorkflows()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteYaml(root, ".github/workflows/parent.yaml", "on: push\njobs: { build: { runs-on: ubuntu-24.04, steps: [] } }");
            var child = Path.Combine(root, "child");
            WriteYaml(child, ".github/workflows/child.yaml", "on: push\njobs: { build: { runs-on: ubuntu-24.04, steps: [] } }");

            var files = InputDiscovery.ResolveFiles(
                [],
                includeActions: false,
                SilentLogger,
                startDirectory: child);

            await Assert.That(files).Count().IsEqualTo(1);
            await Assert.That(files[0]).IsEqualTo(Path.GetFullPath(Path.Combine(child, ".github/workflows/child.yaml")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ShouldSuggestIncludeActions_ParentActionsOnly_ReturnsFalse()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".github", "actions", "parent-action"));
            var child = Path.Combine(root, "LogicLooper");
            Directory.CreateDirectory(child);

            var shouldSuggest = CheckCommand.ShouldSuggestIncludeActions(includeActions: false, discoveryStartDirectory: child);
            await Assert.That(shouldSuggest).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteYaml(string repositoryRoot, string relativePath, string content)
    {
        var fullPath = Path.Combine(repositoryRoot, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }
}

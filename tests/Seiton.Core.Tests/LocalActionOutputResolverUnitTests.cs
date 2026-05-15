using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class LocalActionOutputResolverUnitTests
{
    [Test]
    public async Task ResolveOutputNames_EquivalentGithubActionPaths_UsesCachedResolvedOutputs()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var actionDirectory = Path.Combine(repositoryRoot, ".github", "actions", "sample");
            Directory.CreateDirectory(actionDirectory);
            File.WriteAllText(Path.Combine(actionDirectory, "action.yml"), BuildActionYaml("cached_output"));

            var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");
            var resolver = new LocalActionOutputResolver(workflowPath);

            var first = resolver.ResolveOutputNames("./.github/actions/sample"u8);
            await Assert.That(first).IsNotNull();
            await Assert.That(first!).Count().IsEqualTo(1);
            await Assert.That(first[0]).IsEqualTo("cached_output");

            File.Delete(Path.Combine(actionDirectory, "action.yml"));

            var second = resolver.ResolveOutputNames("././.github/actions/sample"u8);
            await Assert.That(second).IsNotNull();
            await Assert.That(second!).Count().IsEqualTo(1);
            await Assert.That(second[0]).IsEqualTo("cached_output");
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    [Test]
    public async Task ResolveOutputNames_NonAsciiGithubActionPath_ResolvesUtf8Path()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var actionDirectory = Path.Combine(repositoryRoot, ".github", "actions", "日本語");
            Directory.CreateDirectory(actionDirectory);
            File.WriteAllText(Path.Combine(actionDirectory, "action.yml"), BuildActionYaml("unicode_output"));

            var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");
            var resolver = new LocalActionOutputResolver(workflowPath);

            var resolved = resolver.ResolveOutputNames("./.github/actions/日本語"u8);

            await Assert.That(resolved).IsNotNull();
            await Assert.That(resolved!).Count().IsEqualTo(1);
            await Assert.That(resolved[0]).IsEqualTo("unicode_output");
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    [Test]
    public async Task ResolveOutputNames_PathTraversal_ReturnsNull()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");
            var resolver = new LocalActionOutputResolver(workflowPath);

            var result = resolver.ResolveOutputNames("./../../../../../../etc/passwd"u8);

            await Assert.That(result).IsNull();
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    [Test]
    public async Task ResolveOutputNames_RelativeParentWithinRepo_NotBlockedByTraversalCheck()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var actionDirectory = Path.Combine(repositoryRoot, ".github", "actions", "foo");
            Directory.CreateDirectory(actionDirectory);
            File.WriteAllText(Path.Combine(actionDirectory, "action.yml"), BuildActionYaml("parent_output"));

            var nestedWorkflowDirectory = Path.Combine(repositoryRoot, ".github", "workflows", "nested");
            Directory.CreateDirectory(nestedWorkflowDirectory);
            var workflowPath = Path.Combine(nestedWorkflowDirectory, "caller.yml");
            var resolver = new LocalActionOutputResolver(workflowPath);

            var result = resolver.ResolveOutputNames("../../actions/foo"u8);

            await Assert.That(result).IsNotNull();
            await Assert.That(result!).Count().IsEqualTo(1);
            await Assert.That(result[0]).IsEqualTo("parent_output");
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    private static string BuildActionYaml(string outputName)
    {
        return $$"""
            name: Sample Action
            description: Sample description
            outputs:
              {{outputName}}:
                description: Example output
            runs:
              using: composite
              steps:
                - run: echo ok
                  shell: bash
            """;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

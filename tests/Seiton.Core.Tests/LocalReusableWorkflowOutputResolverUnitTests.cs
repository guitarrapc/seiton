using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class LocalReusableWorkflowOutputResolverUnitTests
{
    [Test]
    public async Task ResolveOutputNames_EquivalentWorkflowPaths_UsesCachedResolvedOutputs()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var workflowDirectory = Path.Combine(repositoryRoot, ".github", "workflows");
            Directory.CreateDirectory(workflowDirectory);
            var reusableWorkflowPath = Path.Combine(workflowDirectory, "reusable.yml");
            File.WriteAllText(reusableWorkflowPath, BuildReusableWorkflowYaml("published_value"));

            var callerPath = Path.Combine(workflowDirectory, "caller.yml");
            var resolver = new LocalReusableWorkflowOutputResolver(callerPath);

            var first = resolver.ResolveOutputNames("./.github/workflows/reusable.yml"u8);
            await Assert.That(first).IsNotNull();
            await Assert.That(first!).Count().IsEqualTo(1);
            await Assert.That(first[0]).IsEqualTo("published_value");

            File.Delete(reusableWorkflowPath);

            var second = resolver.ResolveOutputNames("././.github/workflows/reusable.yml"u8);
            await Assert.That(second).IsNotNull();
            await Assert.That(second!).Count().IsEqualTo(1);
            await Assert.That(second[0]).IsEqualTo("published_value");
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    [Test]
    public async Task ResolveOutputNames_NonAsciiWorkflowPath_ResolvesUtf8Path()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var workflowDirectory = Path.Combine(repositoryRoot, ".github", "workflows");
            Directory.CreateDirectory(workflowDirectory);
            File.WriteAllText(Path.Combine(workflowDirectory, "再利用.yml"), BuildReusableWorkflowYaml("unicode_value"));

            var callerPath = Path.Combine(workflowDirectory, "caller.yml");
            var resolver = new LocalReusableWorkflowOutputResolver(callerPath);

            var resolved = resolver.ResolveOutputNames("./.github/workflows/再利用.yml"u8);

            await Assert.That(resolved).IsNotNull();
            await Assert.That(resolved!).Count().IsEqualTo(1);
            await Assert.That(resolved[0]).IsEqualTo("unicode_value");
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    [Test]
    public async Task ResolveOutputNames_InvalidPath_ReturnsNull()
    {
        var resolver = new LocalReusableWorkflowOutputResolver("/tmp/repo/.github/workflows/caller.yml");
        var resolved = resolver.ResolveOutputNames("./\0.yml"u8);

        await Assert.That(resolved).IsNull();
    }

    private static string BuildReusableWorkflowYaml(string outputName)
    {
                return $$$"""
            on:
              workflow_call:
                outputs:
                                    {{{outputName}}}:
                    value: ${{ jobs.example.outputs.value }}
            jobs:
              example:
                runs-on: ubuntu-latest
                outputs:
                  value: done
                steps:
                  - run: echo ok
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
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Failed to delete test directory '{path}': {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Failed to delete test directory '{path}': {ex}");
        }
    }
}

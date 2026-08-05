using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class ActionRefHelpersTests
{
    [Test]
    public async Task SelfRepositoryReference_ResolvesFromRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");

        var baseDirectory = ActionRefHelpers.ResolveLocalReferenceBaseDirectory(workflowPath, "$/.github/actions/sample");
        var resolved = ActionRefHelpers.NormalizeFullPath(baseDirectory, "$/.github/actions/sample");
        var expected = ActionRefHelpers.NormalizePath(Path.Combine(repositoryRoot, ".github", "actions", "sample"));

        await Assert.That(baseDirectory).IsEqualTo(ActionRefHelpers.NormalizePath(repositoryRoot));
        await Assert.That(resolved).IsEqualTo(expected);
    }

    [Test]
    public async Task SelfRepositoryReference_PathTraversal_ReturnsNull()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var resolved = ActionRefHelpers.NormalizeFullPath(repositoryRoot, "$/../outside");

        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task TryParseRemoteUses_SelfRepositoryReference_ReturnsFalse()
    {
        var parsed = ActionRefHelpers.TryParseRemoteUses("$/.github/actions/sample@v1"u8, out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task ResolveLocalReferenceBaseDirectory_SlashNormalizedGithubPath_UsesRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");

        var baseDirectory = ActionRefHelpers.ResolveLocalReferenceBaseDirectory(workflowPath, "./.github/actions/sample");

        await Assert.That(baseDirectory).IsEqualTo(ActionRefHelpers.NormalizePath(repositoryRoot));
        await Assert.That(baseDirectory).DoesNotContain("\\");
    }

    [Test]
    public async Task NormalizeFullPath_ReturnsSlashNormalizedAbsolutePath()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var normalized = ActionRefHelpers.NormalizeFullPath(ActionRefHelpers.NormalizePath(repositoryRoot), "./.github/workflows/reusable.yml");
        var expected = ActionRefHelpers.NormalizePath(Path.GetFullPath(Path.Combine(repositoryRoot, ".github", "workflows", "reusable.yml")));

        await Assert.That(normalized).IsEqualTo(expected);
        await Assert.That(normalized).IsNotNull();
        await Assert.That(normalized!).DoesNotContain("\\");
    }

    [Test]
    public async Task TryGetRepositoryRoot_FromWorkflowPath_ReturnsRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");

        var found = ActionRefHelpers.TryGetRepositoryRoot(workflowPath, out var resolvedRoot);

        await Assert.That(found).IsTrue();
        await Assert.That(resolvedRoot).IsEqualTo(ActionRefHelpers.NormalizePath(repositoryRoot));
    }

    [Test]
    public async Task TryGetRepositoryRoot_FromCompositeActionPath_ReturnsRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var actionPath = Path.Combine(repositoryRoot, ".github", "actions", "git-push", "action.yaml");

        var found = ActionRefHelpers.TryGetRepositoryRoot(actionPath, out var resolvedRoot);

        await Assert.That(found).IsTrue();
        await Assert.That(resolvedRoot).IsEqualTo(ActionRefHelpers.NormalizePath(repositoryRoot));
    }

    [Test]
    public async Task TryGetRepositoryRoot_FromCompositeActionOutsideGithub_ReturnsGitRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
            var actionPath = Path.Combine(repositoryRoot, "actions", "git-push", "action.yaml");

            var found = ActionRefHelpers.TryGetRepositoryRoot(actionPath, out var resolvedRoot);

            await Assert.That(found).IsTrue();
            await Assert.That(resolvedRoot).IsEqualTo(ActionRefHelpers.NormalizePath(repositoryRoot));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Test]
    public async Task TryGetRepositoryRoot_FromNestedGithubDirectory_PrefersGitRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
            var actionPath = Path.Combine(repositoryRoot, "actions", "parent", ".github", "actions", "child", "action.yml");

            var found = ActionRefHelpers.TryGetRepositoryRoot(actionPath, out var resolvedRoot);

            await Assert.That(found).IsTrue();
            await Assert.That(resolvedRoot).IsEqualTo(ActionRefHelpers.NormalizePath(repositoryRoot));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Test]
    public async Task TryGetRepositoryRoot_FromWorktreeGitFile_ReturnsRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(repositoryRoot);
            File.WriteAllText(Path.Combine(repositoryRoot, ".git"), "gitdir: ../worktrees/sample");
            var actionPath = Path.Combine(repositoryRoot, "actions", "sample", "action.yml");

            var found = ActionRefHelpers.TryGetRepositoryRoot(actionPath, out var resolvedRoot);

            await Assert.That(found).IsTrue();
            await Assert.That(resolvedRoot).IsEqualTo(ActionRefHelpers.NormalizePath(repositoryRoot));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Test]
    public async Task NormalizeFullPath_SelfRepositoryReferenceThroughSymlink_ReturnsNull()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var repositoryRoot = Path.Combine(testRoot, "repo");
        var outsideRoot = Path.Combine(testRoot, "outside");
        try
        {
            Directory.CreateDirectory(repositoryRoot);
            Directory.CreateDirectory(outsideRoot);
            var linkPath = Path.Combine(repositoryRoot, "linked");
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideRoot);
            }
            catch (UnauthorizedAccessException)
            {
                Skip.Test("Creating symbolic links is not permitted in this environment.");
            }
            catch (PlatformNotSupportedException)
            {
                Skip.Test("Symbolic links are not supported on this platform.");
            }

            var resolved = ActionRefHelpers.NormalizeFullPath(repositoryRoot, "$/linked/action");

            await Assert.That(resolved).IsNull();
        }
        finally
        {
            var linkPath = Path.Combine(repositoryRoot, "linked");
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Test]
    public async Task FileSystemPathComparer_MatchesOperatingSystemCaseSensitivity()
    {
        var equals = ActionRefHelpers.FileSystemPathComparer.Equals("Action.yml", "action.yml");

        await Assert.That(equals).IsEqualTo(OperatingSystem.IsWindows());
    }

    [Test]
    public async Task ResolveLocalReferenceBaseDirectory_FromCompositeActionPath_UsesRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var actionPath = Path.Combine(repositoryRoot, ".github", "actions", "git-push", "action.yaml");

        var baseDirectory = ActionRefHelpers.ResolveLocalReferenceBaseDirectory(actionPath, "./.github/actions/signed-commit");

        await Assert.That(baseDirectory).IsEqualTo(ActionRefHelpers.NormalizePath(repositoryRoot));
    }

    [Test]
    public async Task NormalizeFullPath_FromCompositeActionPath_ResolvesSiblingLocalAction()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var actionPath = Path.Combine(repositoryRoot, ".github", "actions", "git-push", "action.yaml");
        var baseDirectory = ActionRefHelpers.ResolveLocalReferenceBaseDirectory(actionPath, "./.github/actions/signed-commit");

        var resolved = ActionRefHelpers.NormalizeFullPath(baseDirectory, "./.github/actions/signed-commit");
        var expected = ActionRefHelpers.NormalizePath(Path.GetFullPath(Path.Combine(repositoryRoot, ".github", "actions", "signed-commit")));

        await Assert.That(resolved).IsEqualTo(expected);
    }

    [Test]
    public async Task ResolveLocalReferenceBaseDirectory_NonGithubRelativePath_UsesFileDirectory()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var actionPath = Path.Combine(repositoryRoot, ".github", "actions", "git-push", "action.yaml");

        var baseDirectory = ActionRefHelpers.ResolveLocalReferenceBaseDirectory(actionPath, "./signed-commit");

        await Assert.That(baseDirectory).IsEqualTo(ActionRefHelpers.NormalizePath(Path.GetDirectoryName(actionPath)!));
    }

    [Test]
    public async Task ResolveLocalReferenceBaseDirectory_GithubRelativePathWithoutGithubSegment_UsesFileDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var sourceFilePath = Path.Combine(root, "scripts", "lint-target.yaml");
        var fileDirectory = ActionRefHelpers.NormalizePath(Path.GetDirectoryName(sourceFilePath)!);

        var baseDirectory = ActionRefHelpers.ResolveLocalReferenceBaseDirectory(sourceFilePath, "./.github/actions/signed-commit");

        await Assert.That(baseDirectory).IsEqualTo(fileDirectory);
    }
}

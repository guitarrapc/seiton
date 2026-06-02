using Seiton.Output;

namespace Seiton.Tests;

public sealed class PathDisplayResolverTests
{
    [Test]
    public async Task GetDisplayPath_AbsoluteUnderBase_EmitsRelativeWithForwardSlashes()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "seiton-path-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var target = Path.Combine(baseDir, ".github", "workflows", "ci.yml");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, "on: push\n");

            var resolver = new PathDisplayResolver(baseDir);
            var display = resolver.GetDisplayPath(target);

            await Assert.That(display).IsEqualTo(".github/workflows/ci.yml");
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Test]
    public async Task GetDisplayPath_AlreadyRelative_ReturnsNormalizedForwardSlashes()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "seiton-path-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var resolver = new PathDisplayResolver(baseDir);
            var display = resolver.GetDisplayPath(@".github\workflows\ci.yml");

            await Assert.That(display).IsEqualTo(".github/workflows/ci.yml");
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Test]
    public async Task GetDisplayPath_NullOrUnknown_PreservesSentinel()
    {
        var resolver = new PathDisplayResolver(Environment.CurrentDirectory);

        await Assert.That(resolver.GetDisplayPath(null)).IsEqualTo("<unknown>");
        await Assert.That(resolver.GetDisplayPath("<unknown>")).IsEqualTo("<unknown>");
        await Assert.That(resolver.GetDisplayPath("<stdin>")).IsEqualTo("<stdin>");
    }

    [Test]
    public async Task ResolveSarifArtifactLocation_RelativeUnderBase_EmitsUriBaseIdAndRelativeUri()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "seiton-path-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var target = Path.Combine(baseDir, ".github", "workflows", "ci with space.yml");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, "on: push\n");

            var resolver = new PathDisplayResolver(baseDir);
            var location = resolver.ResolveSarifArtifactLocation(target);

            await Assert.That(location.UriBaseId).IsEqualTo(PathDisplayResolver.SarifWorkingDirectoryBaseId);
            await Assert.That(location.Uri).IsEqualTo(".github/workflows/ci%20with%20space.yml");
            await Assert.That(resolver.SarifBaseUri).IsNotNull();
            await Assert.That(resolver.CreateOriginalUriBaseIds()).IsNotNull();
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Test]
    public async Task ResolveSarifArtifactLocation_UnknownPath_UsesSafeFileUri()
    {
        var resolver = new PathDisplayResolver(Environment.CurrentDirectory);
        var location = resolver.ResolveSarifArtifactLocation(null);

        await Assert.That(location.Uri).IsEqualTo("file:///unknown");
        await Assert.That(location.UriBaseId).IsNull();
    }

    [Test]
    public async Task ResolveSarifArtifactLocation_StdinSentinel_EmitsLiteralUriWithoutBaseId()
    {
        var resolver = new PathDisplayResolver(Environment.CurrentDirectory);
        var location = resolver.ResolveSarifArtifactLocation("<stdin>");

        await Assert.That(location.Uri).IsEqualTo("<stdin>");
        await Assert.That(location.UriBaseId).IsNull();
        await Assert.That(resolver.CreateOriginalUriBaseIds()).IsNull();
    }

    [Test]
    public async Task ResolveSarifArtifactLocation_HyphenSentinel_EmitsLiteralUriWithoutBaseId()
    {
        var resolver = new PathDisplayResolver(Environment.CurrentDirectory);
        var location = resolver.ResolveSarifArtifactLocation("-");

        await Assert.That(location.Uri).IsEqualTo("-");
        await Assert.That(location.UriBaseId).IsNull();
        await Assert.That(resolver.CreateOriginalUriBaseIds()).IsNull();
    }

    [Test]
    public async Task CreateOriginalUriBaseIds_NoRelativeArtifacts_ReturnsNull()
    {
        var resolver = new PathDisplayResolver(Environment.CurrentDirectory);
        _ = resolver.ResolveSarifArtifactLocation("https://example.com/repo/workflow.yml");

        await Assert.That(resolver.CreateOriginalUriBaseIds()).IsNull();
    }

    [Test]
    public async Task ResolveSarifArtifactLocation_CrossDrive_FallsBackToAbsoluteUriWithoutBaseId()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var resolver = new PathDisplayResolver(@"C:\repo");
        var location = resolver.ResolveSarifArtifactLocation(@"Z:\other\workflow.yml");

        await Assert.That(location.Uri).StartsWith("file:///");
        await Assert.That(location.Uri).Contains("/Z:/other/workflow.yml");
        await Assert.That(location.UriBaseId).IsNull();
    }

    [Test]
    public async Task ResolveSarifArtifactLocation_UriLikeButInvalid_DoesNotAddBaseId()
    {
        var resolver = new PathDisplayResolver(Environment.CurrentDirectory);
        var location = resolver.ResolveSarifArtifactLocation("http:not-a-uri");

        await Assert.That(location.Uri).IsEqualTo("http:not-a-uri");
        await Assert.That(location.UriBaseId).IsNull();
        await Assert.That(resolver.CreateOriginalUriBaseIds()).IsNull();
    }

    [Test]
    public async Task ResolveSarifArtifactLocation_InvalidFilesystemPath_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var resolver = new PathDisplayResolver(Environment.CurrentDirectory);
        var location = resolver.ResolveSarifArtifactLocation("D:\\a\u0000b");

        await Assert.That(location.Uri).IsEqualTo(PathDisplayResolver.UnknownSarifFileUri);
        await Assert.That(location.UriBaseId).IsNull();
    }

    [Test]
    public async Task GetDisplayPath_InvalidFilesystemPath_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var invalidPath = "D:\\a\u0000b";
        var resolver = new PathDisplayResolver(Environment.CurrentDirectory);
        var display = resolver.GetDisplayPath(invalidPath);

        await Assert.That(display).IsEqualTo(NormalizeExpectedDisplayPath(invalidPath));
    }

    private static string NormalizeExpectedDisplayPath(string path) =>
        path.AsSpan().IndexOf('\\') >= 0 ? path.Replace('\\', '/') : path;

    [Test]
    public async Task GetDisplayPath_CachesRepeatedLookups()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "seiton-path-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var target = Path.Combine(baseDir, "workflow.yml");
            await File.WriteAllTextAsync(target, "on: push\n");

            var resolver = new PathDisplayResolver(baseDir);
            var first = resolver.GetDisplayPath(target);
            var second = resolver.GetDisplayPath(target);

            await Assert.That(first).IsEqualTo("workflow.yml");
            await Assert.That(second).IsEqualTo(first);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }
}

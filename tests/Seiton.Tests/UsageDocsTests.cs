namespace Seiton.Tests;

public sealed class UsageDocsTests
{
    [Test]
    public async Task UsageMd_ExitCodesSection_DocumentsWarningsOnlyExitAndMinSeverityForCi()
    {
        var usagePath = Path.Combine(FindRepoRoot(), "docs", "usage.md");
        await Assert.That(File.Exists(usagePath)).IsTrue();

        var usage = File.ReadAllText(usagePath);
        await Assert.That(usage).Contains("warnings alone still produce exit code");
        await Assert.That(usage).Contains("--min-severity error");
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "seiton.slnx")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}

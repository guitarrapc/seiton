using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Tests;

public sealed class PinFixFormatterTests
{
    [Test]
    public async Task BuildActionsShaFix_ReturnsTextEdit_WithExpectedOffsetAndLength()
    {
        var yaml = "steps:\n  - uses: actions/checkout@v4\n";
        var source = Encoding.UTF8.GetBytes(yaml);
        var oldRef = "actions/checkout@v4";
        var diagnostic = new Diagnostic(
            DiagnosticSeverity.Warning,
            "'actions/checkout@v4' is not pinned to a full-length commit SHA",
            new TextRange(0, source.Length, 1, 1, 2, 32),
            RuleId: "unpinned-uses",
            Metadata: PinDiagnosticMetadata.ForUsesRef(oldRef));

        var sha = "0123456789abcdef0123456789abcdef01234567";
        var fix = PinFixFormatter.BuildActionsShaFix(diagnostic, sha, "v4", source);

        await Assert.That(fix.HasValue).IsTrue();
        await Assert.That(fix!.Value.Edits.Length).IsEqualTo(1);
        await Assert.That(fix.Value.Edits[0].Offset).IsEqualTo(yaml.IndexOf(oldRef, StringComparison.Ordinal));
        await Assert.That(fix.Value.Edits[0].Length).IsEqualTo(oldRef.Length);
        await Assert.That(fix.Value.Edits[0].NewText).IsEqualTo($"actions/checkout@{sha} # v4");
    }

    [Test]
    public async Task BuildImageDigestFix_ReturnsTextEdit_WithExpectedOffsetAndLength()
    {
        var yaml = "steps:\n  - uses: docker://ghcr.io/astral-sh/uv:latest\n";
        var source = Encoding.UTF8.GetBytes(yaml);
        var oldRef = "docker://ghcr.io/astral-sh/uv:latest";
        var diagnostic = new Diagnostic(
            DiagnosticSeverity.Warning,
            "'docker://ghcr.io/astral-sh/uv:latest' is not pinned by digest (expected @sha256:<64-hex>)",
            new TextRange(0, source.Length, 1, 1, 2, 60),
            RuleId: "unpinned-image",
            Metadata: PinDiagnosticMetadata.ForImageRef(oldRef));

        var digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var fix = PinFixFormatter.BuildImageDigestFix(diagnostic, digest, source);

        await Assert.That(fix.HasValue).IsTrue();
        await Assert.That(fix!.Value.Edits.Length).IsEqualTo(1);
        await Assert.That(fix.Value.Edits[0].Offset).IsEqualTo(yaml.IndexOf(oldRef, StringComparison.Ordinal));
        await Assert.That(fix.Value.Edits[0].Length).IsEqualTo(oldRef.Length);
        await Assert.That(fix.Value.Edits[0].NewText).IsEqualTo(oldRef + "@" + digest);
    }

    [Test]
    public async Task BuildActionsShaFix_ReturnsNull_WhenUsesRefMetadataMissing()
    {
        var yaml = "steps:\n  - uses: actions/checkout@v4\n";
        var source = Encoding.UTF8.GetBytes(yaml);
        var diagnostic = new Diagnostic(
            DiagnosticSeverity.Warning,
            "'actions/checkout@v4' is not pinned to a full-length commit SHA",
            new TextRange(0, source.Length, 1, 1, 2, 32),
            RuleId: "unpinned-uses");

        var fix = PinFixFormatter.BuildActionsShaFix(diagnostic, "0123456789abcdef0123456789abcdef01234567", "v4", source);

        await Assert.That(fix.HasValue).IsFalse();
    }

    [Test]
    public async Task BuildActionsShaFix_ReturnsNull_WhenAlreadyPinnedBySha40()
    {
        var sha = "0123456789abcdef0123456789abcdef01234567";
        var yaml = $"steps:\n  - uses: actions/checkout@{sha}\n";
        var source = Encoding.UTF8.GetBytes(yaml);
        var diagnostic = new Diagnostic(
            DiagnosticSeverity.Warning,
            $"'actions/checkout@{sha}' is not pinned to a full-length commit SHA",
            new TextRange(0, source.Length, 1, 1, 2, 70),
            RuleId: "unpinned-uses",
            Metadata: PinDiagnosticMetadata.ForUsesRef($"actions/checkout@{sha}"));

        var fix = PinFixFormatter.BuildActionsShaFix(diagnostic, sha, "v4", source);

        await Assert.That(fix.HasValue).IsFalse();
    }

    [Test]
    public async Task BuildActionsShaFix_DuplicateUses_EachDiagnosticGetsDistinctOffset()
    {
        var yaml = """
            on: push
            jobs:
              dependabot:
                steps:
                  - uses: actions/github-script@v9
                    id: check
              external:
                steps:
                  - uses: actions/github-script@v9
                    id: check
            """;
        var source = Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n", StringComparison.Ordinal));
        var lintEngine = new LintEngine([new UnpinnedUsesRule()]);
        using var lintResult = lintEngine.Check(source, "duplicate-uses.yml");

        var unpinned = lintResult.Diagnostics
            .Where(d => d.RuleId == "unpinned-uses")
            .ToArray();
        await Assert.That(unpinned.Length).IsEqualTo(2);

        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var fixes = new DiagnosticFix?[unpinned.Length];
        for (var i = 0; i < unpinned.Length; i++)
        {
            fixes[i] = PinFixFormatter.BuildActionsShaFix(unpinned[i], sha, "v9", source);
            await Assert.That(fixes[i].HasValue).IsTrue();
        }

        await Assert.That(fixes[0]!.Value.Edits[0].Offset)
            .IsNotEqualTo(fixes[1]!.Value.Edits[0].Offset);

        var pinnedDiagnostics = new Diagnostic[unpinned.Length];
        for (var i = 0; i < unpinned.Length; i++)
        {
            pinnedDiagnostics[i] = unpinned[i] with { Fix = fixes[i] };
        }

        var updated = FixEngine.Apply(source, pinnedDiagnostics);
        var updatedYaml = Encoding.UTF8.GetString(updated);
        var expectedPin = $"actions/github-script@{sha} # v9";
        await Assert.That(updatedYaml.Split(expectedPin, StringSplitOptions.None).Length - 1).IsEqualTo(2);
    }

    [Test]
    public async Task TryFindReplacementOffset_ContainingAnchor_FindsCorrectOccurrence()
    {
        var yaml = "aaa actions/checkout@v4 bbb actions/checkout@v4 ccc";
        var source = Encoding.UTF8.GetBytes(yaml);
        var oldBytes = Encoding.UTF8.GetBytes("actions/checkout@v4");
        var secondAnchor = yaml.IndexOf("@v4", yaml.IndexOf("@v4", StringComparison.Ordinal) + 1, StringComparison.Ordinal);

        await Assert.That(PinFixFormatter.TryFindReplacementOffset(source, oldBytes, secondAnchor, out var offset)).IsTrue();
        await Assert.That(offset).IsEqualTo(yaml.LastIndexOf("actions/checkout@v4", StringComparison.Ordinal));
    }

    [Test]
    public async Task TryFindReplacementOffset_AnchorBeforeValue_ForwardFallbackFindsMatch()
    {
        var yaml = "uses: actions/checkout@v4";
        var source = Encoding.UTF8.GetBytes(yaml);
        var oldBytes = Encoding.UTF8.GetBytes("actions/checkout@v4");
        var anchor = yaml.IndexOf("uses", StringComparison.Ordinal);

        await Assert.That(PinFixFormatter.TryFindReplacementOffset(source, oldBytes, anchor, out var offset)).IsTrue();
        await Assert.That(offset).IsEqualTo(yaml.IndexOf("actions/checkout@v4", StringComparison.Ordinal));
    }

    [Test]
    public async Task TryFindReplacementOffset_NoMatch_ReturnsFalse()
    {
        var source = Encoding.UTF8.GetBytes("uses: actions/checkout@v4");
        var oldBytes = Encoding.UTF8.GetBytes("actions/setup-node@v4");

        await Assert.That(PinFixFormatter.TryFindReplacementOffset(source, oldBytes, anchorOffset: 0, out _)).IsFalse();
    }

    [Test]
    public async Task BuildImageDigestFix_ReturnsNull_WhenAlreadyPinnedByDigest()
    {
        var digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var yaml = $"steps:\n  - uses: docker://ghcr.io/astral-sh/uv@{digest}\n";
        var source = Encoding.UTF8.GetBytes(yaml);
        var diagnostic = new Diagnostic(
            DiagnosticSeverity.Warning,
            $"'docker://ghcr.io/astral-sh/uv@{digest}' is not pinned by digest (expected @sha256:<64-hex>)",
            new TextRange(0, source.Length, 1, 1, 2, 120),
            RuleId: "unpinned-image",
            Metadata: PinDiagnosticMetadata.ForImageRef($"docker://ghcr.io/astral-sh/uv@{digest}"));

        var fix = PinFixFormatter.BuildImageDigestFix(diagnostic, digest, source);

        await Assert.That(fix.HasValue).IsFalse();
    }
}

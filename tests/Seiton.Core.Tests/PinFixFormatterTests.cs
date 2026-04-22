using Seiton.Core.Linting.PinRemediation;
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
            "action uses 'actions/checkout@v4' is not pinned to a full-length commit SHA",
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
            "docker action uses 'docker://ghcr.io/astral-sh/uv:latest' is not pinned by digest (expected @sha256:<64-hex>)",
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
            "action uses 'actions/checkout@v4' is not pinned to a full-length commit SHA",
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
            $"action uses 'actions/checkout@{sha}' is not pinned to a full-length commit SHA",
            new TextRange(0, source.Length, 1, 1, 2, 70),
            RuleId: "unpinned-uses",
            Metadata: PinDiagnosticMetadata.ForUsesRef($"actions/checkout@{sha}"));

        var fix = PinFixFormatter.BuildActionsShaFix(diagnostic, sha, "v4", source);

        await Assert.That(fix.HasValue).IsFalse();
    }

    [Test]
    public async Task BuildImageDigestFix_ReturnsNull_WhenAlreadyPinnedByDigest()
    {
        var digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var yaml = $"steps:\n  - uses: docker://ghcr.io/astral-sh/uv@{digest}\n";
        var source = Encoding.UTF8.GetBytes(yaml);
        var diagnostic = new Diagnostic(
            DiagnosticSeverity.Warning,
            $"docker action uses 'docker://ghcr.io/astral-sh/uv@{digest}' is not pinned by digest (expected @sha256:<64-hex>)",
            new TextRange(0, source.Length, 1, 1, 2, 120),
            RuleId: "unpinned-image",
            Metadata: PinDiagnosticMetadata.ForImageRef($"docker://ghcr.io/astral-sh/uv@{digest}"));

        var fix = PinFixFormatter.BuildImageDigestFix(diagnostic, digest, source);

        await Assert.That(fix.HasValue).IsFalse();
    }
}

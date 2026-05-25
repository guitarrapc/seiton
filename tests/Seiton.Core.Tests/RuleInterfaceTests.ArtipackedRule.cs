using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_ArtipackedRule_TableDriven()
    {
        var cases = new[]
        {
            // Case 1: checkout (no persist-credentials) + upload-artifact v4 (path: ., include-hidden-files: true) → error
            new RuleCase(
            "ng-checkout-upload-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Case 2: checkout (persist-credentials: false) + upload-artifact (path: .) → OK
            new RuleCase(
            "ok-checkout-persist-false",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
            """,
            []),
            // Case 3: checkout (no persist-credentials) + upload-artifact (path: dist/) → OK (safe path)
            new RuleCase(
            "ok-safe-path",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: dist/
            """,
            []),
            // Case 4: checkout v6+ (no persist-credentials) + upload-artifact v4 (path: .) is safe.
            // v6+ credentials live under $RUNNER_TEMP, so current-dir upload does not reach them.
            new RuleCase(
            "ok-checkout-v6-upload-dot-hidden",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            []),
            new RuleCase(
            "ok-checkout-uppercase-v6-upload-dot-hidden",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@V6
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
                        []),
            // Edge case: checkout @v6-legacy should be treated as non-v6+ (arbitrary ref, error not warning)
            new RuleCase(
            "ng-checkout-v6-legacy-upload-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6-legacy
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: checkout @v6.1 is valid semver v6+, and current-dir upload remains safe.
            new RuleCase(
            "ok-checkout-v6-1-upload-dot-hidden",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6.1
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
                        []),
            // Case 5: checkout only (no upload-artifact) → OK
            new RuleCase(
            "ok-checkout-only",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
            // Case 6: upload-artifact only (no checkout) → OK
            new RuleCase(
            "ok-upload-only",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
            """,
            []),
            // Edge case: path: .. (parent directory) + hidden files → error
            new RuleCase(
            "ng-checkout-upload-dotdot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ..
                              include-hidden-files: true
            """,
            ["upload-artifact with path '..'", "persist-credentials: false"]),
            // Edge case: path: ${{ github.workspace }} + hidden files → error
            new RuleCase(
            "ng-checkout-upload-workspace",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ${{ github.workspace }}
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: persist-credentials expression is treated conservatively as unsafe
            new RuleCase(
            "ng-persist-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: ${{ inputs.persist_credentials }}
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: include-hidden-files expression is treated conservatively as potentially true
            new RuleCase(
            "ng-include-hidden-files-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: ${{ inputs.include_hidden }}
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: persist-credentials: true → still flagged
            new RuleCase(
            "ng-persist-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: SHA-pinned checkout → treated as non-v6+ (unknown version)
            new RuleCase(
            "ng-checkout-sha-upload-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            new RuleCase(
            "ng-checkout-upload-root-equivalent-dot-slash-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ./.
                              include-hidden-files: true
            """,
            ["upload-artifact with path './.'", "persist-credentials: false"]),
            new RuleCase(
            "ng-checkout-upload-root-equivalent-dot-double-slash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .//
                              include-hidden-files: true
            """,
            ["upload-artifact with path './/'", "persist-credentials: false"]),
            new RuleCase(
            "ng-checkout-upload-parent-equivalent-dotdot-slash-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ../.
                              include-hidden-files: true
            """,
            ["upload-artifact with path '../.'", "persist-credentials: false"]),
            new RuleCase(
            "ng-checkout-upload-workspace-suffix",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ${{ github.workspace }}/.
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: upload-artifact before checkout should not be reported because checkout runs later.
            new RuleCase(
            "ok-upload-before-checkout",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
                        - uses: actions/checkout@v4
            """,
            []),
            // Edge case: upload-artifact v4 with arbitrary branch/tag ref like @v4-legacy should be treated conservatively
            new RuleCase(
            "ng-checkout-upload-v4-legacy-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4-legacy
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: @v4. (dot but no minor digits) should be treated conservatively
            new RuleCase(
            "ng-checkout-upload-v4-dot-only",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: @v4.x (non-numeric minor) should be treated conservatively
            new RuleCase(
            "ng-checkout-upload-v4-dot-x",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.x
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: @v4.4-legacy (suffix after minor) should be treated conservatively
            new RuleCase(
            "ng-checkout-upload-v4-4-legacy",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.4-legacy
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: @v4.6.2 (patch version) should be accepted as v4.6 (safe, no hidden files by default)
            new RuleCase(
            "ok-checkout-upload-v4-6-2-no-hidden",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.6.2
                          with:
                              name: my-artifact
                              path: .
            """,
            []),
            // Edge case: @v4.3.1 (patch version, minor < 4) should be treated as unsafe (hidden files by default)
            new RuleCase(
            "ng-checkout-upload-v4-3-1-hidden-default",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.3.1
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: @v4.6.2-legacy (patch with suffix) should be treated conservatively
            new RuleCase(
            "ng-checkout-upload-v4-6-2-legacy",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.6.2-legacy
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: backslash path separators (Windows-style) should be treated as dangerous
            new RuleCase(
            "ng-checkout-upload-backslash-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .\
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            new RuleCase(
            "ng-checkout-upload-backslash-dotdot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ..\
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: github.workspace with backslash trailing
            new RuleCase(
            "ng-checkout-upload-workspace-backslash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ${{ github.workspace }}\
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: path with embedded newlines should be escaped in diagnostics
            new RuleCase(
            "ng-checkout-upload-multiline-path-escaped",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: |
                                  .
                                  extra
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.\\n", "persist-credentials: false"]),
            // Edge case: ${{ github.workspace }}/.. uploads parent directory (dangerous)
            new RuleCase(
            "ng-checkout-upload-workspace-dotdot-suffix",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ${{ github.workspace }}/..
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: ${{ github.workspace }}\.. (backslash) uploads parent directory (dangerous)
            new RuleCase(
            "ng-checkout-upload-workspace-backslash-dotdot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ${{ github.workspace }}\..
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: ./** glob pattern uploads everything recursively (dangerous)
            new RuleCase(
            "ng-checkout-upload-glob-dot-star-star",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ./**
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: ** alone matches everything from root (dangerous)
            new RuleCase(
            "ng-checkout-upload-glob-double-star",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: "**"
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: checkout v6+ still leaks credentials when parent-directory upload can include $RUNNER_TEMP.
            new RuleCase(
            "ng-checkout-v6-upload-parent-dir-without-hidden-files",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: my-artifact
                              path: ../..
            """,
            ["upload-artifact with path '../..'", "persist-credentials: false"]),
            // Negative case: v6+ checkout + current-dir upload + no hidden files is safe.
            // v6+ credentials are in $RUNNER_TEMP (not .git/config), and hidden files excluded,
            // so current-dir upload does not expose credentials.
            new RuleCase(
            "ok-checkout-v6-upload-dot-no-hidden",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: my-artifact
                              path: .
            """,
            []),
            // Edge case: both legacy and v6+ checkout + parent-dir upload + hidden files excluded.
            // Legacy .git/config is protected by hidden-file filter; only v6+ $RUNNER_TEMP concern
            // remains, so severity should be warning (not error).
            new RuleCase(
            "ng-checkout-both-parent-dir-no-hidden-warning",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: my-artifact
                              path: ../..
            """,
            ["upload-artifact with path '../..'", "$RUNNER_TEMP"]),
            // Edge case: SHA-pinned checkout has unknown version — conservatively assumes both risks.
            // With parent-dir upload and hidden files excluded, $RUNNER_TEMP risk yields warning.
            new RuleCase(
            "ng-checkout-sha-parent-dir-no-hidden-warning",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: my-artifact
                              path: ../..
            """,
            ["upload-artifact with path '../..'", "$RUNNER_TEMP"]),
            // Edge case: leading-zero checkout refs are arbitrary tags, not semver v6+.
            new RuleCase(
            "ng-checkout-v06-upload-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v06
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: leading-zero upload refs are arbitrary tags, so hidden-file defaults stay unknown and conservative.
            new RuleCase(
            "ng-checkout-v4-upload-v04-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v04
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Safe case: dist/** is NOT dangerous (subdirectory glob)
            new RuleCase(
            "ok-checkout-upload-glob-subdir",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: dist/**
                              include-hidden-files: true
            """,
            []),
        };

        await AssertRuleCases(new ArtipackedRule(), "artipacked", cases);
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsAllDangerousUploadsInLargeJob()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-01
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-02
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-03
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-04
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-05
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-06
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-07
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-08
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-09
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-many-uploads.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(9);
        await Assert.That(diagnostics.All(x => x.Severity == DiagnosticSeverity.Error)).IsTrue();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_DoesNotMissUnsafeCheckoutAfterSafeOnes()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-late-checkout.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsOnlyUploadsAfterUnsafeCheckout()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/upload-artifact@v4
                          with:
                              name: before-checkout
                              path: .
                              include-hidden-files: true
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: after-checkout
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-ordered-uploads.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("upload-artifact with path '.'");
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(15);
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_V6PlusCurrentDirWithHiddenFilesIsSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_DoesNotReportUploadArtifactV4_WhenHiddenFilesAreDefaultedOff()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v4-default-hidden-files.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsUploadArtifactV4_WhenHiddenFilesAreIncluded()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v4-include-hidden.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_DoesNotReportUploadArtifactV4_WhenHiddenFilesAreExplicitlyDisabled()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: false
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v4-hidden-disabled.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsPathValueLocation()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-location.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");
        var lines = yaml.Split('\n');
        var pathLineIndex = Array.FindIndex(lines, static x => x.Contains("path: .", StringComparison.Ordinal));
        var pathLine = lines[pathLineIndex];
        var expectedStartColumn = pathLine.IndexOf('.', StringComparison.Ordinal) + 1;
        var expectedLine = pathLineIndex + 1;

        await Assert.That(diagnostic.Location.StartLine).IsEqualTo(expectedLine);
        await Assert.That(diagnostic.Location.StartColumn).IsEqualTo(expectedStartColumn);
        await Assert.That(diagnostic.Location.EndLine).IsEqualTo(expectedLine);
        await Assert.That(diagnostic.Location.EndColumn).IsEqualTo(expectedStartColumn);
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsMultipleParentDirectorySegments()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ../..
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-dotdotdot.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsDeepParentPath()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ../../.
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-deep-parent.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_CaseInsensitivePersistCredentialsFalse()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: False
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-case-insensitive.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_QuotedPersistCredentialsFalse()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: 'false'
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-quoted-persist-false.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_CaseInsensitiveIncludeHiddenFilesTrue()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: True
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-case-hidden.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_QuotedIncludeHiddenFilesTrue()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: 'true'
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-quoted-hidden-true.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_UploadArtifactV4_3_TreatsAsUnsafe()
    {
        // upload-artifact v4.0-v4.3 included hidden files by default
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.3
                          with:
                              name: artifact
                              path: .
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v4.3.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_UploadArtifactV4_4_IsSafeByDefault()
    {
        // upload-artifact v4.4+ excludes hidden files by default
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: .
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v4.4.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_UploadArtifactV5_IsConservativeByDefault()
    {
        // Only v4 behavior is modeled precisely. Newer major versions are treated
        // conservatively unless hidden file behavior is explicitly known.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v5
                          with:
                              name: artifact
                              path: .
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v5.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_UploadArtifactV5_ExplicitlyDisablingHiddenFilesSuppressesLegacyCase()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v5
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: false
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v5-hidden-disabled.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_UnknownUploadArtifactRef_RemainsConservativeEvenWhenHiddenFilesDisabled()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@main
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: false
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-main-hidden-disabled.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_BothCheckoutsParentDirNoHiddenIsWarning()
    {
        // When both legacy and v6+ checkout are present but hidden files excluded,
        // legacy .git/config is protected by hidden-file filter. Only v6+ $RUNNER_TEMP
        // is at risk via parent-dir, so severity should be warning (not error).
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../..
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-both-parent.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_BothCheckoutsWithHiddenFilesIsError()
    {
        // When both legacy and v6+ checkout are present AND hidden files included,
        // legacy .git/config IS exposed, so severity should be error.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-both-hidden.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains(".git/config");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ShaPinnedCheckoutParentDirNoHiddenIsWarning()
    {
        // SHA-pinned checkout has unknown version — could be v6+.
        // With parent-dir upload and hidden files excluded, $RUNNER_TEMP may be at risk → WARNING.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../..
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-sha-parent.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ShaPinnedCheckoutWithHiddenFilesIsError()
    {
        // SHA-pinned checkout has unknown version — could be legacy.
        // With hidden files included, .git/config is at risk → ERROR.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-sha-hidden.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains(".git/config");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ShaPinnedCheckoutCurrentDirNoHiddenIsSafe()
    {
        // SHA-pinned checkout with current-dir upload and hidden files excluded.
        // Legacy .git/config is hidden (safe), v6+ $RUNNER_TEMP is not in current dir (safe) → no diagnostic.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: .
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-sha-safe.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_MultilinePathAccumulatesParentDir()
    {
        // Multi-line path: first line is "." (current-dir, not parent-exposing),
        // second line is "../.." (parent-dir). The rule must scan all lines to
        // accumulate exposesParentDirectory correctly.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  .
                                  ../..
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-multiline.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_MultilinePathExcludingGitDirectoryIsSafe()
    {
        // Multi-line artifact paths support exclusion globs. When the root is uploaded
        // but .git is excluded, legacy checkout credentials in .git/config are not exposed.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-multiline-exclude-git.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_MultilinePathExcludingGitConfigIsSafe()
    {
        // Excluding .git/config directly should also suppress the legacy checkout
        // credential exposure case.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git/config
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-multiline-exclude-git-config.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_BareGitExclusionDoesNotSuppressWarning()
    {
        // !.git (bare) does NOT exclude .git/config in @actions/glob — only !.git/** or !.git/config does
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-bare-git-exclusion.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_GitDirectoryExclusionDoesNotSuppressNestedCheckoutPath()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-exclude-root-git-nested-checkout.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_GitConfigExclusionDoesNotSuppressNestedCheckoutPath()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git/config
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-exclude-root-git-config-nested-checkout.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_NestedGitDirectoryExclusionIsSafeForNestedCheckoutPath()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !repo/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-exclude-nested-git-directory.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_InterleavedNestedCheckoutExclusionsApplyPerCheckout()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo-a
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-a
                              path: |
                                  .
                                  !repo-a/.git/**
                              include-hidden-files: true
                        - uses: actions/checkout@v4
                          with:
                              path: repo-b
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-b
                              path: |
                                  .
                                  !repo-a/.git/**
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-c
                              path: |
                                  .
                                  !repo-a/.git/**
                                  !repo-b/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-interleaved-nested-exclusions.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
        // Verify the diagnostic targets artifact-b's path (line 22 = "path: |", content starts line 23)
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(23);
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_DeepNestedGitDirectoryExclusionIsSafe()
    {
        var nestedCheckoutPath = string.Join("/", Enumerable.Range(1, 64).Select(index => $"segment-{index:D2}"));
        var yaml = NormalizeYaml(
            $$"""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: {{nestedCheckoutPath}}
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !{{nestedCheckoutPath}}/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-deep-nested-git-directory-exclusion.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_LegacyGitExclusionDoesNotSuppressV6ParentDirectoryWarning()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  ../..
                                  !.git/**
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-parent-with-legacy-exclusion.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_NegativePatternExcludingRunnerTempSuppressesV6Warning()
    {
        // !../../_temp/** after ../.. explicitly excludes $RUNNER_TEMP content,
        // so the v6+ credential exposure warning should be suppressed.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  ../..
                                  !../../_temp/**
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-parent-with-temp-exclusion.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_BareNegativePatternWithoutGlobDoesNotSuppressV6Warning()
    {
        // !../../_temp (without trailing glob) does not exclude files UNDER the directory,
        // so the v6+ credential exposure warning must NOT be suppressed.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  ../..
                                  !../../_temp
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-parent-with-bare-temp-exclusion.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_WorkspacePrefixedRunnerTempExclusionSuppressesV6Warning()
    {
        // Workspace-prefixed exclusions should behave like other workspace-relative
        // artipacked paths and suppress the v6+ warning when they exclude temp contents.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  ../..
                                  !${{ github.workspace }}/../../_temp/**
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-parent-with-workspace-temp-exclusion.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ShallowRunnerTempWildcardDoesNotSuppressV6Warning()
    {
        // !_temp/* only excludes immediate children and does not cover the full
        // runner-temp subtree where checkout v6+ credentials may live.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  ../..
                                  !../../_temp/*
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-parent-with-shallow-temp-exclusion.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_NestedCheckoutUploadPathWithoutRootLikeExpansionRemainsDeferred()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: repo
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-nested-upload-path-deferred.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_GitConfigSubpathExclusionIsNotSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git/config/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-exclude-git-config-subpath.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_InternalWhitespaceInCheckoutPathDoesNotMatchDifferentExclusionPath()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo /nested
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !repo/nested/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-checkout-path-with-internal-whitespace.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_BracketWorkspacePathIsFlagged()
    {
        // Bracket-style workspace access is equivalent to github.workspace and should
        // be treated as a dangerous root-like path.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ github['workspace'] }}
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-bracket-workspace.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_DoubleQuotedBracketWorkspacePathIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ github['workspace'] }}
                              include-hidden-files: true
            """).Replace("github['workspace']", "github[\"workspace\"]", StringComparison.Ordinal);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-double-quoted-bracket-workspace.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_UppercaseWorkspacePathIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ GITHUB.workspace }}
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-uppercase-workspace.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_RootFileGlobIsNotFlagged()
    {
        // A narrow root file glob does not recursively sweep the checkout root and
        // should not be treated like ./** or **.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: '*.txt'
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-root-file-glob.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_RootSingleWildcardIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: '*'
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-root-single-wildcard.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_DotSlashSingleWildcardIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: './*'
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-dot-slash-single-wildcard.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_V6SingleParentDirectoryIsSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ..
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-single-parent.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_V6WorkspaceSingleParentDirectoryIsSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ${{ github.workspace }}/..
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-workspace-single-parent.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_V6NamedDirectoryTwoLevelsUpIsNotFlagged()
    {
        // ../../some-dir targets a specific non-_temp directory — does NOT reach $RUNNER_TEMP
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../some-dir
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-named-dir.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_V6SingleLevelTempIsNotFlagged()
    {
        // ../_temp is only 1 level up — NOT the real $RUNNER_TEMP (which is 2 levels up)
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../_temp
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-single-level-temp.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_RootRecursiveGlobWithFilesIsFlagged()
    {
        var yaml = "on: push\n"
            + "jobs:\n"
            + "  build:\n"
            + "    runs-on: ubuntu-latest\n"
            + "    steps:\n"
            + "      - uses: actions/checkout@v4\n"
            + "      - uses: actions/upload-artifact@v4\n"
            + "        with:\n"
            + "          name: artifact\n"
            + "          path: |\n"
            + "            **/*\n"
            + "          include-hidden-files: true\n";

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-root-recursive-with-files.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_WorkspaceRecursiveGlobIsFlagged()
    {
        // Workspace-root recursive glob is equivalent to ./** and should be treated
        // as a dangerous root-like upload.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ github.workspace }}/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-workspace-recursive-glob.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_CurrentDirectoryRecursiveGlobWithFilesIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ./**/*
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-current-recursive-with-files.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_NormalizedRootPathIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: repo/..
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-normalized-root.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_NormalizedWorkspacePathIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ github.workspace }}/repo/..
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-normalized-workspace.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_NormalizedRootPathExcludingGitDirectoryIsSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  repo/..
                                  !repo/../.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-normalized-root-exclude-git.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_NormalizedWorkspacePathExcludingGitConfigIsSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  ${{ github.workspace }}/repo/..
                                  !${{ github.workspace }}/repo/../.git/config
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-normalized-workspace-exclude-git-config.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_NormalizedWorkspaceGitConfigSubpathExclusionIsNotSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  ${{ github.workspace }}/repo/..
                                  !${{ github.workspace }}/repo/../.git/config/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-normalized-workspace-exclude-git-config-subpath.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ExpressionPathIsNotFlaggedAsDangerous()
    {
        // Dynamic expression path like ${{ inputs.artifact_path }} should not be
        // treated as a dangerous glob — it resolves at runtime and cannot be
        // classified statically.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ inputs.artifact_path }}
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-expr-path.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_WorkspaceSuffixWithoutSeparatorIsNotFlagged()
    {
        // ${{ github.workspace }}.. (no separator) is string concatenation, NOT a parent path.
        // The rule should NOT treat it as ${{ github.workspace }}/.. .
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ github.workspace }}..
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-workspace-no-separator.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_WorkspaceExclusionWithoutSeparatorDoesNotSuppress()
    {
        // !${{ github.workspace }}.git/** (no separator) is not a valid exclusion.
        // The rule should still flag the upload.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  ${{ github.workspace }}
                                  !${{ github.workspace }}.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-workspace-exclusion-no-separator.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_RecursiveWildcardExcludesSuppressesNestedCheckout()
    {
        // !**/.git/** should suppress the warning for a nested checkout at "repo"
        // because ** matches any prefix including "repo".
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !**/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-recursive-wildcard-nested-checkout.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_RecursiveWildcardExcludesSuppressesRootCheckout()
    {
        // !**/.git/** should also suppress the warning for a root checkout (empty path)
        // because ** can match zero segments.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !**/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-recursive-wildcard-root-checkout.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ParentDirectoryWithChildNameIsWarning()
    {
        // ../../_temp escapes the workspace even though it names a child directory.
        // On GitHub-hosted runners this can reach $RUNNER_TEMP, so v6+ checkout should
        // be warned (parent-directory exposure).
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../_temp
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-with-child.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_WorkspaceParentDirectoryWithChildNameIsWarning()
    {
        // ${{ github.workspace }}/../../_temp escapes the workspace even though it names
        // a child. Should be flagged as parent-directory exposure.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ${{ github.workspace }}/../../_temp
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-workspace-parent-child.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ParentDirectorySingleFileIsNotFlagged()
    {
        // A narrow parent-directory file path is not equivalent to sweeping a parent
        // directory tree or $RUNNER_TEMP. Keep this deferred rather than warning.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../artifact.txt
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-single-file.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ParentDirectoryTempRecursiveGlobIsWarning()
    {
        // ../../_temp/** sweeps $RUNNER_TEMP recursively — should be flagged.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../_temp/**
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-temp-recursive-glob.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ParentDirectoryTempStarGlobIsWarning()
    {
        // ../../_temp/* sweeps immediate children of $RUNNER_TEMP — should be flagged.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../_temp/*
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-temp-star-glob.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_ParentDirectoryTempRecursiveStarGlobIsWarning()
    {
        // ../../_temp/**/* sweeps $RUNNER_TEMP recursively — should be flagged.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../_temp/**/*
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-temp-recursive-star-glob.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_IntermediateBacktrackToRunnerTempIsDetected()
    {
        // ../../foo/../_temp normalizes to ../../_temp — should reach $RUNNER_TEMP.
        // Regression: the intermediate `foo` segment left escapedNamedSegments stale
        // so that the subsequent `_temp` was counted as the 2nd escaped segment.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../foo/../_temp
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-backtrack-temp.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }


    [Test]
    public async Task RuleRegression_ArtipackedRule_LeadingRecursiveExclusionSuppressesWithExpressionCheckoutPath()
    {
        // !**/.git/** suppresses legacy credential exposure even when checkout
        // path contains an expression (cannot be statically normalized).
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v3
                          with:
                              path: ${{ matrix.repo_path }}
                        - uses: actions/upload-artifact@v4.3
                          with:
                              name: artifact
                              path: |
                                  .
                                  !**/.git/**
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-recursive-excl-expr-path.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    private static async Task AssertRuleCases(IRule rule, string ruleId, RuleCase[] cases, LintConfig? config = null)
    {
        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var yaml = NormalizeYaml(c.Yaml);
            using var result = config is null
                ? new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), $"rule-case-{c.Name}.yml")
                : new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), $"rule-case-{c.Name}.yml", config);
            var diagnostics = result.Diagnostics.Where(x => x.RuleId == ruleId).ToArray();

            if (c.ExpectedSubstrings.Length == 0)
            {
                await Assert.That(diagnostics).IsEmpty();
                continue;
            }

            for (var j = 0; j < c.ExpectedSubstrings.Length; j++)
            {
                var expected = c.ExpectedSubstrings[j];
                var found = diagnostics.Any(x => x.Message.Contains(expected, StringComparison.Ordinal));
                if (!found)
                {
                    var observed = diagnostics.Length == 0
                        ? "<no rule diagnostics>"
                        : string.Join(" | ", diagnostics.Select(static x => x.Message));
                    throw new InvalidOperationException($"rule={ruleId} case={c.Name} expected={expected} observed={observed}");
                }
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

    private static string NormalizeYaml(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        var end = lines.Length - 1;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        if (end < start)
        {
            return string.Empty;
        }

        var minIndent = int.MaxValue;
        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            if (indent < minIndent)
            {
                minIndent = indent;
            }
        }

        if (minIndent == int.MaxValue)
        {
            minIndent = 0;
        }

        var builder = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            if (line.Length >= minIndent)
            {
                builder.Append(line[minIndent..]);
            }
            else
            {
                builder.Append(line);
            }

            if (i < end)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private readonly record struct RuleCase(string Name, string Yaml, string[] ExpectedSubstrings);

    private readonly record struct FixabilityCase(string RuleId, IRule Rule, string Yaml, bool ExpectsFix);

    private sealed class DuplicateDiagnosticRule : IRule
    {
        private readonly List<Diagnostic> diagnostics = [];

        public DuplicateDiagnosticRule(RuleId id)
        {
            Id = id;
        }

        public RuleId Id { get; }

        public string Name => $"Duplicate-{Id.ToId()}";

        public bool SupportsDocumentKind(DocumentKind documentKind) => true;

        public IReadOnlyList<Diagnostic> GetDiagnostics() => diagnostics;

        public void SetConfig(LintConfig config)
        {
        }

        public void VisitWorkflowPre(Workflow workflow)
        {
            diagnostics.Clear();
            diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "shared duplicate diagnostic",
                    new TextRange(0, 0, 1, 1, 1, 1),
                    RuleId: Id.ToId()));
        }

        public void VisitWorkflowPost(Workflow workflow)
        {
        }

        public void VisitEvent(Event ev)
        {
        }

        public void VisitJobPre(Job job)
        {
        }

        public void VisitJobPost(Job job)
        {
        }

        public void VisitStep(Step step)
        {
        }
    }

    private sealed class ConfigCaptureRule : IRule
    {
        public RuleId Id => RuleId.JobStructure;

        public string Name => "Config Capture Rule";

        public bool SupportsDocumentKind(DocumentKind documentKind) => true;

        public LintConfig? LastConfig { get; private set; }

        public IReadOnlyList<Diagnostic> GetDiagnostics() => [];

        public void SetConfig(LintConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            LastConfig = config;
        }

        public void VisitWorkflowPre(Workflow workflow)
        {
        }

        public void VisitWorkflowPost(Workflow workflow)
        {
        }

        public void VisitEvent(Event ev)
        {
        }

        public void VisitJobPre(Job job)
        {
        }

        public void VisitJobPost(Job job)
        {
        }

        public void VisitStep(Step step)
        {
        }
    }

    private sealed class CountingRule : IRule
    {
        private LintConfig? config;

        public RuleId Id => RuleId.JobStructure;

        public string Name => "Test Rule";

        public bool SupportsDocumentKind(DocumentKind documentKind) => true;

        public int WorkflowPreCount { get; private set; }

        public int WorkflowPostCount { get; private set; }

        public int EventCount { get; private set; }

        public int JobPreCount { get; private set; }

        public int JobPostCount { get; private set; }

        public int StepCount { get; private set; }

        public IReadOnlyList<Diagnostic> GetDiagnostics() => [];

        public void SetConfig(LintConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            this.config = config;
        }

        public void VisitWorkflowPre(Workflow workflow)
        {
            EnsureConfigured();
            WorkflowPreCount++;
        }

        public void VisitWorkflowPost(Workflow workflow)
        {
            EnsureConfigured();
            WorkflowPostCount++;
        }

        public void VisitEvent(Event ev)
        {
            EnsureConfigured();
            EventCount++;
        }

        public void VisitJobPre(Job job)
        {
            EnsureConfigured();
            JobPreCount++;
        }

        public void VisitJobPost(Job job)
        {
            EnsureConfigured();
            JobPostCount++;
        }

        public void VisitStep(Step step)
        {
            EnsureConfigured();
            StepCount++;
        }

        private void EnsureConfigured()
        {
            if (config is null)
            {
                throw new InvalidOperationException("Rule is not configured.");
            }
        }
    }
}

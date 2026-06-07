using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;
using System.Text;
using static Seiton.Core.Tests.TestHelper;

namespace Seiton.Core.Tests;

public sealed class FixEngineTests
{
    [Test]
    public async Task Apply_AppliesDiagnosticFixCollection()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var fixes = new[]
        {
            new DiagnosticFix("first", [new TextEdit(2, 2, "AB")]),
            new DiagnosticFix("second", [new TextEdit(7, 2, "YZ")]),
        };

        var result = FixEngine.Apply(source, fixes);

        await Assert.That(Encoding.UTF8.GetString(result)).IsEqualTo("01AB456YZ9");
    }

    [Test]
    public async Task Apply_AppliesDiagnosticsWithFixAndIgnoresNoFixDiagnostics()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var diagnostics = new[]
        {
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "no fix",
                new TextRange(0, 0, 1, 1, 1, 1),
                RuleId: "x"),
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "has fix",
                new TextRange(0, 0, 1, 1, 1, 1),
                RuleId: "x",
                Fix: new DiagnosticFix("replace", [new TextEdit(2, 2, "AB")]))
        };

        var result = FixEngine.Apply(source, diagnostics);

        await Assert.That(Encoding.UTF8.GetString(result)).IsEqualTo("01AB456789");
    }

    [Test]
    public async Task Apply_AppliesEditsInDescendingOffsetOrder()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var edits = new[]
        {
            new TextEdit(2, 2, "AB"),
            new TextEdit(7, 2, "YZ"),
        };

        var result = FixEngine.Apply(source, edits);

        await Assert.That(Encoding.UTF8.GetString(result)).IsEqualTo("01AB456YZ9");
    }

    [Test]
    public async Task Apply_RejectsOverlappingEdits()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var edits = new[]
        {
            new TextEdit(2, 4, "ABCD"),
            new TextEdit(5, 2, "YZ"),
        };

        await Assert.That(() => FixEngine.Apply(source, edits)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Apply_RejectsOverlappingEdits_MessageContainsOffsetAndBatchInfo()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var edits = new[]
        {
            new TextEdit(2, 4, "ABCD"),
            new TextEdit(5, 2, "YZ"),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => FixEngine.Apply(source, edits));
        // Message must include the conflicting offset so users/developers can diagnose
        await Assert.That(ex.Message).Contains("offset 5");
        // Message must include batch size for context
        await Assert.That(ex.Message).Contains("2 edits in batch");
    }

    [Test]
    public async Task Apply_RejectsConflictingEditsAtSameOffset_MessageContainsContext()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var edits = new[]
        {
            new TextEdit(3, 0, "INSERT-A"),
            new TextEdit(3, 0, "INSERT-B"),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => FixEngine.Apply(source, edits));
        // Same-offset (insert at same position) must be reported clearly
        await Assert.That(ex.Message).Contains("offset 3");
        await Assert.That(ex.Message).Contains("2 edits in batch");
    }

    [Test]
    public async Task Apply_DiagnosticsWithConflictingEdits_IncludesRuleIdsInMessage()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var diagnostics = new[]
        {
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "first",
                new TextRange(3, 0, 1, 4, 1, 4),
                RuleId: "unpinned-uses",
                Fix: new DiagnosticFix("a", [new TextEdit(3, 0, "INSERT-A")])),
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "second",
                new TextRange(3, 0, 1, 4, 1, 4),
                RuleId: "job-timeout-minutes-required",
                Fix: new DiagnosticFix("b", [new TextEdit(3, 0, "INSERT-B")])),
        };

        var ex = Assert.Throws<FixApplyConflictException>(() => FixEngine.Apply(source, diagnostics));
        await Assert.That(ex.Message).Contains("unpinned-uses");
        await Assert.That(ex.Message).Contains("job-timeout-minutes-required");
    }

    [Test]
    public async Task Apply_DiagnosticsWithConflictingEdits_PreservesOriginalConflictAsInnerException()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var diagnostics = new[]
        {
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "first",
                new TextRange(3, 0, 1, 4, 1, 4),
                RuleId: "unpinned-uses",
                Fix: new DiagnosticFix("a", [new TextEdit(3, 0, "INSERT-A")])),
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "second",
                new TextRange(3, 0, 1, 4, 1, 4),
                RuleId: "job-timeout-minutes-required",
                Fix: new DiagnosticFix("b", [new TextEdit(3, 0, "INSERT-B")])),
        };

        var ex = Assert.Throws<FixApplyConflictException>(() => FixEngine.Apply(source, diagnostics));

        await Assert.That(ex.InnerException).IsNotNull();
        await Assert.That(ex.InnerException).IsTypeOf<FixApplyConflictException>();
        await Assert.That(ex.InnerException!.Message).DoesNotContain("conflicting rule-id(s)");
    }

    [Test]
    public async Task Apply_PartiallyOverlappingReplacements_Throws()
    {
        // Edit A covers [2,6), Edit B covers [4,8) — partial overlap
        var source = Encoding.UTF8.GetBytes("0123456789");
        var edits = new[]
        {
            new TextEdit(2, 4, "AAAA"),
            new TextEdit(4, 4, "BBBB"),
        };

        await Assert.That(() => FixEngine.Apply(source, edits)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Apply_AdjacentEdits_DoNotConflict()
    {
        // Edit A covers [0,3), Edit B covers [3,3) — adjacent, not overlapping
        var source = Encoding.UTF8.GetBytes("0123456789");
        var edits = new[]
        {
            new TextEdit(0, 3, "AAA"),
            new TextEdit(3, 3, "BBB"),
        };

        var result = FixEngine.Apply(source, edits);

        await Assert.That(Encoding.UTF8.GetString(result)).IsEqualTo("AAABBB6789");
    }

    [Test]
    public async Task Apply_ThreeEditsAtDifferentPositions_AllAppliedInOrder()
    {
        // Verify that multiple non-overlapping edits given in any order are applied correctly
        var source = Encoding.UTF8.GetBytes("0123456789ABCDEF");
        var edits = new[]
        {
            new TextEdit(10, 2, "XX"),  // middle
            new TextEdit(0, 2, "YY"),   // start
            new TextEdit(14, 2, "ZZ"),  // end
        };

        var result = FixEngine.Apply(source, edits);

        await Assert.That(Encoding.UTF8.GetString(result)).IsEqualTo("YY23456789XXCDZZ");
    }

    [Test]
    public async Task DetectDominantLineEnding_PrefersCrLfWhenMajority()
    {
        var source = Encoding.UTF8.GetBytes("a\r\nb\r\nc\n");

        var lineEnding = FixFormatting.DetectDominantLineEnding(source);

        await Assert.That(lineEnding).IsEqualTo("\r\n");
    }

    [Test]
    public async Task InferIndentation_PrefersSiblingIndentation()
    {
        var source = NormalizeYamlLiteral("""
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
        """);

        var indentation = FixFormatting.InferIndentation(source, siblingLineNumber: 3, parentLineNumber: 2);

        await Assert.That(indentation).IsEqualTo("    ");
    }

    [Test]
    public async Task InferIndentation_FallsBackToParentPlusIndentationUnit()
    {
        var source = NormalizeYamlLiteral("""
        jobs:
          build:
        """);

        var indentation = FixFormatting.InferIndentation(source, siblingLineNumber: null, parentLineNumber: 2);

        await Assert.That(indentation).IsEqualTo("    ");
    }

    [Test]
    public async Task TryInferIndentation_ReturnsTrue_ForMixedScopeIndentationCurrentBehavior()
    {
        var source = NormalizeYamlLiteral("""
        jobs:
          build:
            runs-on: ubuntu-latest
        """) + "  \tsteps:\n";

        var ok = FixFormatting.TryInferIndentation(
            source,
            siblingLineNumber: null,
            parentLineNumber: 2,
            scopeStartLine: 3,
            scopeEndLine: 4,
            out _);

        await Assert.That(ok).IsTrue();
    }

    [Test]
    public async Task TryInferIndentation_ReturnsTrue_WhenSpaceParentWouldRequireGlobalTabUnitCurrentBehavior()
    {
        var source = NormalizeYamlLiteral("""
        jobs:
          build: {}
        """) + "\tnote: tab-leading\n";

        var ok = FixFormatting.TryInferIndentation(
            source,
            siblingLineNumber: null,
            parentLineNumber: 2,
            scopeStartLine: 3,
            scopeEndLine: 3,
            out _);

        await Assert.That(ok).IsTrue();
    }

    [Test]
    public async Task DetectQuoteStyle_UsesSourceBytesAroundRange()
    {
        var source = Encoding.UTF8.GetBytes("name: 'value'\n");

        var quoteStyle = FixFormatting.DetectQuoteStyle(
            source,
            new TextRange(7, 5, 1, 8, 1, 13),
            quoted: true);

        await Assert.That(quoteStyle).IsEqualTo(ScalarQuoteStyle.SingleQuoted);
    }

    [Test]
    public async Task LintResult_Fixes_ReturnsOnlyFixPayloads()
    {
        var parseResult = new ParseResultData(null, null, [], HasFatalError: false);
        var result = new LintResultData(
            parseResult,
            [
                new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "no fix",
                    new TextRange(0, 0, 1, 1, 1, 1),
                    RuleId: "a"),
                new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "has fix",
                    new TextRange(0, 0, 1, 1, 1, 1),
                    RuleId: "b",
                    Fix: new DiagnosticFix("replace", [new TextEdit(1, 1, "X")]))
            ]);

        await Assert.That(result.Fixes.Length).IsEqualTo(1);
        await Assert.That(result.Fixes[0].Description).IsEqualTo("replace");
    }

    [Test]
    public async Task ApplyAndRelint_ClearsSelectedDiagnostics()
    {
        var yaml = NormalizeEol("""
        on: push
        permissions: write-all
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ok
        """);

        var source = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyWriteAllRule()]);
        using var before = engine.Check(source, "revalidate-deny.yml");

        using var revalidated = FixEngine.ApplyAndRelint(engine, source, "revalidate-deny.yml", before.FixableDiagnostics);

        await Assert.That(revalidated.Before.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsFalse();
        await Assert.That(revalidated.After.HasFatalError).IsFalse();
    }

    [Test]
    public async Task ApplyAndRelint_ThrowsWhenFatalParseErrorIsIntroduced()
    {
        var yaml = NormalizeEol("""
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ok
        """);

        var source = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobStructureRule()]);
        var fixes = new[]
        {
            new DiagnosticFix("break yaml", [new TextEdit(0, source.Length, "[]")]),
        };

        await Assert.That(() => FixEngine.ApplyAndRelint(engine, source, "revalidate-fatal.yml", fixes))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ApplyAndRelint_ThrowsOnOverlappingEditsBeforeRelint()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var engine = new LintEngine([new JobStructureRule()]);
        var fixes = new[]
        {
            new DiagnosticFix("left", [new TextEdit(2, 4, "ABCD")]),
            new DiagnosticFix("right", [new TextEdit(5, 2, "YZ")]),
        };

        await Assert.That(() => FixEngine.ApplyAndRelint(engine, source, "revalidate-overlap.yml", fixes))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RunSecretsContextDirectUseFix_ApplyAndRelint_ClearsDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ secrets.MY_TOKEN }}"
        """;
        var source = Encoding.UTF8.GetBytes(yaml);
        var config = new LintConfig { Fix = new FixConfig { Enabled = true } };
        var engine = new LintEngine([new RunSecretsContextDirectUseRule()]);

        using var before = engine.Check(source, "secrets-access.yml", config);
        await Assert.That(before.Diagnostics.Any(d => d.RuleId == "run-secrets-context-direct-use" && d.Fix is not null)).IsTrue();

        using var revalidated = FixEngine.ApplyAndRelint(
            engine,
            source,
            "secrets-access.yml",
            before.Fixes,
            expectedClearedRuleIds: ["run-secrets-context-direct-use"],
            config);

        await Assert.That(revalidated.After.Diagnostics.Any(d => d.RuleId == "run-secrets-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task ApplyAndRelint_WithExpectedClearedRuleIds_PassesWhenRuleIsCleared()
    {
        var yaml = """
                on: push
                permissions: write-all
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ok
                """;

        var source = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyWriteAllRule()]);
        using var before = engine.Check(source, "revalidate-expected-pass.yml");

        using var revalidated = FixEngine.ApplyAndRelint(
                engine,
                source,
                "revalidate-expected-pass.yml",
                before.Fixes,
                expectedClearedRuleIds: ["deny-write-all"]);

        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsFalse();
    }

    [Test]
    public async Task ApplyAndRelint_WithExpectedClearedRuleIds_ThrowsWhenRuleRemains()
    {
        var yaml = """
                on: push
                permissions: write-all
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ok
                """;

        var source = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyWriteAllRule()]);
        var noOpFixes = new[]
        {
                        new DiagnosticFix("noop", [new TextEdit(source.Length, 0, string.Empty)]),
                };

        await Assert.That(() => FixEngine.ApplyAndRelint(
                        engine,
                        source,
                        "revalidate-expected-fail.yml",
                        noOpFixes,
                        expectedClearedRuleIds: ["deny-write-all"]))
                .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AutoFixCatalog_MixedDiagnostics_AttachFixesOnlyForDocumentedRuleIds()
    {
        var yaml = """
        on: pull_request_target
        permissions: write-all
        jobs:
            "build job":
                if: ${{ steps.prep.outcome == 'success' }}
                runs-on: ubuntu-9999
                steps:
                    - uses: actions/checkout@v4
                      with:
                          fetch-depht: 1
                    - shell: fish
                      run: echo "${{ env.VERSION }}"
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "fixability-mixed.yml");
        var expectedFixableRuleIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "deny-write-all",
            "job-permissions-required",
            "run-env-context-direct-use",
            "run-secrets-context-direct-use",
            "run-inputs-context-direct-use",
            "checkout-persist-credentials",
            "deny-read-all",
            "job-timeout-minutes-required",
            "id-naming",
        };

        var attachedFixRuleIds = result.Diagnostics
            .Where(static x => x.Fix is not null && x.RuleId is not null)
            .Select(static x => x.RuleId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        await Assert.That(attachedFixRuleIds.Length > 0).IsTrue();
        for (var i = 0; i < attachedFixRuleIds.Length; i++)
        {
            await Assert.That(expectedFixableRuleIds.Contains(attachedFixRuleIds[i])).IsTrue();
        }
    }

    [Test]
    public async Task BuildUnifiedDiff_ShowsChangedLinesAndContext_ForReplacementEdit()
    {
        var sourceText = NormalizeEol("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
            """);
        var source = Encoding.UTF8.GetBytes(sourceText);
        var offset = sourceText.IndexOf("push", StringComparison.Ordinal);
        var fixes = new[]
        {
            new DiagnosticFix("replace trigger", [new TextEdit(offset, "push".Length, "pull_request")]),
        };

        var diff = FixEngine.BuildUnifiedDiff(source, fixes, "workflow.yml", contextLines: 1);

        await Assert.That(diff.Contains("--- workflow.yml", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diff.Contains("+++ workflow.yml", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diff.Contains("@@", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diff.Contains("-on: push", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diff.Contains("+on: pull_request", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diff.Contains(" jobs:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diff.Contains("runs-on: ubuntu-latest", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task BuildUnifiedDiff_ShowsInsertedLine_ForInsertionEdit()
    {
        var sourceText = NormalizeYamlLiteral("""
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ok
        """);
        var source = Encoding.UTF8.GetBytes(sourceText);
        var offset = sourceText.IndexOf("    steps:", StringComparison.Ordinal);
        var fixes = new[]
        {
            new DiagnosticFix("insert permissions", [new TextEdit(offset, 0, "    permissions: {}\n")]),
        };

        var diff = FixEngine.BuildUnifiedDiff(source, fixes, "workflow.yml", contextLines: 1);

        await Assert.That(diff.Contains("@@", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diff.Contains("+    permissions: {}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diff.Contains("-    steps:", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task JobPermissionsRequiredFix_AndDryRunDiff_PreserveIndentation_ForTwoSpaceYaml()
    {
        var sourceText = NormalizeYamlLiteral("""
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ok
        """);

        var source = Encoding.UTF8.GetBytes(sourceText);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        using var lint = engine.Check(source, "indent-2.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });

        await Assert.That(lint.Diagnostics.Any(x => x.RuleId == "job-permissions-required" && x.Fix is not null)).IsTrue();

        var diff = NormalizeEol(FixEngine.BuildUnifiedDiff(source, lint.Diagnostics, "indent-2.yml", contextLines: 1));
        await Assert.That(diff.Contains("+    permissions: {}", StringComparison.Ordinal)).IsTrue();

        var updated = NormalizeEol(Encoding.UTF8.GetString(FixEngine.Apply(source, lint.Diagnostics)));
        await Assert.That(updated.Contains("    runs-on: ubuntu-latest\n    permissions: {}\n    steps:", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task JobPermissionsRequiredFix_AndDryRunDiff_PreserveIndentation_ForFourSpaceYaml()
    {
        var sourceText = NormalizeYamlLiteral("""
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """);

        var source = Encoding.UTF8.GetBytes(sourceText);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        using var lint = engine.Check(source, "indent-4.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });

        await Assert.That(lint.Diagnostics.Any(x => x.RuleId == "job-permissions-required" && x.Fix is not null)).IsTrue();

        var diff = NormalizeEol(FixEngine.BuildUnifiedDiff(source, lint.Diagnostics, "indent-4.yml", contextLines: 1));
        await Assert.That(diff.Contains("+        permissions: {}", StringComparison.Ordinal)).IsTrue();

        var updated = NormalizeEol(Encoding.UTF8.GetString(FixEngine.Apply(source, lint.Diagnostics)));
        await Assert.That(updated.Contains("        runs-on: ubuntu-latest\n        permissions: {}\n        steps:", StringComparison.Ordinal)).IsTrue();
    }

    private static string NormalizeYamlLiteral(string value)
    {
        var normalized = NormalizeEol(value);
        var lines = normalized.Split('\n');

        var minIndent = int.MaxValue;
        for (var i = 0; i < lines.Length; i++)
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

            if (indent == line.Length)
            {
                continue;
            }

            if (indent < minIndent)
            {
                minIndent = indent;
            }
        }

        if (minIndent == int.MaxValue || minIndent == 0)
        {
            return normalized;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length >= minIndent)
            {
                lines[i] = line[minIndent..];
            }
        }

        return string.Join("\n", lines);
    }
}

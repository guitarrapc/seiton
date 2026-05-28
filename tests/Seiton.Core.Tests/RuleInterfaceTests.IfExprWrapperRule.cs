using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_IfExprWrapperRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-already-wrapped",
            """
            on: push
            jobs:
                build:
                    if: ${{ github.ref != 'refs/heads/main' }}
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ success() }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-literal-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: true
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-literal-false",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: false
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-always-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: always()
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-failure-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: failure()
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-cancelled-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: cancelled()
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-success-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: success()
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-step-bare-comparison",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.event_name == 'push'
                          run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
            new RuleCase(
            "ng-job-bare-comparison",
            """
            on: push
            jobs:
                build:
                    if: github.ref != 'refs/heads/main'
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
            new RuleCase(
            "ng-step-bare-context-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.event.pull_request.merged
                          run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
            new RuleCase(
            "ng-step-bare-logical-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.event_name == 'push' && github.ref == 'refs/heads/main'
                          run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
            new RuleCase(
            "ng-step-bare-negation",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: "!cancelled()"
                          run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
            new RuleCase(
            "ng-snapshot-if-bare-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    snapshot:
                        image-name: test
                        if: github.event_name == 'push'
                    steps:
                        - run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
        };

        await AssertRuleCases(new IfExprWrapperRule(), "if-expr-wrapper", cases);
    }



    [Test]
    public async Task IfExprWrapperRule_AutoFix_WrapsExpression()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - if: github.event_name == 'push'
                      run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new IfExprWrapperRule()]);
        using var result = engine.Check(sourceBytes, "if-expr-wrapper-fix.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "if-expr-wrapper");

        await Assert.That(diagnostic.Fix is not null).IsTrue();
        await Assert.That(diagnostic.Fix!.Value.Description).Contains("${{");

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "if-expr-wrapper-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(fixedText).Contains("${{ github.event_name == 'push' }}");
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "if-expr-wrapper")).IsFalse();
    }


    [Test]
    public async Task IfExprWrapperRule_BlockScalar_MessageDoesNotContainNewline()
    {
        // Regression: block scalar `if: |\n  expr` includes trailing \n in raw value.
        // The diagnostic message must NOT contain the raw newline.
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: |\n          github.event_name == 'push'\n        run: echo ok\n";
        using var result = new LintEngine([new IfExprWrapperRule()]).Check(

            Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-expr-wrapper").ToArray();

        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        // Message must not contain literal newline
        await Assert.That(diagnostics[0].Message).DoesNotContain("\n");
        await Assert.That(diagnostics[0].Message).Contains("github.event_name == 'push'");
    }


    [Test]
    public async Task IfExprWrapperRule_BlockScalar_NoAutoFix()
    {
        // Block scalar `if: |\n  expr\n` must NOT offer auto-fix (trailing \n is structural)
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: |\n          github.event_name == 'push'\n        run: echo ok\n";
        using var result = new LintEngine([new IfExprWrapperRule()]).Check(

            Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-expr-wrapper").ToArray();

        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        // Block scalar must not offer auto-fix (would break YAML structure)
        await Assert.That(diagnostics[0].Fix is null).IsTrue();
    }


    [Test]
    public async Task IfExprWrapperRule_QuotedScalar_FixIncludesQuotes()
    {
        // Quoted scalar `if: "expr"` — fix must replace including surrounding quotes
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - if: "github.event_name == 'push'"
                      run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new IfExprWrapperRule()]);
        using var result = engine.Check(sourceBytes, "test.yaml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "if-expr-wrapper");

        await Assert.That(diagnostic.Fix is not null).IsTrue();
        // Apply fix and verify the result doesn't have leftover quotes around ${{ }}
        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "test.yaml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(fixedText).Contains("${{ github.event_name == 'push' }}");
        await Assert.That(fixedText).DoesNotContain("\"${{ github.event_name == 'push' }}\"");
    }


    [Test]
    public async Task IfExprWrapperRule_ContainsExpressionMarker_NoFix()
    {
        // Value already contains ${{ but isn't a clean wrapper (leading !) — should warn but NOT offer fix
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - if: "!${{ cancelled() }}"
                      run: echo ok
        """;

        using var result = new LintEngine([new IfExprWrapperRule()]).Check(

            Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-expr-wrapper").ToArray();

        // This fires (not a clean wrapper) but must NOT offer fix (would nest ${{ }})
        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        await Assert.That(diagnostics[0].Fix is null).IsTrue();
        // Message should say "not properly wrapped" (not "missing wrapper") when ${{ is already present
        await Assert.That(diagnostics[0].Message).DoesNotContain("missing");
        await Assert.That(diagnostics[0].Message).Contains("not properly wrapped");
    }


    [Test]
    public async Task IfExprWrapperRule_ReuseAcrossFiles_NoCrash()
    {
        // Rule instance reused across multiple Check calls must not crash
        // when the cached slice offset from file1 exceeds file2's length.
        var rule = new IfExprWrapperRule();
        var engine = new LintEngine([rule]);

        // Long YAML with condition near the end (high offset cached)
        var yaml1 = "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n      - run: echo bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n      - run: echo cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\n      - if: github.ref == 'main'\n        run: echo 1\n";
        // Short YAML with same-length condition — cached offset from yaml1 exceeds yaml2 length
        var yaml2 = "on: push\njobs:\n  b:\n    runs-on: ubuntu-latest\n    steps:\n      - if: github.ref == 'main'\n        run: echo 2\n";

        using var result1 = engine.Check(Encoding.UTF8.GetBytes(yaml1), "file1.yml");
        await Assert.That(result1.Diagnostics.Any(d => d.RuleId == "if-expr-wrapper")).IsTrue();

        // Second call with shorter yaml must not throw (stale cache offset > yaml2.Length)
        using var result2 = engine.Check(Encoding.UTF8.GetBytes(yaml2), "file2.yml");
        await Assert.That(result2.Diagnostics.Any(d => d.RuleId == "if-expr-wrapper")).IsTrue();
    }


    [Test]
    public async Task IfExprWrapperRule_FoldedBlockScalar_NoAutoFix()
    {
        // Folded block scalar `if: >\n  expr1 ||\n  expr2` must NOT offer auto-fix
        var yaml = "on: push\njobs:\n  build:\n    if: >\n      always() && (needs.a.result != 'skipped' ||\n      needs.b.result != 'skipped')\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n";
        using var result = new LintEngine([new IfExprWrapperRule()]).Check(
            Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-expr-wrapper").ToArray();

        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        // Folded block scalar must not offer auto-fix (would break YAML structure)
        await Assert.That(diagnostics[0].Fix is null).IsTrue();
    }


    [Test]
    public async Task IfExprWrapperRule_FoldedBlockScalar_MessageDoesNotContainNewline()
    {
        // Folded block scalar diagnostic message must not contain raw newlines
        var yaml = "on: push\njobs:\n  build:\n    if: >\n      always() && (needs.a.result != 'skipped' ||\n      needs.b.result != 'skipped')\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n";
        using var result = new LintEngine([new IfExprWrapperRule()]).Check(
            Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-expr-wrapper").ToArray();

        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        // Message must not contain literal newline
        await Assert.That(diagnostics[0].Message).DoesNotContain("\n");
        // Message must contain the actual expression content (not garbage from other keys)
        await Assert.That(diagnostics[0].Message).Contains("always()");
        await Assert.That(diagnostics[0].Message).DoesNotContain("runs-on");
    }


    [Test]
    public async Task IfExprWrapperRule_FoldedBlockScalar_FixDoesNotCorruptFile()
    {
        // Regression: folded block scalar fix must not be offered (would corrupt adjacent YAML keys)
        var yaml = "on: push\njobs:\n  conclusion:\n    needs:\n      - activation\n    if: >\n      always() && (needs.activation.result != 'skipped' ||\n      needs.activation.outputs.failed == 'true')\n    runs-on: ubuntu-slim\n    permissions:\n      contents: read\n    steps:\n      - run: echo ok\n";
        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new IfExprWrapperRule()]);
        using var result = engine.Check(sourceBytes, "test.yaml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-expr-wrapper").ToArray();

        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        // Block scalar must NOT offer auto-fix — doing so would corrupt runs-on: and other keys
        await Assert.That(diagnostics[0].Fix is null).IsTrue();
    }


    [Test]
    public async Task IfExprWrapperRule_FoldedBlockScalar_StripChomping_NoAutoFix()
    {
        // Strip chomping `>-` removes trailing \n — still must NOT offer auto-fix
        // because source content spans multiple lines (internal newlines exist)
        var yaml = "on: push\njobs:\n  build:\n    if: >-\n      always() && (needs.a.result != 'skipped' ||\n      needs.b.result != 'skipped')\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n";
        using var result = new LintEngine([new IfExprWrapperRule()]).Check(
            Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-expr-wrapper").ToArray();

        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        // Even with strip chomping (no trailing \n), multi-line block scalar must not offer fix
        await Assert.That(diagnostics[0].Fix is null).IsTrue();
    }


    [Test]
    public async Task IfExprWrapperRule_FoldedBlockScalar_Crlf_NoAutoFix()
    {
        // Folded block scalar with CRLF line endings must NOT offer auto-fix
        var yaml = "on: push\r\njobs:\r\n  build:\r\n    if: >\r\n      always() && (needs.a.result != 'skipped' ||\r\n      needs.b.result != 'skipped')\r\n    runs-on: ubuntu-latest\r\n    steps:\r\n      - run: echo ok\r\n";
        using var result = new LintEngine([new IfExprWrapperRule()]).Check(
            Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-expr-wrapper").ToArray();

        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        await Assert.That(diagnostics[0].Fix is null).IsTrue();
        await Assert.That(diagnostics[0].Message).DoesNotContain("\n");
        await Assert.That(diagnostics[0].Message).DoesNotContain("\r");
    }
}

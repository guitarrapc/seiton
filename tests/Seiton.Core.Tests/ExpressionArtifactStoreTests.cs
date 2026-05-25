using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>
/// Tests for the expression artifact store contract: verifies that pre-parsed expression
/// results from the parser can be consumed by the linter without re-parsing.
/// </summary>
public sealed class ExpressionArtifactStoreTests
{
    [Test]
    public async Task Store_TryGet_ReturnsPreParsedResult()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ github.sha }}\n"u8.ToArray();

        // Simulate parser populating the artifact store
        var store = new ExpressionArtifactStore(4);
        var expressionBody = "github.sha"u8;
        var bodyOffset = System.Text.Encoding.UTF8.GetString(yaml).IndexOf("github.sha", StringComparison.Ordinal);
        var location = new TextRange(bodyOffset, expressionBody.Length, 6, 22, 6, 32);
        var parseResult = ExpressionParser.Parse(expressionBody);
        var contentHash = ComputeExpressionHash(expressionBody);

        store.Add(new ExpressionArtifact(contentHash, location, ExpressionValidationContext.StepRun, parseResult));

        // Verify retrieval
        var found = store.TryGet(contentHash, expressionBody, yaml, out var retrieved);
        await Assert.That(found).IsTrue();
        await Assert.That(retrieved.HasRoot).IsTrue();
        await Assert.That(retrieved.Nodes.Length).IsEqualTo(parseResult.Nodes.Length);
    }

    [Test]
    public async Task Store_TryGet_ReturnsFalse_WhenHashCollision()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ github.sha }}\n"u8.ToArray();

        var store = new ExpressionArtifactStore(4);
        var expressionBody = "github.sha"u8;
        var bodyOffset = System.Text.Encoding.UTF8.GetString(yaml).IndexOf("github.sha", StringComparison.Ordinal);
        var location = new TextRange(bodyOffset, expressionBody.Length, 6, 22, 6, 32);
        var parseResult = ExpressionParser.Parse(expressionBody);
        var contentHash = ComputeExpressionHash(expressionBody);

        store.Add(new ExpressionArtifact(contentHash, location, ExpressionValidationContext.StepRun, parseResult));

        // Try to get with different expression bytes but same hash (simulated)
        var differentExpr = "github.ref"u8;
        var found = store.TryGet(contentHash, differentExpr, yaml, out _);
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Store_Count_TracksEntries()
    {
        var store = new ExpressionArtifactStore(4);
        await Assert.That(store.Count).IsEqualTo(0);

        var expr = "github.sha"u8;
        var hash = ComputeExpressionHash(expr);
        var parseResult = ExpressionParser.Parse(expr);
        store.Add(new ExpressionArtifact(hash, default, ExpressionValidationContext.StepRun, parseResult));

        await Assert.That(store.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LintConfig_ParseExpression_ConsultsArtifactStore()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ github.sha }}\n"u8.ToArray();

        // Create store with pre-parsed result
        var store = new ExpressionArtifactStore(4);
        var expressionBody = "github.sha"u8;
        var bodyOffset = System.Text.Encoding.UTF8.GetString(yaml).IndexOf("github.sha", StringComparison.Ordinal);
        var location = new TextRange(bodyOffset, expressionBody.Length, 6, 22, 6, 32);
        var parseResult = ExpressionParser.Parse(expressionBody);
        var contentHash = ComputeExpressionHash(expressionBody);
        store.Add(new ExpressionArtifact(contentHash, location, ExpressionValidationContext.StepRun, parseResult));

        // Wire store into LintConfig
        var config = new LintConfig { Utf8Yaml = yaml, ExpressionArtifacts = store };

        // ParseExpression should return the pre-parsed result without re-parsing
        var result = config.ParseExpression(expressionBody);
        await Assert.That(result.HasRoot).IsTrue();
        await Assert.That(result.Nodes.Length).IsEqualTo(parseResult.Nodes.Length);
        await Assert.That(result.RootNode).IsEqualTo(parseResult.RootNode);
    }

    [Test]
    public async Task LintConfig_ParseExpression_FallsBackToCache_WhenNoArtifacts()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ github.sha }}\n"u8.ToArray();
        var config = new LintConfig { Utf8Yaml = yaml };

        // No artifact store set — should fall back to existing cache mechanism
        var expressionBody = "github.sha"u8;
        var result = config.ParseExpression(expressionBody);
        await Assert.That(result.HasRoot).IsTrue();
    }

    [Test]
    public async Task ParseResultData_ExpressionArtifacts_DefaultsToNull()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n"u8.ToArray();

        var result = WorkflowParser.ParseDirect(yaml, "test.yml", out var arena);

        // Current parser does not populate artifacts by default
        await Assert.That(result.ExpressionArtifacts).IsNull();
        arena?.Dispose();
    }

    [Test]
    public async Task Store_TryGet_ReturnsFalse_WhenLocationExceedsSourceBounds()
    {
        var yaml = "short"u8.ToArray();

        var store = new ExpressionArtifactStore(4);
        var expressionBody = "github.sha"u8;
        var contentHash = ComputeExpressionHash(expressionBody);

        // Location that exceeds source length
        var outOfBoundsLocation = new TextRange(100, 10, 1, 1, 1, 11);
        var parseResult = ExpressionParser.Parse(expressionBody);
        store.Add(new ExpressionArtifact(contentHash, outOfBoundsLocation, ExpressionValidationContext.StepRun, parseResult));

        // Should return false instead of throwing
        var found = store.TryGet(contentHash, expressionBody, yaml, out _);
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Store_TryGet_ReturnsFalse_WhenLocationStartIsNegative()
    {
        var yaml = "short"u8.ToArray();

        var store = new ExpressionArtifactStore(4);
        var expressionBody = "github.sha"u8;
        var contentHash = ComputeExpressionHash(expressionBody);

        var invalidLocation = new TextRange(-1, 1, 1, 1, 1, 2);
        var parseResult = ExpressionParser.Parse(expressionBody);
        store.Add(new ExpressionArtifact(contentHash, invalidLocation, ExpressionValidationContext.StepRun, parseResult));

        var found = store.TryGet(contentHash, expressionBody, yaml, out _);
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Store_TryGet_ReturnsFalse_WhenLocationLengthIsNegative()
    {
        var yaml = "short"u8.ToArray();

        var store = new ExpressionArtifactStore(4);
        var expressionBody = "github.sha"u8;
        var contentHash = ComputeExpressionHash(expressionBody);

        var invalidLocation = new TextRange(0, -1, 1, 1, 1, 1);
        var parseResult = ExpressionParser.Parse(expressionBody);
        store.Add(new ExpressionArtifact(contentHash, invalidLocation, ExpressionValidationContext.StepRun, parseResult));

        var found = store.TryGet(contentHash, expressionBody, yaml, out _);
        await Assert.That(found).IsFalse();
    }

    private static long ComputeExpressionHash(ReadOnlySpan<byte> expression)
    {
        return (long)XxHash64.Hash(expression);
    }
}

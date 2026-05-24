using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>
/// Tests for the shared ExpressionSemanticModel used by lint rules.
/// Verifies context availability, function availability, and diagnostic formatting.
/// </summary>
public sealed class ExpressionSemanticModelTests
{
    [Test]
    public async Task IsContextAvailable_StepsInStepScope_ReturnsTrue()
    {
        var model = new ExpressionSemanticModel();
        var result = model.IsContextAvailable(ExpressionValidationContext.StepRun, "steps"u8);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsContextAvailable_StepsInJobScope_ReturnsFalse()
    {
        var model = new ExpressionSemanticModel();
        var result = model.IsContextAvailable(ExpressionValidationContext.JobIf, "steps"u8);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsContextAvailable_GithubInAnyScope_ReturnsTrue()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsContextAvailable(ExpressionValidationContext.StepRun, "github"u8)).IsTrue();
        await Assert.That(model.IsContextAvailable(ExpressionValidationContext.JobIf, "github"u8)).IsTrue();
        await Assert.That(model.IsContextAvailable(ExpressionValidationContext.Env, "github"u8)).IsTrue();
    }

    [Test]
    public async Task CheckFunctionAvailability_SuccessInIfContext_ReturnsNull()
    {
        var model = new ExpressionSemanticModel();
        var result = model.CheckFunctionAvailability(ExpressionValidationContext.StepIf, "success"u8);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task CheckFunctionAvailability_SuccessOutsideIf_ReturnsDiagnostic()
    {
        var model = new ExpressionSemanticModel();
        var result = model.CheckFunctionAvailability(ExpressionValidationContext.StepRun, "success"u8);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Contains("is not allowed here", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result!.Contains("only available in \"if\" conditions", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task CheckFunctionAvailability_HashFilesAtStepLevel_ReturnsNull()
    {
        var model = new ExpressionSemanticModel();
        var result = model.CheckFunctionAvailability(ExpressionValidationContext.StepRun, "hashFiles"u8);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task CheckFunctionAvailability_HashFilesAtJobLevel_ReturnsDiagnostic()
    {
        var model = new ExpressionSemanticModel();
        var result = model.CheckFunctionAvailability(ExpressionValidationContext.JobEnv, "hashFiles"u8);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Contains("hashFiles", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result!.Contains("step-level", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FormatContextNotAvailable_BuiltinContext_IncludesAvailableList()
    {
        var model = new ExpressionSemanticModel();
        var message = model.FormatContextNotAvailable(ExpressionValidationContext.JobIf, "steps"u8);
        await Assert.That(message.Contains("steps", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains("is not allowed here", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FormatContextNotAvailable_UnknownContext_IncludesUndefined()
    {
        var model = new ExpressionSemanticModel();
        var message = model.FormatContextNotAvailable(ExpressionValidationContext.StepRun, "foo"u8);
        await Assert.That(message.Contains("undefined context", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SemanticModel_AccessibleFromLintConfig()
    {
        var config = new LintConfig();
        await Assert.That(config.SemanticModel).IsNotNull();
    }

    [Test]
    public async Task SemanticModel_SetContext_UpdatesCurrent()
    {
        var model = new ExpressionSemanticModel();
        model.SetContext(ExpressionValidationContext.StepIf);
        await Assert.That(model.CurrentContext).IsEqualTo(ExpressionValidationContext.StepIf);
    }
}

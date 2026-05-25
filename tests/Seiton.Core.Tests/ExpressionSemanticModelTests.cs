using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>
/// Tests for the shared ExpressionSemanticModel used by lint rules.
/// Verifies context availability, function availability, and helper checks.
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
    public async Task IsStatusCheckFunction_Success_ReturnsTrue()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsStatusCheckFunction("success"u8)).IsTrue();
    }

    [Test]
    public async Task IsStatusCheckFunction_RegularFunction_ReturnsFalse()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsStatusCheckFunction("contains"u8)).IsFalse();
    }

    [Test]
    public async Task IsHashFilesFunction_HashFiles_ReturnsTrue()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsHashFilesFunction("hashFiles"u8)).IsTrue();
    }

    [Test]
    public async Task IsHashFilesFunction_OtherFunction_ReturnsFalse()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsHashFilesFunction("contains"u8)).IsFalse();
    }

    [Test]
    public async Task IsIfContext_StepIf_ReturnsTrue()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsIfContext(ExpressionValidationContext.StepIf)).IsTrue();
        await Assert.That(model.IsIfContext(ExpressionValidationContext.JobIf)).IsTrue();
        await Assert.That(model.IsIfContext(ExpressionValidationContext.JobSnapshotIf)).IsTrue();
    }

    [Test]
    public async Task IsIfContext_NonIf_ReturnsFalse()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsIfContext(ExpressionValidationContext.StepRun)).IsFalse();
        await Assert.That(model.IsIfContext(ExpressionValidationContext.JobEnv)).IsFalse();
    }

    [Test]
    public async Task IsStepLevel_StepRun_ReturnsTrue()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsStepLevel(ExpressionValidationContext.StepRun)).IsTrue();
        await Assert.That(model.IsStepLevel(ExpressionValidationContext.StepIf)).IsTrue();
    }

    [Test]
    public async Task IsStepLevel_JobLevel_ReturnsFalse()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsStepLevel(ExpressionValidationContext.JobEnv)).IsFalse();
        await Assert.That(model.IsStepLevel(ExpressionValidationContext.JobIf)).IsFalse();
    }

    [Test]
    public async Task IsBuiltinContext_Github_ReturnsTrue()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsBuiltinContext("github"u8)).IsTrue();
        await Assert.That(model.IsBuiltinContext("steps"u8)).IsTrue();
        await Assert.That(model.IsBuiltinContext("matrix"u8)).IsTrue();
    }

    [Test]
    public async Task IsBuiltinContext_Unknown_ReturnsFalse()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsBuiltinContext("foo"u8)).IsFalse();
    }

    [Test]
    public async Task FormatAvailableContexts_ReturnsNonEmpty()
    {
        var model = new ExpressionSemanticModel();
        var text = model.FormatAvailableContexts(ExpressionValidationContext.JobIf);
        await Assert.That(text.Length > 0).IsTrue();
        await Assert.That(text.Contains("github", StringComparison.Ordinal)).IsTrue();
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

    // --- Equivalence-class tests ---

    [Test]
    public async Task IsContextAvailable_NeedsInJobIf_ReturnsTrue()
    {
        var model = new ExpressionSemanticModel();
        var result = model.IsContextAvailable(ExpressionValidationContext.JobIf, "needs"u8);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsContextAvailable_NeedsInWorkflowEnv_ReturnsFalse()
    {
        // "needs" is not available at workflow-level env
        var model = new ExpressionSemanticModel();
        var result = model.IsContextAvailable(ExpressionValidationContext.Env, "needs"u8);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsStatusCheckFunction_AllStatusFunctions_ReturnsTrue()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsStatusCheckFunction("success"u8)).IsTrue();
        await Assert.That(model.IsStatusCheckFunction("failure"u8)).IsTrue();
        await Assert.That(model.IsStatusCheckFunction("cancelled"u8)).IsTrue();
        await Assert.That(model.IsStatusCheckFunction("always"u8)).IsTrue();
    }

    [Test]
    public async Task IsStatusCheckFunction_CaseInsensitive_ReturnsTrue()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsStatusCheckFunction("SUCCESS"u8)).IsTrue();
        await Assert.That(model.IsStatusCheckFunction("Failure"u8)).IsTrue();
        await Assert.That(model.IsStatusCheckFunction("CANCELLED"u8)).IsTrue();
        await Assert.That(model.IsStatusCheckFunction("Always"u8)).IsTrue();
    }

    [Test]
    public async Task IsHashFilesFunction_CaseInsensitive_ReturnsTrue()
    {
        var model = new ExpressionSemanticModel();
        await Assert.That(model.IsHashFilesFunction("hashfiles"u8)).IsTrue();
        await Assert.That(model.IsHashFilesFunction("HASHFILES"u8)).IsTrue();
        await Assert.That(model.IsHashFilesFunction("HashFiles"u8)).IsTrue();
    }
}

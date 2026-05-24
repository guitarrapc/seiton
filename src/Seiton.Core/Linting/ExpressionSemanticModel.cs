using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

/// <summary>
/// Shared expression semantic model for lint rules. Provides workflow-aware context resolution
/// so that multiple rules can query expression context availability, function availability,
/// and dynamic type overrides without duplicating logic.
/// </summary>
/// <remarks>
/// Lifetime: one instance per <see cref="LintConfig"/>, reset per lint run via
/// <see cref="PrepareForWorkflow"/>. Rules access it via <see cref="LintConfig.SemanticModel"/>.
/// The model does NOT allocate per expression — it caches per-workflow/per-job state.
/// </remarks>
internal sealed class ExpressionSemanticModel
{
    private ExpressionValidationContext _currentContext;

    /// <summary>
    /// Checks whether a root context identifier (e.g. "steps", "github", "matrix") is available
    /// in the given workflow position.
    /// </summary>
    internal bool IsContextAvailable(ExpressionValidationContext context, ReadOnlySpan<byte> rootName)
    {
        return Availability.IsRootContextAvailable(context, rootName);
    }

    /// <summary>
    /// Checks whether a function is available in the given workflow position.
    /// Returns a diagnostic message if the function is restricted, or null if allowed.
    /// </summary>
    internal string? CheckFunctionAvailability(ExpressionValidationContext context, ReadOnlySpan<byte> funcName)
    {
        // Status check functions: only in if conditions
        var isIfContext = context is ExpressionValidationContext.JobIf
            or ExpressionValidationContext.StepIf
            or ExpressionValidationContext.JobSnapshotIf;

        if (!isIfContext && IsStatusCheckFunction(funcName))
        {
            var funcNameText = System.Text.Encoding.UTF8.GetString(funcName);
            var scopeText = Availability.GetLintCategoryText(context);
            return $"function \"{funcNameText}\" is not allowed here. \"{funcNameText}\" is only available in \"if\" conditions of jobs and steps. called in {scopeText}";
        }

        // hashFiles: only at step level
        if (IsHashFilesFunction(funcName) && !Availability.IsStepLevel(context))
        {
            var scopeText = Availability.GetLintCategoryText(context);
            return $"function \"hashFiles\" is not allowed here. \"hashFiles\" is only available in step-level expressions. called in {scopeText}";
        }

        return null;
    }

    /// <summary>
    /// Formats a context-not-available diagnostic message for a given root context.
    /// </summary>
    internal string FormatContextNotAvailable(ExpressionValidationContext context, ReadOnlySpan<byte> rootName)
    {
        var rootNameText = System.Text.Encoding.UTF8.GetString(rootName);
        var scopeText = Availability.GetLintCategoryText(context);

        if (IsBuiltinContext(rootName))
        {
            var availableText = Availability.FormatAvailableContexts(context);
            return $"context \"{rootNameText}\" is not allowed here. {availableText}. called in {scopeText}";
        }

        return $"context \"{rootNameText}\" is not allowed here. undefined context \"{rootNameText}\". called in {scopeText}";
    }

    /// <summary>
    /// Prepares per-workflow state. Call once at workflow visit start.
    /// </summary>
    internal void PrepareForWorkflow()
    {
        // Currently stateless at workflow level — reserved for future Phase 5 migration
        // when dynamic context overrides are centralized here.
    }

    /// <summary>
    /// Sets the current expression evaluation context (used by rules that evaluate
    /// expressions incrementally).
    /// </summary>
    internal void SetContext(ExpressionValidationContext context)
    {
        _currentContext = context;
    }

    /// <summary>Gets the current expression evaluation context.</summary>
    internal ExpressionValidationContext CurrentContext => _currentContext;

    private static bool IsStatusCheckFunction(ReadOnlySpan<byte> name)
    {
        return name.SequenceEqual("success"u8)
            || name.SequenceEqual("failure"u8)
            || name.SequenceEqual("cancelled"u8)
            || name.SequenceEqual("always"u8);
    }

    private static bool IsHashFilesFunction(ReadOnlySpan<byte> name)
    {
        return name.SequenceEqual("hashFiles"u8);
    }

    private static bool IsBuiltinContext(ReadOnlySpan<byte> name)
    {
        return name.SequenceEqual("github"u8)
            || name.SequenceEqual("env"u8)
            || name.SequenceEqual("vars"u8)
            || name.SequenceEqual("job"u8)
            || name.SequenceEqual("jobs"u8)
            || name.SequenceEqual("steps"u8)
            || name.SequenceEqual("runner"u8)
            || name.SequenceEqual("secrets"u8)
            || name.SequenceEqual("strategy"u8)
            || name.SequenceEqual("matrix"u8)
            || name.SequenceEqual("needs"u8)
            || name.SequenceEqual("inputs"u8);
    }
}

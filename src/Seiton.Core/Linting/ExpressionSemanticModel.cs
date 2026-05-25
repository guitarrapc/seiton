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
    /// Checks whether the given name is a known built-in context (github, env, vars, etc.).
    /// </summary>
    internal bool IsBuiltinContext(ReadOnlySpan<byte> rootName)
    {
        return rootName.SequenceEqual("github"u8)
            || rootName.SequenceEqual("env"u8)
            || rootName.SequenceEqual("vars"u8)
            || rootName.SequenceEqual("job"u8)
            || rootName.SequenceEqual("jobs"u8)
            || rootName.SequenceEqual("steps"u8)
            || rootName.SequenceEqual("runner"u8)
            || rootName.SequenceEqual("secrets"u8)
            || rootName.SequenceEqual("strategy"u8)
            || rootName.SequenceEqual("matrix"u8)
            || rootName.SequenceEqual("needs"u8)
            || rootName.SequenceEqual("inputs"u8);
    }

    /// <summary>
    /// Checks whether the given function name is a status check function (success, failure, cancelled, always).
    /// </summary>
    internal bool IsStatusCheckFunction(ReadOnlySpan<byte> funcName)
    {
        return SpanHelpers.EqualsAsciiIgnoreCase(funcName, "success"u8)
            || SpanHelpers.EqualsAsciiIgnoreCase(funcName, "failure"u8)
            || SpanHelpers.EqualsAsciiIgnoreCase(funcName, "cancelled"u8)
            || SpanHelpers.EqualsAsciiIgnoreCase(funcName, "always"u8);
    }

    /// <summary>
    /// Checks whether the given function name is hashFiles.
    /// </summary>
    internal bool IsHashFilesFunction(ReadOnlySpan<byte> funcName)
    {
        return SpanHelpers.EqualsAsciiIgnoreCase(funcName, "hashfiles"u8);
    }

    /// <summary>
    /// Checks whether the given context is at step level (where hashFiles is available).
    /// </summary>
    internal bool IsStepLevel(ExpressionValidationContext context)
    {
        return Availability.IsStepLevel(context);
    }

    /// <summary>
    /// Checks whether the given context is an "if" condition (where status functions are available).
    /// </summary>
    internal bool IsIfContext(ExpressionValidationContext context)
    {
        return context is ExpressionValidationContext.JobIf
            or ExpressionValidationContext.StepIf
            or ExpressionValidationContext.JobSnapshotIf;
    }

    /// <summary>
    /// Formats the available contexts for a diagnostic message.
    /// </summary>
    internal string FormatAvailableContexts(ExpressionValidationContext context)
    {
        return Availability.FormatAvailableContexts(context);
    }

    /// <summary>
    /// Prepares per-workflow state. Call once at workflow visit start.
    /// </summary>
    internal void PrepareForWorkflow()
    {
        // Currently stateless at workflow level — reserved for future use
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
}

using Seiton.Core.Generated;
using Seiton.Core.Parsing;

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
public sealed class ExpressionSemanticModel
{
    private ExpressionValidationContext _currentContext;

    /// <summary>
    /// Checks whether a root context identifier (e.g. "steps", "github", "matrix") is available
    /// in the given workflow position.
    /// </summary>
    public bool IsContextAvailable(ExpressionValidationContext context, ReadOnlySpan<byte> rootName)
    {
        return Availability.IsRootContextAvailable(context, rootName);
    }

    /// <summary>
    /// Checks whether the given name is a known built-in context (github, env, vars, etc.).
    /// Uses case-insensitive comparison and the generated <see cref="ContextTypes.BuiltinContextTypes"/> list.
    /// </summary>
    public bool IsBuiltinContext(ReadOnlySpan<byte> rootName)
    {
        var builtins = ContextTypes.BuiltinContextTypes;
        for (var i = 0; i < builtins.Length; i++)
        {
            if (SpanHelpers.EqualsAsciiIgnoreCase(rootName, builtins[i].NameUtf8))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the given function name is a status check function (success, failure, cancelled, always).
    /// </summary>
    public bool IsStatusCheckFunction(ReadOnlySpan<byte> funcName)
    {
        return SpanHelpers.EqualsAsciiIgnoreCase(funcName, "success"u8)
            || SpanHelpers.EqualsAsciiIgnoreCase(funcName, "failure"u8)
            || SpanHelpers.EqualsAsciiIgnoreCase(funcName, "cancelled"u8)
            || SpanHelpers.EqualsAsciiIgnoreCase(funcName, "always"u8);
    }

    /// <summary>
    /// Checks whether the given function name is hashFiles.
    /// </summary>
    public bool IsHashFilesFunction(ReadOnlySpan<byte> funcName)
    {
        return SpanHelpers.EqualsAsciiIgnoreCase(funcName, "hashfiles"u8);
    }

    /// <summary>
    /// Checks whether the given context is at step level (where hashFiles is available).
    /// </summary>
    public bool IsStepLevel(ExpressionValidationContext context)
    {
        return Availability.IsStepLevel(context);
    }

    /// <summary>
    /// Checks whether the given context is an "if" condition (where status functions are available).
    /// </summary>
    public bool IsIfContext(ExpressionValidationContext context)
    {
        return context is ExpressionValidationContext.JobIf
            or ExpressionValidationContext.StepIf
            or ExpressionValidationContext.JobSnapshotIf;
    }

    /// <summary>
    /// Formats the available contexts for a diagnostic message.
    /// </summary>
    public string FormatAvailableContexts(ExpressionValidationContext context)
    {
        return Availability.FormatAvailableContexts(context);
    }

    /// <summary>
    /// Prepares per-workflow state. Call once at workflow visit start.
    /// </summary>
    public void PrepareForWorkflow()
    {
        // Currently stateless at workflow level — reserved for future use
        // when dynamic context overrides are centralized here.
    }

    /// <summary>
    /// Sets the current expression evaluation context (used by rules that evaluate
    /// expressions incrementally).
    /// </summary>
    public void SetContext(ExpressionValidationContext context)
    {
        _currentContext = context;
    }

    /// <summary>Gets the current expression evaluation context.</summary>
    public ExpressionValidationContext CurrentContext => _currentContext;
}

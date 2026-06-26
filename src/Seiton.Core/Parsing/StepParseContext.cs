using System.Runtime.CompilerServices;
using Seiton.Core.Generated;

namespace Seiton.Core.Parsing;

/// <summary>Parse context for workflow job steps, parallel children, or composite action steps.</summary>
internal enum StepParseContext : byte
{
    WorkflowJobStep = 0,
    ParallelChild = 1,
    CompositeActionStep = 2,
}

internal static class StepParseContextRules
{
    internal const string WorkflowMissingPrimaryMessage =
        "must run script with \"run\" section or run action with \"uses\" section, or use \"wait\", \"wait-all\", \"cancel\", or \"parallel\"";

    internal const string RestrictedMissingPrimaryMessage =
        "must run script with \"run\" section or run action with \"uses\" section";

    internal const string RestrictedExpectedKeys =
        "\"continue-on-error\", \"env\", \"id\", \"if\", \"name\", \"run\", \"shell\", \"timeout-minutes\", \"uses\", \"with\", \"working-directory\"";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsRestricted(StepParseContext context)
        => context is not StepParseContext.WorkflowJobStep;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string GetMissingPrimaryMessage(StepParseContext context)
        => IsRestricted(context) ? RestrictedMissingPrimaryMessage : WorkflowMissingPrimaryMessage;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsPrimaryFormAllowed(StepParseContext context, StepSchema.FormId form)
        => !IsRestricted(context) || form is StepSchema.FormId.Run or StepSchema.FormId.Uses;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsBackgroundModifierAllowed(StepParseContext context, StepSchema.FormId? form)
        => context == StepParseContext.WorkflowJobStep
            && form is StepSchema.FormId.Run or StepSchema.FormId.Uses;

    internal static string GetScopeDescription(StepParseContext context)
        => context switch
        {
            StepParseContext.ParallelChild => "step in parallel group",
            StepParseContext.CompositeActionStep => "step in composite action",
            _ => "step",
        };
}

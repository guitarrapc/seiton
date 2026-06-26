using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class StepSchemaCSharpGenerator
{
    public string Generate(StepSchemaModel model)
    {
        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-step-schema");
        sb.AppendLine(
            """
            namespace Seiton.Core.Generated;

            internal static class StepSchema
            {
            """);

        sb.AppendLine("    internal enum FormId : byte");
        sb.AppendLine("    {");
        for (var i = 0; i < model.Forms.Count; i++)
        {
            var form = model.Forms[i];
            var name = ToFormEnumName(form.Id);
            sb.AppendLine($"        {name} = {i},");
        }

        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var form in model.Forms)
        {
            var constantName = ToFormKeysConstantName(form.Id);
            var quotedKeys = form.AllowedKeys.Select(static k => $"\\\"{k}\\\"").ToArray();
            var value = string.Join(", ", quotedKeys);
            sb.AppendLine($"    /// <summary>Allowed keys for step form '{form.Id}'</summary>");
            sb.AppendLine($"    internal const string {constantName} = \"{value}\";");
            sb.AppendLine();
        }

        sb.AppendLine("    /// <summary>Legacy alias for uses-form step keys.</summary>");
        sb.AppendLine("    internal const string ActionStepKeys = UsesStepKeys;");
        sb.AppendLine();

        sb.AppendLine("    internal static string GetUnexpectedKeyDescription(FormId formId) => formId switch");
        sb.AppendLine("    {");
        foreach (var form in model.Forms)
        {
            sb.AppendLine($"        FormId.{ToFormEnumName(form.Id)} => \"{Escape(form.UnexpectedKeyDescription)}\",");
        }

        sb.AppendLine("        _ => \"step\",");
        sb.AppendLine("    };");
        sb.AppendLine();

        if (model.Modifiers.Count > 0)
        {
            sb.AppendLine("    internal static bool IsModifierAllowed(FormId formId, ReadOnlySpan<byte> keyUtf8)");
            sb.AppendLine("    {");
            foreach (var modifier in model.Modifiers)
            {
                var formChecks = modifier.AllowedOnFormIds
                    .Select(ToFormEnumName)
                    .Select(static name => $"FormId.{name}")
                    .ToList();
                var formGuard = formChecks.Count switch
                {
                    0 => "false",
                    1 => $"formId is {formChecks[0]}",
                    _ => $"formId is {string.Join(" or ", formChecks)}",
                };
                sb.AppendLine($"        if (keyUtf8.SequenceEqual(\"{modifier.Key}\"u8))");
                sb.AppendLine($"            return {formGuard};");
            }

            sb.AppendLine("        return false;");
            sb.AppendLine("    }");
        }
        else
        {
            sb.AppendLine("    internal static bool IsModifierAllowed(FormId formId, ReadOnlySpan<byte> keyUtf8) => false;");
        }

        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    private static string ToFormEnumName(string formId) => formId switch
    {
        "run" => "Run",
        "uses" => "Uses",
        "wait" => "Wait",
        "wait-all" => "WaitAll",
        "cancel" => "Cancel",
        "parallel" => "Parallel",
        _ => throw new InvalidOperationException($"Unsupported step form id '{formId}'."),
    };

    private static string ToFormKeysConstantName(string formId) => formId switch
    {
        "run" => "RunStepKeys",
        "uses" => "UsesStepKeys",
        "wait" => "WaitStepKeys",
        "wait-all" => "WaitAllStepKeys",
        "cancel" => "CancelStepKeys",
        "parallel" => "ParallelStepKeys",
        _ => throw new InvalidOperationException($"Unsupported step form id '{formId}'."),
    };

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

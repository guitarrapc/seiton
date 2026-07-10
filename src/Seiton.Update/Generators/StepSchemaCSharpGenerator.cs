using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class StepSchemaCSharpGenerator
{
    public string Generate(StepSchemaModel model)
    {
        var mappingKeys = CollectMappingKeys(model);
        var primaryKeyToForm = model.Forms.ToDictionary(
            static f => f.PrimaryKey,
            static f => f.Id,
            StringComparer.Ordinal);

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-step-schema");
        sb.AppendLine(
            """
            namespace Seiton.Core.Generated;

            internal static class StepSchema
            {
            """);

        AppendFormIdEnum(sb, model);
        AppendFormKeyConstants(sb, model);
        AppendGetExpectedKeys(sb, model);
        AppendUnexpectedKeyDescription(sb, model);
        AppendMappingKeyEnum(sb, mappingKeys);
        AppendMappingKeyTable(sb, mappingKeys);
        AppendIsKnownMappingKey(sb, mappingKeys);
        AppendIsPrimaryMappingKey(sb, mappingKeys, primaryKeyToForm);
        AppendPrimaryFormForMappingKey(sb, mappingKeys, primaryKeyToForm);
        AppendIsModifierAllowed(sb, model);

        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    private static IReadOnlyList<string> CollectMappingKeys(StepSchemaModel model)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var form in model.Forms)
        {
            foreach (var key in form.AllowedKeys)
            {
                keys.Add(key);
            }
        }

        return keys.OrderBy(static k => k, StringComparer.Ordinal).ToList();
    }

    private static void AppendFormIdEnum(StringBuilder sb, StepSchemaModel model)
    {
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
    }

    private static void AppendFormKeyConstants(StringBuilder sb, StepSchemaModel model)
    {
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
    }

    private static void AppendGetExpectedKeys(StringBuilder sb, StepSchemaModel model)
    {
        sb.AppendLine("    internal static string GetExpectedKeys(FormId formId) => formId switch");
        sb.AppendLine("    {");
        foreach (var form in model.Forms)
        {
            sb.AppendLine($"        FormId.{ToFormEnumName(form.Id)} => {ToFormKeysConstantName(form.Id)},");
        }

        sb.AppendLine("        _ => RunStepKeys,");
        sb.AppendLine("    };");
        sb.AppendLine();
    }

    private static void AppendUnexpectedKeyDescription(StringBuilder sb, StepSchemaModel model)
    {
        sb.AppendLine("    internal static string GetUnexpectedKeyDescription(FormId formId) => formId switch");
        sb.AppendLine("    {");
        foreach (var form in model.Forms)
        {
            sb.AppendLine($"        FormId.{ToFormEnumName(form.Id)} => \"{Escape(form.UnexpectedKeyDescription)}\",");
        }

        sb.AppendLine("        _ => \"step\",");
        sb.AppendLine("    };");
        sb.AppendLine();
    }

    private static void AppendMappingKeyEnum(StringBuilder sb, IReadOnlyList<string> mappingKeys)
    {
        sb.AppendLine("    internal enum MappingKey : byte");
        sb.AppendLine("    {");
        for (var i = 0; i < mappingKeys.Count; i++)
        {
            sb.AppendLine($"        {ToMappingKeyEnumName(mappingKeys[i])} = {i},");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void AppendMappingKeyTable(StringBuilder sb, IReadOnlyList<string> mappingKeys)
    {
        sb.AppendLine("    internal readonly struct MappingKeyTable : global::Seiton.Core.Parsing.IUtf8OrderedKeyTable");
        sb.AppendLine("    {");
        sb.AppendLine($"        public static int KeyCount => {mappingKeys.Count};");
        sb.AppendLine();
        sb.AppendLine("        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch");
        sb.AppendLine("        {");
        for (var i = 0; i < mappingKeys.Count; i++)
        {
            sb.AppendLine($"            {i} => \"{mappingKeys[i]}\"u8,");
        }

        sb.AppendLine("            _ => ReadOnlySpan<byte>.Empty,");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void AppendIsKnownMappingKey(StringBuilder sb, IReadOnlyList<string> mappingKeys)
    {
        sb.AppendLine("    internal static bool IsKnownMappingKey(ReadOnlySpan<byte> keyUtf8)");
        sb.AppendLine("    {");
        if (mappingKeys.Count == 0)
        {
            sb.AppendLine("        return false;");
        }
        else
        {
            sb.Append("        return ");
            for (var i = 0; i < mappingKeys.Count; i++)
            {
                if (i > 0)
                {
                    sb.AppendLine();
                    sb.Append("            || ");
                }

                sb.Append($"keyUtf8.SequenceEqual(\"{mappingKeys[i]}\"u8)");
            }

            sb.AppendLine(";");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void AppendIsPrimaryMappingKey(
        StringBuilder sb,
        IReadOnlyList<string> mappingKeys,
        IReadOnlyDictionary<string, string> primaryKeyToForm)
    {
        var primaryEnumNames = mappingKeys
            .Where(primaryKeyToForm.ContainsKey)
            .Select(ToMappingKeyEnumName)
            .ToList();

        sb.AppendLine("    internal static bool IsPrimaryMappingKey(MappingKey key) => key switch");
        sb.AppendLine("    {");
        if (primaryEnumNames.Count > 0)
        {
            sb.AppendLine($"        MappingKey.{string.Join(" or MappingKey.", primaryEnumNames)} => true,");
        }

        sb.AppendLine("        _ => false,");
        sb.AppendLine("    };");
        sb.AppendLine();
    }

    private static void AppendPrimaryFormForMappingKey(
        StringBuilder sb,
        IReadOnlyList<string> mappingKeys,
        IReadOnlyDictionary<string, string> primaryKeyToForm)
    {
        sb.AppendLine("    internal static FormId PrimaryFormForMappingKey(MappingKey key) => key switch");
        sb.AppendLine("    {");
        foreach (var key in mappingKeys)
        {
            if (!primaryKeyToForm.TryGetValue(key, out var formId))
            {
                continue;
            }

            sb.AppendLine($"        MappingKey.{ToMappingKeyEnumName(key)} => FormId.{ToFormEnumName(formId)},");
        }

        sb.AppendLine("        _ => FormId.Run,");
        sb.AppendLine("    };");
        sb.AppendLine();
    }

    private static void AppendIsModifierAllowed(StringBuilder sb, StepSchemaModel model)
    {
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

    private static string ToMappingKeyEnumName(string key)
    {
        if (key.Length == 0)
        {
            throw new InvalidOperationException("Step mapping key must not be empty.");
        }

        if (!key.Contains('-', StringComparison.Ordinal))
        {
            if (key.Length == 1)
            {
                return char.ToUpperInvariant(key[0]).ToString();
            }

            return char.ToUpperInvariant(key[0]) + key[1..];
        }

        return string.Concat(key.Split('-').Select(static segment =>
            char.ToUpperInvariant(segment[0]) + segment[1..]));
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

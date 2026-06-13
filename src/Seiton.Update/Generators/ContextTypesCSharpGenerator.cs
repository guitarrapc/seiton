using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class ContextTypesCSharpGenerator
{
    public string Generate(ContextTypesModel model)
    {
        var contexts = model.Contexts
            .OrderBy(static x => x.Name, StringComparer.Ordinal)
            .ToArray();

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-context-types");
        sb.AppendLine(
            """
            using Seiton.Core.Parsing;

            namespace Seiton.Core.Generated;

            internal static class ContextTypes
            {
                internal static readonly (byte[] NameUtf8, ExprType Type)[] BuiltinContextTypes = BuildBuiltinContextTypes();

                private static (byte[] NameUtf8, ExprType Type)[] BuildBuiltinContextTypes()
                {
            """);

        // Emit helper variables for nested types before the return array
        foreach (var ctx in contexts)
        {
            EmitContextVariable(sb, ctx);
        }

        // Emit return array
        sb.AppendLine();
        sb.AppendLine("        return");
        sb.AppendLine("        [");
        foreach (var ctx in contexts)
        {
            sb.AppendLine($"            (\"{ctx.Name}\"u8.ToArray(), (ExprType){VariableName(ctx.Name)}Type),");
        }
        sb.AppendLine("        ];");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    private void EmitContextVariable(StringBuilder sb, ContextEntry ctx)
    {
        var varName = $"{VariableName(ctx.Name)}Type";

        if (ctx.Properties is null or { Count: 0 } && ctx.DynamicPropertyType is not null)
        {
            // Simple mapped object (env, secrets, vars, steps, matrix, needs, inputs)
            var dynType = ToExprTypeCode(ctx.DynamicPropertyType);
            sb.AppendLine($"        var {varName} = ExprType.Object(dynamicPropertyType: {dynType});");
            return;
        }

        if (ctx.Properties is { Count: > 0 })
        {
            // Object with known properties — may have nested objects
            EmitObjectWithProperties(sb, varName, ctx.Properties, ctx.Strict == true, ctx.DynamicPropertyType, "        ");
            return;
        }

        // Fallback: empty object
        sb.AppendLine($"        var {varName} = ExprType.EmptyObject;");
    }

    private void EmitObjectWithProperties(
        StringBuilder sb,
        string varName,
        IReadOnlyList<ContextPropertyEntry> properties,
        bool strict,
        string? dynamicPropertyType,
        string indent)
    {
        // First emit any nested object variables
        foreach (var prop in properties)
        {
            if (prop.Type == "object" && prop.Properties is { Count: > 0 })
            {
                var nestedVarName = $"{varName}_{SanitizeName(prop.Name)}";
                EmitObjectWithProperties(sb, nestedVarName, prop.Properties, prop.Strict == true, prop.DynamicPropertyType, indent);
            }
            else if (prop.Type == "object" && prop.DynamicPropertyObject is not null)
            {
                var dynObj = prop.DynamicPropertyObject;
                var nestedVarName = $"{varName}_{SanitizeName(prop.Name)}_value";
                EmitObjectWithProperties(sb, nestedVarName, dynObj.Properties ?? [], dynObj.Strict == true, null, indent);
            }
        }

        // Build the properties dictionary
        sb.AppendLine($"{indent}var {varName} = ExprType.Object(");
        sb.AppendLine($"{indent}    new Dictionary<Utf8String, ExprType>");
        sb.AppendLine($"{indent}    {{");

        foreach (var prop in properties)
        {
            var typeExpr = GetPropertyTypeExpression(varName, prop);
            sb.AppendLine($"{indent}        {{ new Utf8String(\"{prop.Name}\"u8), {typeExpr} }},");
        }

        sb.Append($"{indent}    }}");

        if (dynamicPropertyType is not null || strict)
        {
            sb.AppendLine(",");
            var extraArgs = new List<string>();
            if (dynamicPropertyType is not null)
            {
                extraArgs.Add($"dynamicPropertyType: {ToExprTypeCode(dynamicPropertyType)}");
            }
            if (strict)
            {
                extraArgs.Add("strict: true");
            }
            sb.AppendLine($"{indent}    {string.Join(", ", extraArgs)});");
        }
        else
        {
            sb.AppendLine(");");
        }
    }

    private string GetPropertyTypeExpression(string parentVarName, ContextPropertyEntry prop)
    {
        if (prop.Type == "object")
        {
            if (prop.Properties is { Count: > 0 })
            {
                return $"{parentVarName}_{SanitizeName(prop.Name)}";
            }

            if (prop.DynamicPropertyObject is not null)
            {
                return $"ExprType.Object(dynamicPropertyType: {parentVarName}_{SanitizeName(prop.Name)}_value)";
            }

            if (prop.DynamicPropertyType is not null)
            {
                return $"ExprType.Object(dynamicPropertyType: {ToExprTypeCode(prop.DynamicPropertyType)})";
            }

            return "ExprType.EmptyObject";
        }

        return ToExprTypeCode(prop.Type);
    }

    private static string ToExprTypeCode(string typeName)
    {
        return typeName switch
        {
            "any" => "ExprType.Any",
            "bool" => "ExprType.Bool",
            "number" => "ExprType.Number",
            "string" => "ExprType.String",
            "null" => "ExprType.Null",
            _ => throw new InvalidOperationException($"Unknown type: {typeName}"),
        };
    }

    private static string VariableName(string contextName)
    {
        // Convert context name to camelCase variable name
        return contextName switch
        {
            "github" => "github",
            "env" => "env",
            "job" => "job",
            "runner" => "runner",
            "secrets" => "secrets",
            "strategy" => "strategy",
            "steps" => "steps",
            "matrix" => "matrix",
            "needs" => "needs",
            "inputs" => "inputs",
            "vars" => "vars",
            _ => contextName.Replace("-", "_"),
        };
    }

    private static string SanitizeName(string name) => name.Replace("-", "_").Replace(".", "_");
}

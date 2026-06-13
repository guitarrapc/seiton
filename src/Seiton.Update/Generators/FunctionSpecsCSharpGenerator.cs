using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class FunctionSpecsCSharpGenerator
{
    public string Generate(FunctionSpecsModel model)
    {
        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-function-specs");
        sb.AppendLine(
            """
            using Seiton.Core.Parsing;

            namespace Seiton.Core.Generated;

            internal static class FunctionSpecs
            {
            """);

        var functions = model.Functions
            .OrderBy(static x => x.Name, StringComparer.Ordinal)
            .ToArray();

        sb.AppendLine("    internal static readonly ExpressionSemanticAnalyzer.FunctionSpec[] Specs =");
        sb.AppendLine("    [");

        foreach (var func in functions)
        {
            sb.AppendLine($"        new ExpressionSemanticAnalyzer.FunctionSpec(\"{func.Name}\"u8.ToArray(),");
            sb.AppendLine("        [");

            foreach (var overload in func.Overloads)
            {
                var returnTypeExpr = ToExprTypeCode(overload.ReturnType);
                var paramExprs = overload.Params.Select(ToExprTypeCode).ToList();
                var paramsJoined = string.Join(", ", paramExprs);

                if (overload.VariadicParam is not null)
                {
                    var variadicExpr = ToExprTypeCode(overload.VariadicParam);
                    sb.AppendLine($"            new ExpressionSemanticAnalyzer.FuncOverload({returnTypeExpr}, [{paramsJoined}], {variadicExpr}),");
                }
                else
                {
                    sb.AppendLine($"            new ExpressionSemanticAnalyzer.FuncOverload({returnTypeExpr}, [{paramsJoined}]),");
                }
            }

            sb.AppendLine("        ]),");
        }

        sb.AppendLine("    ];");
        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
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
            "array<any>" => "ExprType.ArrayOf(ExprType.Any)",
            _ => throw new InvalidOperationException($"Unknown type: {typeName}"),
        };
    }
}

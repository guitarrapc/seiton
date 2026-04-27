using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class EventPayloadTypesCSharpGenerator
{
    public string Generate(EventPayloadTypesModel model)
    {
        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-event-payload-types");
        sb.AppendLine(
            """
            using Seiton.Core.Parsing;

            namespace Seiton.Core.Generated;

            internal static class EventPayloadTypes
            {
                internal static readonly ObjectExprType DefaultEventType = ExprType.Object(dynamicPropertyType: ExprType.Any);

            """);

        // Emit static fields for each event type
        foreach (var ev in model.Events)
        {
            EmitEventType(sb, ev);
        }

        // Emit the lookup method
        sb.AppendLine();
        sb.AppendLine("    internal static bool TryGetEventPayloadType(ReadOnlySpan<byte> eventNameUtf8, out ObjectExprType payloadType)");
        sb.AppendLine("    {");

        foreach (var ev in model.Events)
        {
            var fieldName = ToFieldName(ev.Name);
            sb.AppendLine($"        if (SpanHelpers.EqualsAsciiIgnoreCase(eventNameUtf8, \"{ev.Name}\"u8))");
            sb.AppendLine("        {");
            sb.AppendLine($"            payloadType = {fieldName};");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        payloadType = DefaultEventType;");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");

        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    private static void EmitEventType(StringBuilder sb, EventPayloadEntry ev)
    {
        var fieldName = ToFieldName(ev.Name);

        sb.AppendLine($"    private static readonly ObjectExprType {fieldName} = ExprType.Object(");
        sb.AppendLine("        new Dictionary<Utf8String, ExprType>");
        sb.AppendLine("        {");

        foreach (var prop in ev.Properties)
        {
            var typeExpr = ToExprTypeCode(prop);
            sb.AppendLine($"            {{ new Utf8String(\"{prop.Name}\"u8), {typeExpr} }},");
        }

        sb.AppendLine("        },");
        sb.AppendLine("        dynamicPropertyType: ExprType.Any);");
        sb.AppendLine();
    }

    private static string ToExprTypeCode(EventPayloadPropertyEntry prop)
    {
        return prop.Type switch
        {
            "string" => "ExprType.String",
            "number" => "ExprType.Number",
            "bool" => "ExprType.Bool",
            "object" => "ExprType.Object(dynamicPropertyType: ExprType.Any)",
            "array" when prop.ElementType is not null => $"ExprType.ArrayOf({ToElementTypeCode(prop.ElementType)})",
            "array" => "ExprType.ArrayOf(ExprType.Any)",
            "any" => "ExprType.Any",
            _ => throw new InvalidOperationException($"Unknown property type: {prop.Type}"),
        };
    }

    private static string ToElementTypeCode(EventPayloadElementTypeEntry elementType)
    {
        return elementType.Type switch
        {
            "string" => "ExprType.String",
            "number" => "ExprType.Number",
            "bool" => "ExprType.Bool",
            "object" => "ExprType.Object(dynamicPropertyType: ExprType.Any)",
            "any" => "ExprType.Any",
            _ => throw new InvalidOperationException($"Unknown element type: {elementType.Type}"),
        };
    }

    private static string ToFieldName(string eventName)
    {
        var parts = eventName.Split('_');
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length > 0)
            {
                sb.Append(char.ToUpperInvariant(part[0]));
                sb.Append(part, 1, part.Length - 1);
            }
        }
        sb.Append("EventType");
        return sb.ToString();
    }
}

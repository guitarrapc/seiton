using System.Text.Json;
using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

public sealed class GitHubWorkflowStepSchemaParserInferValueKindTests
{
    [Test]
    public async Task InferValueKind_EnvRef_ReturnsEnvMapping()
    {
        var kind = Infer("with", """{ "$ref": "#/definitions/env" }""", out _);
        await Assert.That(kind).IsEqualTo("envMapping");
    }

    [Test]
    public async Task InferValueKind_IfProperty_ReturnsStepIf()
    {
        var kind = Infer("if", """{ "type": ["boolean", "number", "string"] }""", out _);
        await Assert.That(kind).IsEqualTo("stepIf");
    }

    [Test]
    public async Task InferValueKind_BooleanType_ReturnsBoolean()
    {
        var kind = Infer("background", """{ "type": "boolean" }""", out _);
        await Assert.That(kind).IsEqualTo("boolean");
    }

    [Test]
    public async Task InferValueKind_StringType_ReturnsString()
    {
        var kind = Infer("run", """{ "type": "string" }""", out var ctx);
        await Assert.That(kind).IsEqualTo("string");
        await Assert.That(ctx).IsEqualTo("StepRun");
    }

    [Test]
    public async Task InferValueKind_CancelString_ReturnsNonEmptyString()
    {
        var kind = Infer("cancel", """{ "type": "string" }""", out _);
        await Assert.That(kind).IsEqualTo("nonEmptyString");
    }

    [Test]
    public async Task InferValueKind_ContinueOnErrorOneOf_ReturnsBoolOrExpression()
    {
        var kind = Infer(
            "continue-on-error",
            """
            {
              "oneOf": [
                { "type": "boolean" },
                { "$ref": "#/definitions/expressionSyntax" }
              ]
            }
            """,
            out _);
        await Assert.That(kind).IsEqualTo("boolOrExpression");
    }

    [Test]
    public async Task InferValueKind_TimeoutMinutesOneOf_ReturnsFloatOrExpression()
    {
        var kind = Infer(
            "timeout-minutes",
            """
            {
              "oneOf": [
                { "type": "number" },
                { "$ref": "#/definitions/expressionSyntax" }
              ]
            }
            """,
            out _);
        await Assert.That(kind).IsEqualTo("floatOrExpression");
    }

    [Test]
    public async Task InferValueKind_ShellAnyOf_ReturnsString()
    {
        var kind = Infer("shell", """{ "$ref": "#/definitions/shell" }""", out var ctx);
        await Assert.That(kind).IsEqualTo("string");
        await Assert.That(ctx).IsEqualTo("StepShell");
    }

    [Test]
    public async Task InferValueKind_WaitAllNullaryType_ReturnsNullary()
    {
        var kind = Infer("wait-all", """{ "type": ["boolean", "null"] }""", out _);
        await Assert.That(kind).IsEqualTo("nullary");
    }

    [Test]
    public void InferValueKind_StringNullUnion_IsNotNullary()
    {
        Assert.Throws<InvalidDataException>(() =>
            Infer("name", """{ "type": ["string", "null"] }""", out _));
    }

    [Test]
    public async Task InferValueKind_WaitOneOf_ReturnsStringOrNonEmptyStringArray()
    {
        var kind = Infer(
            "wait",
            """
            {
              "oneOf": [
                { "type": "string" },
                {
                  "type": "array",
                  "items": { "type": "string" },
                  "minItems": 1
                }
              ]
            }
            """,
            out _);
        await Assert.That(kind).IsEqualTo("stringOrNonEmptyStringArray");
    }

    [Test]
    public async Task InferValueKind_ParallelStepArray_ReturnsNonEmptyStepArray()
    {
        var kind = Infer(
            "parallel",
            """
            {
              "type": "array",
              "items": { "$ref": "#/definitions/step" },
              "minItems": 1
            }
            """,
            out _);
        await Assert.That(kind).IsEqualTo("nonEmptyStepArray");
    }

    [Test]
    public void InferValueKind_UnsupportedOneOf_Throws()
    {
        Assert.Throws<InvalidDataException>(() =>
            Infer(
                "custom",
                """
                {
                  "oneOf": [
                    { "type": "number" },
                    { "type": "object" }
                  ]
                }
                """,
                out _));
    }

    private static string Infer(string propertyName, string propertySchemaJson, out string? expressionContext)
    {
        using var doc = JsonDocument.Parse(
            $$"""
            {
              "definitions": {
                "env": {
                  "oneOf": [
                    { "type": "object", "additionalProperties": { "type": "string" } },
                    { "$ref": "#/definitions/expressionSyntax" }
                  ]
                },
                "expressionSyntax": { "type": "string" },
                "shell": {
                  "anyOf": [
                    { "type": "string" },
                    { "type": "string", "enum": ["bash", "sh"] }
                  ]
                },
                "step": {
                  "type": "object",
                  "properties": {
                    "{{propertyName}}": {{propertySchemaJson}}
                  }
                }
              }
            }
            """);

        var definitions = doc.RootElement.GetProperty("definitions");
        var property = definitions.GetProperty("step").GetProperty("properties").GetProperty(propertyName);
        return GitHubWorkflowStepSchemaParser.InferValueKind(propertyName, property, definitions, out expressionContext);
    }
}

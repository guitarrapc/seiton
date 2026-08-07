using Seiton.Update.Generators;
using Seiton.Update.Model;

namespace Seiton.Update.Tests;

public sealed class PermissionsCSharpGeneratorTests
{
    [Test]
    public async Task Generate_WithDeprecationNote_EmitsSpanLookup()
    {
        var model = new PermissionsModel(
        [
            new PermissionScopeModel("contents", ["read", "write", "none"]),
            new PermissionScopeModel("models", ["read", "none"], "retired: remove it"),
        ]);

        var output = new PermissionsCSharpGenerator().Generate(model);

        await Assert.That(output).Contains("internal static string? GetDeprecationNote(ReadOnlySpan<byte> scopeNameUtf8)");
        await Assert.That(output).Contains("scopeNameUtf8.SequenceEqual(\"models\"u8)");
        await Assert.That(output).Contains("return \"retired: remove it\";");
    }

    [Test]
    public async Task Generate_WithoutDeprecationNote_EmitsNullOnlyLookup()
    {
        var model = new PermissionsModel([new PermissionScopeModel("contents", ["read", "write", "none"])]);

        var output = new PermissionsCSharpGenerator().Generate(model);

        await Assert.That(output).Contains("internal static string? GetDeprecationNote(ReadOnlySpan<byte> scopeNameUtf8)");

        // The lookup body is just the null return when nothing is deprecated.
        var body = output[output.IndexOf("GetDeprecationNote(", StringComparison.Ordinal)..];
        await Assert.That(body).DoesNotContain("SequenceEqual");
    }

    /// <summary>
    /// C# treats NEL and the Unicode line separators as line terminators, so they break a
    /// regular string literal exactly like CR/LF do.
    /// </summary>
    [Test]
    [Arguments('\u0085')]
    [Arguments('\u2028')]
    [Arguments('\u2029')]
    public async Task Generate_NoteWithUnicodeLineTerminator_EmitsEscapedLiteral(char terminator)
    {
        var model = new PermissionsModel(
        [
            new PermissionScopeModel("models", ["read", "none"], $"line one.{terminator}line two."),
        ]);

        var output = new PermissionsCSharpGenerator().Generate(model);

        await Assert.That(output).DoesNotContain(terminator.ToString());
        await Assert.That(output).Contains($"\\u{(int)terminator:x4}");
    }

    /// <summary>
    /// A note containing newlines or quotes must not break the generated string literal.
    /// The generated file is compiled as part of Seiton.Core, and <c>verify-permissions</c>
    /// only compares text, so an unterminated literal would reach the build unnoticed.
    /// </summary>
    [Test]
    public async Task Generate_NoteWithNewlinesAndQuotes_EmitsEscapedSingleLineLiteral()
    {
        var model = new PermissionsModel(
        [
            new PermissionScopeModel("models", ["read", "none"], "line one.\r\nline \"two\"\tend\\"),
        ]);

        var output = new PermissionsCSharpGenerator().Generate(model);

        await Assert.That(output).Contains("return \"line one.\\r\\nline \\\"two\\\"\\tend\\\\\";");

        // The literal stays on one line: a raw newline would make it unterminated.
        var returnLine = output.Split('\n').Single(static l => l.TrimStart().StartsWith("return \"", StringComparison.Ordinal));
        await Assert.That(returnLine.TrimEnd('\r')).EndsWith("\";");
    }
}

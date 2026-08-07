using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

/// <summary>
/// The canonical snapshot is the codegen input, so anything it accepts is emitted verbatim into
/// C# literals. These tests pin the validation that keeps a malformed snapshot from producing a
/// generated file that does not compile.
/// </summary>
public sealed class PermissionsSourceParserTests
{
    [Test]
    public async Task Parse_ValidSnapshot_ReturnsScopesSortedByName()
    {
        var path = WriteSnapshot("""
            {
              "scopes": [
                { "name": "contents", "allowed": ["read", "write", "none"] },
                { "name": "actions", "allowed": ["read", "write", "none"], "deprecationNote": "gone" }
              ]
            }
            """);

        try
        {
            var model = new PermissionsSourceParser().Parse(path);

            await Assert.That(model.Scopes.Select(static s => s.Name)).IsEquivalentTo(new[] { "actions", "contents" });
            await Assert.That(model.Scopes[0].DeprecationNote).IsEqualTo("gone");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Duplicate names become duplicate switch labels in the generated file (CS8510).</summary>
    [Test]
    public async Task Parse_DuplicateScopeName_Throws()
    {
        var path = WriteSnapshot("""
            {
              "scopes": [
                { "name": "contents", "allowed": ["read", "write", "none"] },
                { "name": "contents", "allowed": ["read", "write", "none"] }
              ]
            }
            """);

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => new PermissionsSourceParser().Parse(path));
            await Assert.That(ex!.Message).Contains("contents");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    [Arguments("Contents")]
    [Arguments("foo bar")]
    [Arguments("foo\"bar")]
    [Arguments("foo\\bar")]
    [Arguments("-leading-dash")]
    public async Task Parse_InvalidScopeName_Throws(string name)
    {
        var path = WriteSnapshot($$"""
            {
              "scopes": [
                { "name": {{System.Text.Json.JsonSerializer.Serialize(name)}}, "allowed": ["read", "none"] }
              ]
            }
            """);

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => new PermissionsSourceParser().Parse(path));
            await Assert.That(ex!.Message).Contains("scope name");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    [Arguments("READ")]
    [Arguments("read write")]
    [Arguments("read\"")]
    public async Task Parse_InvalidAccessValue_Throws(string value)
    {
        var path = WriteSnapshot($$"""
            {
              "scopes": [
                { "name": "contents", "allowed": [{{System.Text.Json.JsonSerializer.Serialize(value)}}] }
              ]
            }
            """);

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => new PermissionsSourceParser().Parse(path));
            await Assert.That(ex!.Message).Contains("access value");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteSnapshot(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "seiton-perm-src-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}

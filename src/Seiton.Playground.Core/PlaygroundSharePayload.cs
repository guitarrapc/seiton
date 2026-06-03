using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Seiton.Playground;

/// <summary>
/// URL hash codec for playground Share links (v2 JSON + legacy v1 raw YAML).
/// Kept in sync with <c>wwwroot/share-payload.js</c>.
/// </summary>
public static class PlaygroundSharePayload
{
    /// <summary>Current share payload version.</summary>
    public const int PayloadVersion = 2;
    /// <summary>Default virtual file path for workflow documents.</summary>
    public const string DefaultFilePath = ".github/workflows/test.yml";

    /// <summary>Max hash segment length (# excluded) before P2 fallback.</summary>
    public const int MaxHashLength = 16_384;

    /// <summary>Max full URL length (path + query + hash) before P2 fallback.</summary>
    public const int MaxUrlLength = 8_192;

    /// <summary>Share payload state restored from URL hash.</summary>
    /// <param name="Yaml">Workflow YAML document.</param>
    /// <param name="Config">Optional playground config YAML.</param>
    /// <param name="FilePath">Selected virtual file path.</param>
    public sealed record State(string Yaml, string Config, string FilePath);

    /// <summary>Encodes v2 share payload (JSON + zlib + base64url).</summary>
    public static string Encode(State state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var yaml = state.Yaml ?? "";
        var config = state.Config ?? "";
        var path = string.IsNullOrWhiteSpace(state.FilePath) ? DefaultFilePath : state.FilePath.Trim();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", PayloadVersion);
            writer.WriteString("y", yaml);
            if (config.Length > 0)
            {
                writer.WriteString("c", config);
            }

            if (!string.Equals(path, DefaultFilePath, StringComparison.Ordinal))
            {
                writer.WriteString("p", path);
            }

            writer.WriteEndObject();
        }

        return CompressToHashSegment(stream.ToArray());
    }

    /// <summary>Encodes YAML-only v2 share payload (config omitted).</summary>
    public static string EncodeYamlOnly(string yaml, string? filePath = null)
    {
        var path = string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath.Trim();
        return Encode(new State(yaml ?? "", "", path));
    }

    /// <summary>Legacy v1: deflate UTF-8 YAML only (standard base64, not base64url).</summary>
    public static string EncodeLegacyYamlOnly(string yaml)
    {
        var bytes = Encoding.UTF8.GetBytes(yaml ?? "");
        return CompressToLegacyHashSegment(bytes);
    }

    /// <summary>
    /// Decodes a share hash segment (v2 preferred, v1 legacy fallback).
    /// Returns <see langword="false"/> only when the hash cannot be decoded/decompressed.
    /// </summary>
    public static bool TryDecode(string hashSegment, out State? state, out string? error)
    {
        state = null;
        error = null;
        if (string.IsNullOrWhiteSpace(hashSegment))
        {
            error = "empty hash";
            return false;
        }

        byte[] compressed;
        try
        {
            compressed = TryDecodeHashToBytes(hashSegment);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        byte[] decompressed;
        try
        {
            decompressed = Inflate(compressed);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        var text = Encoding.UTF8.GetString(decompressed);
        if (TryParseV2Json(text, out state))
        {
            return true;
        }

        state = new State(text, "", DefaultFilePath);
        return true;
    }

    /// <summary>Returns true when the hash segment is below the P2 fallback threshold.</summary>
    public static bool IsHashWithinLimits(string hashSegment)
        => hashSegment.Length <= MaxHashLength;

    /// <summary>Returns true when the full URL is below the P2 fallback threshold.</summary>
    public static bool IsUrlWithinLimits(string url)
        => url.Length <= MaxUrlLength;

    /// <summary>Builds clipboard fallback text containing workflow/config payloads.</summary>
    public static string FormatClipboardBundle(string yaml, string config, string filePath)
    {
        var path = string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath.Trim();
        var sb = new StringBuilder();
        sb.Append("# seiton playground — paste workflow into the editor and config into the config panel\n");
        sb.Append("--- workflow: ").Append(path).Append(" ---\n");
        var y = yaml ?? "";
        sb.Append(y);
        if (!y.EndsWith('\n'))
        {
            sb.Append('\n');
        }

        if (!string.IsNullOrEmpty(config))
        {
            sb.Append("--- config ---\n");
            sb.Append(config);
            if (!config.EndsWith('\n'))
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private static bool TryParseV2Json(string text, out State? state)
    {
        state = null;
        if (text.Length == 0 || text[0] != '{')
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("v", out var vEl) || vEl.GetInt32() != PayloadVersion)
            {
                return false;
            }

            if (!root.TryGetProperty("y", out var yEl) || yEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var yaml = yEl.GetString() ?? "";
            var config = root.TryGetProperty("c", out var cEl) && cEl.ValueKind == JsonValueKind.String
                ? cEl.GetString() ?? ""
                : "";
            var path = root.TryGetProperty("p", out var pEl) && pEl.ValueKind == JsonValueKind.String
                ? pEl.GetString() ?? DefaultFilePath
                : DefaultFilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = DefaultFilePath;
            }

            state = new State(yaml, config, path);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CompressToHashSegment(ReadOnlySpan<byte> jsonUtf8)
    {
        var compressed = Deflate(jsonUtf8);
        return Base64UrlEncode(compressed);
    }

    private static string CompressToLegacyHashSegment(ReadOnlySpan<byte> yamlUtf8)
    {
        var compressed = Deflate(yamlUtf8);
        return Convert.ToBase64String(compressed);
    }

    private static byte[] TryDecodeHashToBytes(string hashSegment)
    {
        if (hashSegment.Contains('+', StringComparison.Ordinal) || hashSegment.Contains('/', StringComparison.Ordinal))
        {
            return Convert.FromBase64String(hashSegment);
        }

        return Base64UrlDecode(hashSegment);
    }

    private static byte[] Deflate(ReadOnlySpan<byte> input)
    {
        using var output = new MemoryStream();
        using (var deflate = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(input);
        }

        return output.ToArray();
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var inflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string hashSegment)
    {
        var padded = hashSegment.Replace('-', '+').Replace('_', '/');
        var mod = padded.Length % 4;
        if (mod > 0)
        {
            padded = padded.PadRight(padded.Length + (4 - mod), '=');
        }

        return Convert.FromBase64String(padded);
    }
}

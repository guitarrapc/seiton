using System.Text.RegularExpressions;

namespace Seiton.Core.Tests;

/// <summary>Parsed expectation line from an actionlint <c>.out</c> file.</summary>
internal readonly record struct ExpectedLine(string Pattern, bool IsRegex);

/// <summary>Result of matching seiton output lines against expected lines.</summary>
internal sealed class MatchResult
{
    public int ExactMatchCount { get; set; }
    public int LineMatchCount { get; set; }
    public int NearLineMatchCount { get; set; }
    public List<ExpectedLine> UnmatchedExpected { get; } = [];
    public List<string> ExtraSeiton { get; } = [];
}

/// <summary>
/// Shared utilities for comparing seiton lint output against actionlint <c>.out</c> expectation files.
/// Used by both <see cref="ActionlintCompatTests"/> (err/) and <see cref="ActionlintExamplesCompatTests"/> (examples/).
/// </summary>
internal static class ActionlintCompatMatcher
{
    /// <summary>
    /// Reads YAML fixture bytes with newline normalization to LF (0x0A).
    /// Golden actionlint <c>.out</c> / <c>.seiton.out</c> files assume LF; a Windows checkout with
    /// <c>core.autocrlf</c> rewrites YAML to CRLF and shifts byte indexes and embedded newlines in diagnostics.
    /// </summary>
    public static byte[] ReadYamlUtf8Normalized(string path) =>
        NormalizeUtf8NewlinesToLf(File.ReadAllBytes(path));

    /// <summary>Replaces CR and CRLF with LF in UTF-8 bytes (no-op if no CR present).</summary>
    public static byte[] NormalizeUtf8NewlinesToLf(byte[] utf8)
    {
        var needsWork = false;
        for (var i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] == (byte)'\r')
            {
                needsWork = true;
                break;
            }
        }

        if (!needsWork)
        {
            return utf8;
        }

        using var ms = new MemoryStream(utf8.Length);
        for (var i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] == (byte)'\r')
            {
                if (i + 1 < utf8.Length && utf8[i + 1] == (byte)'\n')
                {
                    i++;
                }

                ms.WriteByte((byte)'\n');
            }
            else
            {
                ms.WriteByte(utf8[i]);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Parses an actionlint <c>.out</c> file into expectation lines.
    /// Lines wrapped in <c>/pattern/</c> are treated as regex; others are literal.
    /// </summary>
    public static List<ExpectedLine> ParseOutFile(string outPath)
    {
        var rawLines = File.ReadAllLines(outPath);
        var expectations = new List<ExpectedLine>(rawLines.Length);
        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length >= 2 && line[0] == '/' && line[^1] == '/')
            {
                expectations.Add(new ExpectedLine(line[1..^1], IsRegex: true));
            }
            else
            {
                expectations.Add(new ExpectedLine(line, IsRegex: false));
            }
        }

        return expectations;
    }

    /// <summary>
    /// Matches seiton output lines against expected lines using a three-pass algorithm.
    /// <para>Pass 1: Exact/regex match — each expected line consumes at most one seiton line.</para>
    /// <para>Pass 2: Line-number match — remaining expected lines are matched by YAML line number.
    /// This accounts for design differences where seiton reports at a different column or
    /// with a different message format than actionlint for the same YAML line.</para>
    /// <para>Pass 3: Near-line match — remaining expected lines are matched by nearby line (±5)
    /// with the same rule ID. This accounts for position differences where seiton reports the
    /// same issue at a slightly different line. Line 0 expectations (position unknown in
    /// actionlint) match any seiton line with the same rule ID.</para>
    /// </summary>
    public static MatchResult Match(List<string> seitonLines, List<ExpectedLine> expectations, string? fixtureName = null)
    {
        var result = new MatchResult();
        var seitonMatched = new bool[seitonLines.Count];

        var normalized = NormalizeExpectations(expectations, fixtureName);

        // Pass 1: Exact/regex match
        var pass1Unmatched = new List<ExpectedLine>();
        for (var i = 0; i < normalized.Count; i++)
        {
            var expected = normalized[i];
            var found = false;

            for (var j = 0; j < seitonLines.Count; j++)
            {
                if (seitonMatched[j])
                {
                    continue;
                }

                if (IsMatch(seitonLines[j], expected))
                {
                    seitonMatched[j] = true;
                    found = true;
                    result.ExactMatchCount++;
                    break;
                }
            }

            if (!found)
            {
                pass1Unmatched.Add(expected);
            }
        }

        // Pass 2: Line-number match for remaining unmatched expectations.
        var pass2Unmatched = new List<ExpectedLine>();
        foreach (var expected in pass1Unmatched)
        {
            var expectedLineNum = ExtractExpectedLineNumber(expected);
            if (expectedLineNum < 0)
            {
                pass2Unmatched.Add(expected);
                continue;
            }

            var found = false;
            for (var j = 0; j < seitonLines.Count; j++)
            {
                if (seitonMatched[j])
                {
                    continue;
                }

                var seitonLineNum = ExtractLineNumber(seitonLines[j]);
                if (seitonLineNum == expectedLineNum)
                {
                    seitonMatched[j] = true;
                    found = true;
                    result.LineMatchCount++;
                    break;
                }
            }

            if (!found)
            {
                pass2Unmatched.Add(expected);
            }
        }

        // Pass 3: Near-line match with same rule ID for remaining unmatched expectations.
        foreach (var expected in pass2Unmatched)
        {
            var expectedLineNum = ExtractExpectedLineNumber(expected);
            var expectedRuleId = ExtractExpectedRuleId(expected);

            if (expectedRuleId == null)
            {
                result.UnmatchedExpected.Add(expected);
                continue;
            }

            var bestIdx = -1;
            var bestDistance = int.MaxValue;

            for (var j = 0; j < seitonLines.Count; j++)
            {
                if (seitonMatched[j])
                {
                    continue;
                }

                var seitonRuleId = ExtractRuleId(seitonLines[j]);
                if (!string.Equals(seitonRuleId, expectedRuleId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (expectedLineNum == 0)
                {
                    bestIdx = j;
                    break;
                }

                if (expectedLineNum > 0)
                {
                    var seitonLineNum = ExtractLineNumber(seitonLines[j]);
                    var distance = Math.Abs(seitonLineNum - expectedLineNum);
                    if (distance <= 5 && distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIdx = j;
                    }
                }
            }

            if (bestIdx >= 0)
            {
                seitonMatched[bestIdx] = true;
                result.NearLineMatchCount++;
            }
            else
            {
                result.UnmatchedExpected.Add(expected);
            }
        }

        // Collect unmatched seiton lines
        for (var j = 0; j < seitonLines.Count; j++)
        {
            if (!seitonMatched[j])
            {
                result.ExtraSeiton.Add(seitonLines[j]);
            }
        }

        return result;
    }

    public static List<ExpectedLine> NormalizeExpectations(List<ExpectedLine> expectations, string? fixtureName)
    {
        if (fixtureName == null)
        {
            return expectations;
        }

        var fixturePrefix = $"{fixtureName}.yaml:";
        var needsNormalization = false;
        for (var i = 0; i < expectations.Count; i++)
        {
            if (!expectations[i].IsRegex && expectations[i].Pattern.StartsWith(fixturePrefix, StringComparison.Ordinal))
            {
                needsNormalization = true;
                break;
            }
        }

        if (!needsNormalization)
        {
            return expectations;
        }

        var normalized = new List<ExpectedLine>(expectations.Count);
        for (var i = 0; i < expectations.Count; i++)
        {
            var e = expectations[i];
            if (!e.IsRegex && e.Pattern.StartsWith(fixturePrefix, StringComparison.Ordinal))
            {
                normalized.Add(new ExpectedLine("test.yaml:" + e.Pattern[fixturePrefix.Length..], e.IsRegex));
            }
            else
            {
                normalized.Add(e);
            }
        }

        return normalized;
    }

    /// <summary>Extracts the <c>[rule-id]</c> tag from the end of a formatted line.</summary>
    public static string? ExtractRuleId(string line)
    {
        var end = line.LastIndexOf(']');
        if (end < 1)
        {
            return null;
        }

        var start = line.LastIndexOf('[', end - 1);
        if (start < 0)
        {
            return null;
        }

        return line[(start + 1)..end];
    }

    /// <summary>Extracts the rule ID from an expected line (regex or literal).</summary>
    public static string? ExtractExpectedRuleId(ExpectedLine expected)
    {
        if (expected.IsRegex)
        {
            var match = Regex.Match(expected.Pattern, @"\\\[([^\]\\]+)\\\]$");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            var match2 = Regex.Match(expected.Pattern, @"\[([^\]]+)\]$");
            return match2.Success ? match2.Groups[1].Value : null;
        }

        return ExtractRuleId(expected.Pattern);
    }

    /// <summary>Extracts the YAML line number from a formatted seiton line (test.yaml:LINE:COL: ...).</summary>
    public static int ExtractLineNumber(string formattedLine)
    {
        if (formattedLine.StartsWith("test.yaml:", StringComparison.Ordinal))
        {
            var colonIdx = formattedLine.IndexOf(':', 10); // after "test.yaml:"
            if (colonIdx > 10 && int.TryParse(formattedLine.AsSpan(10, colonIdx - 10), out var lineNum))
            {
                return lineNum;
            }
        }

        return -1;
    }

    /// <summary>Extracts the YAML line number from an expected line (regex or literal).</summary>
    public static int ExtractExpectedLineNumber(ExpectedLine expected)
    {
        if (expected.IsRegex)
        {
            var match = Regex.Match(expected.Pattern, @"test\\?\.yaml:(\d+):");
            return match.Success ? int.Parse(match.Groups[1].Value) : -1;
        }

        return ExtractLineNumber(expected.Pattern);
    }

    public static bool IsMatch(string actual, ExpectedLine expected)
    {
        if (expected.IsRegex)
        {
            return Regex.IsMatch(actual, expected.Pattern);
        }

        return string.Equals(actual, expected.Pattern, StringComparison.Ordinal);
    }
}

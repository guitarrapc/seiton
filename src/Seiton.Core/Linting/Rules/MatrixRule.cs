using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates <c>strategy.matrix</c> definitions for structural correctness.</summary>
public sealed class MatrixRule() : RuleBase(RuleId.Matrix)
{
    private const long MaxRecommendedCombinations = 256;

    public override string Name => "Matrix Rule";

    public override void VisitJobPre(Job job)
    {
        if (job.Strategy?.Matrix is not Matrix matrix || Config.Utf8Yaml is null)
        {
            return;
        }

        if (Arena.GetStringExpression(matrix.Expression).HasValue || matrix.Rows is null || matrix.Rows.Value.Count == 0)
        {
            return;
        }

        ValidateRows(job, matrix.Rows.Value);
        ValidateCombinations(job, matrix, matrix.Exclude, "exclude");
    }

    private void ValidateRows(Job job, SliceMap<MatrixRow> rows)
    {
        long combinations = 1;
        var combinationWarningReported = false;

        foreach (var pair in rows)
        {
            var row = pair.Value;
            if (Arena.GetStringExpression(row.Expression).HasValue)
            {
                continue;
            }

            var values = row.Values;
            if (values is null || values.Count == 0)
            {
                var jobId = Decode(Arena.GetStringSlice(job.Id));
                var axisName = Decode(Arena.GetStringSlice(row.Name));
                AddJobWarning(
                    job,
                    $"job '{jobId}' strategy.matrix axis '{axisName}' has no values; remove the axis or provide at least one value",
                    Arena.GetStringRange(row.Name));
                continue;
            }

            // Check for duplicate values within this axis
            ValidateNoDuplicateAxisValues(job, row);

            if (combinationWarningReported)
            {
                continue;
            }

            combinations *= values.Count;
            if (combinations <= MaxRecommendedCombinations)
            {
                continue;
            }

            var matrixNode = job.Strategy?.Matrix;
            if (matrixNode is null)
            {
                continue;
            }

            var jobIdForMessage = Decode(Arena.GetStringSlice(job.Id));
            AddJobWarning(
                job,
                $"job '{jobIdForMessage}' strategy.matrix expands to more than {MaxRecommendedCombinations} combinations; consider reducing matrix fan-out",
                matrixNode.Range);
            combinationWarningReported = true;
        }
    }

    private void ValidateNoDuplicateAxisValues(Job job, MatrixRow row)
    {
        var values = row.Values;
        if (values is null || values.Count < 2)
        {
            return;
        }

        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] is not RawYamlString current)
            {
                continue;
            }

            var currentSpan = Arena.GetStringValue(current.Value);
            if (ExpressionScanHelpers.ContainsExpressionMarker(current.Value, Arena))
            {
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                if (values[j] is not RawYamlString earlier)
                {
                    continue;
                }

                if (ExpressionScanHelpers.ContainsExpressionMarker(earlier.Value, Arena))
                {
                    continue;
                }

                if (!currentSpan.SequenceEqual(Arena.GetStringValue(earlier.Value)))
                {
                    continue;
                }

                var jobId = Decode(Arena.GetStringSlice(job.Id));
                var axisName = Decode(Arena.GetStringSlice(row.Name));
                var valueText = Decode(Arena.GetStringSlice(current.Value));
                AddJobWarning(
                    job,
                    $"job '{jobId}' strategy.matrix axis '{axisName}' has duplicate value '{valueText}'",
                    Arena.GetStringRange(current.Value));
                goto nextValue;
            }

        nextValue:;
        }
    }

    private void ValidateCombinations(Job job, Matrix matrix, IReadOnlyList<MatrixCombinations>? combinations, string section)
    {
        if (matrix.Rows is null || combinations is null || combinations.Count == 0)
        {
            return;
        }

        var source = Config.Utf8Yaml!;

        for (var i = 0; i < combinations.Count; i++)
        {
            var combo = combinations[i];
            if (Arena.GetStringExpression(combo.Expression).HasValue || combo.Entries is null)
            {
                continue;
            }

            for (var entryIndex = 0; entryIndex < combo.Entries.Count; entryIndex++)
            {
                var entry = combo.Entries[entryIndex];
                foreach (var pair in entry)
                {
                    var keyBytes = pair.Key.AsSpan(source);

                    // Check if axis exists in Rows
                    if (matrix.Rows.Value.TryGetValue(source, keyBytes, out var row))
                    {
                        ValidateExcludeValueMatch(job, matrix, row, pair.Key, pair.Value, section);
                        continue;
                    }

                    // Check if axis exists in Include entries
                    var includeValues = CollectIncludeAxisValues(matrix, source, keyBytes);
                    if (includeValues is not null)
                    {
                        ValidateExcludeValueMatchAgainstList(job, matrix, pair.Key, pair.Value, includeValues, section);
                        continue;
                    }

                    // Unknown axis
                    var jobId = Decode(Arena.GetStringSlice(job.Id));
                    var axisName = Decode(pair.Key);
                    var keyLocation = BuildKeyLocation(source, pair.Key);
                    AddJobWarning(
                        job,
                        $"job '{jobId}' strategy.matrix.{section} references unknown axis '{axisName}'",
                        keyLocation);
                    goto nextEntry;
                }

            nextEntry:;
            }
        }
    }

    private void ValidateExcludeValueMatch(Job job, Matrix matrix, MatrixRow row, Utf8Slice axisKey, RawYamlValue excludeValue, string section)
    {
        // Skip if row is expression-based or has no values
        if (Arena.GetStringExpression(row.Expression).HasValue || row.Values is null || row.Values.Count == 0)
        {
            return;
        }

        // Skip if exclude value contains an expression
        if (ContainsExpression(excludeValue))
        {
            return;
        }

        var source = Config.Utf8Yaml!;

        // Check if exclude value matches any row value
        for (var i = 0; i < row.Values.Count; i++)
        {
            var rowValue = row.Values[i];
            if (ContainsExpression(rowValue))
            {
                return; // Can't statically verify when row values contain expressions
            }

            if (RawYamlValuesMatch(excludeValue, rowValue, source))
            {
                return; // Match found
            }
        }

        // No match found — report diagnostic
        var jobId = Decode(Arena.GetStringSlice(job.Id));
        var axisName = Decode(axisKey);
        var excludeText = FormatRawYamlValue(excludeValue);
        var possibleText = FormatPossibleValues(row.Values);
        var location = GetRawYamlValueLocation(excludeValue, matrix.Range);
        AddJobWarning(
            job,
            $"value {excludeText} in \"{section}\" does not match in matrix \"{axisName}\" combinations. possible values are {possibleText}",
            location);
    }

    private void ValidateExcludeValueMatchAgainstList(Job job, Matrix matrix, Utf8Slice axisKey, RawYamlValue excludeValue, List<RawYamlValue> possibleValues, string section)
    {
        // Skip if exclude value contains an expression
        if (ContainsExpression(excludeValue))
        {
            return;
        }

        var source = Config.Utf8Yaml!;

        for (var i = 0; i < possibleValues.Count; i++)
        {
            var possible = possibleValues[i];
            if (ContainsExpression(possible))
            {
                return;
            }

            if (RawYamlValuesMatch(excludeValue, possible, source))
            {
                return;
            }
        }

        // No match found
        var axisName = Decode(axisKey);
        var excludeText = FormatRawYamlValue(excludeValue);
        var possibleText = FormatPossibleValues(possibleValues);
        var location = GetRawYamlValueLocation(excludeValue, matrix.Range);
        AddJobWarning(
            job,
            $"value {excludeText} in \"{section}\" does not match in matrix \"{axisName}\" combinations. possible values are {possibleText}",
            location);
    }

    private static List<RawYamlValue>? CollectIncludeAxisValues(Matrix matrix, ReadOnlySpan<byte> source, ReadOnlySpan<byte> axisKey)
    {
        if (matrix.Include is null || matrix.Include.Count == 0)
        {
            return null;
        }

        List<RawYamlValue>? values = null;
        for (var i = 0; i < matrix.Include.Count; i++)
        {
            var combo = matrix.Include[i];
            if (combo.Entries is null)
            {
                continue;
            }

            for (var j = 0; j < combo.Entries.Count; j++)
            {
                if (combo.Entries[j].TryGetValue(source, axisKey, out var val))
                {
                    values ??= [];
                    values.Add(val);
                }
            }
        }

        return values;
    }

    private bool RawYamlValuesMatch(RawYamlValue excludeValue, RawYamlValue rowValue, ReadOnlySpan<byte> source)
    {
        // Both scalars
        if (excludeValue is RawYamlString exStr && rowValue is RawYamlString rwStr)
        {
            return Arena.GetStringValue(exStr.Value).SequenceEqual(Arena.GetStringValue(rwStr.Value));
        }

        // Both objects — partial match (every key in exclude must exist in row with matching value)
        if (excludeValue is RawYamlObject exObj && rowValue is RawYamlObject rwObj)
        {
            foreach (var pair in exObj.Properties)
            {
                if (!rwObj.Properties.TryGetValue(source, pair.Key.AsSpan(source), out var rwVal))
                {
                    return false;
                }

                if (!RawYamlValuesMatch(pair.Value, rwVal, source))
                {
                    return false;
                }
            }

            return true;
        }

        // Both arrays — same length, element-wise match
        if (excludeValue is RawYamlArray exArr && rowValue is RawYamlArray rwArr)
        {
            if (exArr.Items.Count != rwArr.Items.Count)
            {
                return false;
            }

            for (var i = 0; i < exArr.Items.Count; i++)
            {
                if (!RawYamlValuesMatch(exArr.Items[i], rwArr.Items[i], source))
                {
                    return false;
                }
            }

            return true;
        }

        // Type mismatch
        return false;
    }

    private bool ContainsExpression(RawYamlValue value)
    {
        if (value is RawYamlString str)
        {
            return ExpressionScanHelpers.ContainsExpressionMarker(str.Value, Arena);
        }

        return false;
    }

    private string FormatRawYamlValue(RawYamlValue value)
    {
        if (value is RawYamlString str)
        {
            return $"\"{Decode(Arena.GetStringSlice(str.Value))}\"";
        }

        if (value is RawYamlObject obj)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            var sortedEntries = new List<(string Key, string Value)>();
            foreach (var pair in obj.Properties)
            {
                sortedEntries.Add((Decode(pair.Key), FormatRawYamlValue(pair.Value)));
            }

            sortedEntries.Sort(static (a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
            for (var i = 0; i < sortedEntries.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append('"').Append(sortedEntries[i].Key).Append("\": ").Append(sortedEntries[i].Value);
            }

            sb.Append('}');
            return sb.ToString();
        }

        if (value is RawYamlArray arr)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (var i = 0; i < arr.Items.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(FormatRawYamlValue(arr.Items[i]));
            }

            sb.Append(']');
            return sb.ToString();
        }

        return "?";
    }

    private string FormatPossibleValues(IReadOnlyList<RawYamlValue> values)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(FormatRawYamlValue(values[i]));
        }

        return sb.ToString();
    }

    private TextRange GetRawYamlValueLocation(RawYamlValue value, TextRange fallback)
    {
        if (value is RawYamlString str)
        {
            return Arena.GetStringRange(str.Value);
        }

        return fallback;
    }

    private static TextRange BuildKeyLocation(ReadOnlySpan<byte> source, Utf8Slice key)
    {
        var offset = key.Offset;
        var length = key.Length <= 0 ? 1 : key.Length;
        if ((uint)offset >= (uint)source.Length)
        {
            return new TextRange(offset, length, 1, 1, 1, length);
        }

        var endOffset = offset + length - 1;
        if (endOffset >= source.Length)
        {
            endOffset = source.Length - 1;
        }

        var start = SpanHelpers.ComputeLineColumn(source, offset);
        var end = SpanHelpers.ComputeLineColumn(source, endOffset);
        return new TextRange(
            Start: offset,
            Length: length,
            StartLine: start.Line,
            StartColumn: start.Column,
            EndLine: end.Line,
            EndColumn: end.Column);
    }
}

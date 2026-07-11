using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates <c>strategy.matrix</c> definitions for structural correctness.</summary>
public sealed class MatrixRule() : RuleBase(RuleId.Matrix)
{
    private const long MaxRecommendedCombinations = 256;

    public override string Name => "Matrix Rule";

    public override void VisitJobPre(JobRef job)
    {
        var matrix = job.Strategy.Matrix;
        if (!matrix.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        if (matrix.Expression.Expression.HasValue || matrix.Rows.Count == 0)
        {
            return;
        }

        ValidateRows(job, matrix.Rows);
        ValidateCombinations(job, matrix, matrix.Exclude, "exclude");
    }

    private void ValidateRows(JobRef job, MatrixRowRefMap rows)
    {
        long combinations = 1;
        var combinationWarningReported = false;

        foreach (var pair in rows)
        {
            var row = pair.Value;
            if (row.Expression.Expression.HasValue)
            {
                continue;
            }

            var values = row.Values;
            if (values.Count == 0)
            {
                var jobId = job.Id.Decode();
                var axisName = row.Name.Decode();
                AddJobWarning(
                    job,
                    $"jobs.'{jobId}'.strategy.matrix axis '{axisName}' has no values; remove the axis or provide at least one value",
                    row.Name.Range);
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

            var matrixNode = job.Strategy.Matrix;
            if (!matrixNode.HasValue)
            {
                continue;
            }

            var jobIdForMessage = job.Id.Decode();
            AddJobWarning(
                job,
                $"jobs.'{jobIdForMessage}'.strategy.matrix expands to more than {MaxRecommendedCombinations} combinations; consider reducing matrix fan-out",
                matrixNode.Range);
            combinationWarningReported = true;
        }
    }

    private void ValidateNoDuplicateAxisValues(JobRef job, MatrixRowRef row)
    {
        var values = row.Values;
        if (values.Count < 2)
        {
            return;
        }

        for (var i = 1; i < values.Count; i++)
        {
            var current = values[i];
            if (current.Kind != RawYamlKind.String)
            {
                continue;
            }

            var currentSpan = current.Scalar.Value;
            if (ExpressionScanHelpers.ContainsExpressionMarker(current.Scalar.Id, Arena))
            {
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                var earlier = values[j];
                if (earlier.Kind != RawYamlKind.String)
                {
                    continue;
                }

                if (ExpressionScanHelpers.ContainsExpressionMarker(earlier.Scalar.Id, Arena))
                {
                    continue;
                }

                if (!currentSpan.SequenceEqual(earlier.Scalar.Value))
                {
                    continue;
                }

                var jobId = job.Id.Decode();
                var axisName = row.Name.Decode();
                var valueText = current.Scalar.Decode();
                AddJobWarning(
                    job,
                    $"jobs.'{jobId}'.strategy.matrix axis '{axisName}' has duplicate value '{valueText}'",
                    current.Scalar.Range);
                goto nextValue;
            }

        nextValue:;
        }
    }

    private void ValidateCombinations(JobRef job, MatrixRef matrix, CombinationsRefList combinations, string section)
    {
        if (!matrix.Rows.HasValue || combinations.Count == 0)
        {
            return;
        }

        var source = Config.Utf8Yaml!;

        for (var i = 0; i < combinations.Count; i++)
        {
            var combo = combinations[i];
            if (combo.Expression.Expression.HasValue || !combo.Entries.HasValue)
            {
                continue;
            }

            for (var entryIndex = 0; entryIndex < combo.Entries.Count; entryIndex++)
            {
                var entry = combo.Entries[entryIndex];
                foreach (var pair in entry)
                {
                    var keyBytes = pair.Key.Bytes;

                    // Check if axis exists in Rows
                    if (matrix.Rows.TryGetValue(keyBytes, out var row))
                    {
                        ValidateExcludeValueMatch(job, matrix, row, pair.Key.Slice, pair.Value, section);
                        continue;
                    }

                    // Check if axis exists in Include entries
                    var includeValues = CollectIncludeAxisValues(matrix, keyBytes);
                    if (includeValues is not null)
                    {
                        ValidateExcludeValueMatchAgainstList(job, matrix, pair.Key.Slice, pair.Value, includeValues, section);
                        continue;
                    }

                    // Unknown axis
                    var jobId = job.Id.Decode();
                    var axisName = pair.Key.Decode();
                    var keyLocation = BuildKeyLocation(source, pair.Key.Slice);
                    AddJobWarning(
                        job,
                        $"jobs.'{jobId}'.strategy.matrix.{section} references unknown axis '{axisName}'",
                        keyLocation);
                    goto nextEntry;
                }

            nextEntry:;
            }
        }
    }

    private void ValidateExcludeValueMatch(JobRef job, MatrixRef matrix, MatrixRowRef row, Utf8Slice axisKey, RawYamlRef excludeValue, string section)
    {
        // Skip if row is expression-based or has no values
        if (row.Expression.Expression.HasValue || row.Values.Count == 0)
        {
            return;
        }

        // Skip if exclude value contains an expression
        if (ContainsExpression(excludeValue))
        {
            return;
        }

        // Check if exclude value matches any row value
        for (var i = 0; i < row.Values.Count; i++)
        {
            var rowValue = row.Values[i];
            if (ContainsExpression(rowValue))
            {
                return; // Can't statically verify when row values contain expressions
            }

            if (RawYamlValuesMatch(excludeValue, rowValue))
            {
                return; // Match found
            }
        }

        // No match found — report diagnostic
        var jobId = job.Id.Decode();
        var axisName = Decode(axisKey);
        var excludeText = FormatRawYamlValue(excludeValue);
        var possibleText = FormatPossibleValues(row.Values);
        var location = GetRawYamlValueLocation(excludeValue, matrix.Range);
        AddJobWarning(
            job,
            $"value {excludeText} in \"{section}\" does not match in matrix \"{axisName}\" combinations. possible values are {possibleText}",
            location);
    }

    private void ValidateExcludeValueMatchAgainstList(JobRef job, MatrixRef matrix, Utf8Slice axisKey, RawYamlRef excludeValue, List<RawYamlRef> possibleValues, string section)
    {
        // Skip if exclude value contains an expression
        if (ContainsExpression(excludeValue))
        {
            return;
        }

        for (var i = 0; i < possibleValues.Count; i++)
        {
            var possible = possibleValues[i];
            if (ContainsExpression(possible))
            {
                return;
            }

            if (RawYamlValuesMatch(excludeValue, possible))
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

    private static List<RawYamlRef>? CollectIncludeAxisValues(MatrixRef matrix, ReadOnlySpan<byte> axisKey)
    {
        if (matrix.Include.Count == 0)
        {
            return null;
        }

        List<RawYamlRef>? values = null;
        for (var i = 0; i < matrix.Include.Count; i++)
        {
            var combo = matrix.Include[i];
            if (!combo.Entries.HasValue)
            {
                continue;
            }

            for (var j = 0; j < combo.Entries.Count; j++)
            {
                if (combo.Entries[j].TryGetValue(axisKey, out var val))
                {
                    values ??= [];
                    values.Add(val);
                }
            }
        }

        return values;
    }

    private bool RawYamlValuesMatch(RawYamlRef excludeValue, RawYamlRef rowValue)
    {
        // Both scalars
        if (excludeValue.Kind == RawYamlKind.String && rowValue.Kind == RawYamlKind.String)
        {
            return excludeValue.Scalar.Value.SequenceEqual(rowValue.Scalar.Value);
        }

        // Both objects — partial match (every key in exclude must exist in row with matching value)
        if (excludeValue.Kind == RawYamlKind.Object && rowValue.Kind == RawYamlKind.Object)
        {
            foreach (var pair in excludeValue.Properties)
            {
                if (!rowValue.Properties.TryGetValue(pair.Key.Bytes, out var rwVal))
                {
                    return false;
                }

                if (!RawYamlValuesMatch(pair.Value, rwVal))
                {
                    return false;
                }
            }

            return true;
        }

        // Both arrays — same length, element-wise match
        if (excludeValue.Kind == RawYamlKind.Array && rowValue.Kind == RawYamlKind.Array)
        {
            if (excludeValue.Items.Count != rowValue.Items.Count)
            {
                return false;
            }

            for (var i = 0; i < excludeValue.Items.Count; i++)
            {
                if (!RawYamlValuesMatch(excludeValue.Items[i], rowValue.Items[i]))
                {
                    return false;
                }
            }

            return true;
        }

        // Type mismatch
        return false;
    }

    private bool ContainsExpression(RawYamlRef value)
    {
        if (value.Kind == RawYamlKind.String)
        {
            return ExpressionScanHelpers.ContainsExpressionMarker(value.Scalar.Id, Arena);
        }

        return false;
    }

    private string FormatRawYamlValue(RawYamlRef value)
    {
        if (value.Kind == RawYamlKind.String)
        {
            return $"\"{value.Scalar.Decode()}\"";
        }

        if (value.Kind == RawYamlKind.Object)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            var sortedEntries = new List<(string Key, string Value)>();
            foreach (var pair in value.Properties)
            {
                sortedEntries.Add((pair.Key.Decode(), FormatRawYamlValue(pair.Value)));
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

        if (value.Kind == RawYamlKind.Array)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (var i = 0; i < value.Items.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(FormatRawYamlValue(value.Items[i]));
            }

            sb.Append(']');
            return sb.ToString();
        }

        return "?";
    }

    private string FormatPossibleValues(RawYamlRefList values)
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

    private string FormatPossibleValues(List<RawYamlRef> values)
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

    private TextRange GetRawYamlValueLocation(RawYamlRef value, TextRange fallback)
    {
        if (value.Kind == RawYamlKind.String)
        {
            return value.Scalar.Range;
        }

        if (value.Range.StartLine > 0)
        {
            return value.Range;
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

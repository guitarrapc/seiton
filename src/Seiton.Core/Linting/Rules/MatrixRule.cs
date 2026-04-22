using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class MatrixRule : RuleBase
{
    private const long MaxRecommendedCombinations = 256;

    public override string Id => "matrix";

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

    private void ValidateCombinations(Job job, Matrix matrix, IReadOnlyList<MatrixCombinations>? combinations, string section)
    {
        if (matrix.Rows is null || combinations is null || combinations.Count == 0)
        {
            return;
        }

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
                    if (Config.Utf8Yaml is not null && matrix.Rows.Value.ContainsKey(Config.Utf8Yaml, pair.Key))
                    {
                        continue;
                    }

                    var jobId = Decode(Arena.GetStringSlice(job.Id));
                    var axisName = Decode(pair.Key);
                    AddJobWarning(
                        job,
                        $"job '{jobId}' strategy.matrix.{section} references unknown axis '{axisName}'",
                        matrix.Range);
                    return;
                }
            }
        }
    }
}

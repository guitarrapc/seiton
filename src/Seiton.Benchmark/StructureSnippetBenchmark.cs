using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Output;
using System.Text;

namespace Seiton.Benchmark;

/// <summary>
/// Hot-path benchmark for structure snippet construction (path resolve + ancestor chain + display).
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class StructureSnippetBenchmark
{
    private Diagnostic[] _diagnostics = [];
    private Dictionary<string, byte[]> _sourceMap = new(StringComparer.Ordinal);
    private Dictionary<string, YamlLineIndex> _lineIndexCache = new(StringComparer.Ordinal);

    [GlobalSetup]
    public void Setup()
    {
        const int fileCount = 10;
        var engine = new LintEngine();
        var list = new List<Diagnostic>(capacity: 512);
        _sourceMap = new Dictionary<string, byte[]>(fileCount, StringComparer.Ordinal);
        _lineIndexCache = new Dictionary<string, YamlLineIndex>(fileCount, StringComparer.Ordinal);

        for (var i = 0; i < fileCount; i++)
        {
            var yaml = WorkflowYamlBuilder.Build(jobCount: 6, stepsPerJob: 8, nameSuffix: $"-ss{i}");
            var bytes = Encoding.UTF8.GetBytes(yaml);
            var relativePath = $".github/workflows/bench-ss-{i}.yml";
            var path = Path.GetFullPath(relativePath);
            _sourceMap[path] = bytes;
            _lineIndexCache[path] = YamlLineIndex.Create(bytes);

            using var result = engine.Check(bytes, path, new LintConfig { Utf8Yaml = bytes, FilePath = path });
            if (result.Diagnostics.Length > 0)
            {
                list.AddRange(result.Diagnostics);
            }
        }

        _diagnostics = [.. list];
    }

    [Benchmark(Baseline = true, Description = "StructureSnippetBuilder TryBuild all diagnostics")]
    public int TryBuildAll()
    {
        var built = 0;
        for (var i = 0; i < _diagnostics.Length; i++)
        {
            var diag = _diagnostics[i];
            var file = diag.FilePath ?? string.Empty;
            if (!_sourceMap.TryGetValue(file, out var bytes))
            {
                continue;
            }

            _lineIndexCache.TryGetValue(file, out var cached);
            Span<StructureSnippetEntry> scratch = stackalloc StructureSnippetEntry[StructureSnippetBuilder.MaxStackDisplayEntries];
            if (StructureSnippetBuilder.TryBuild(
                    bytes,
                    diag,
                    cached,
                    scratch,
                    out var lineIndex,
                    out var entries,
                    out _,
                    out var rentedEntries)
                && !entries.IsEmpty)
            {
                if (rentedEntries is not null)
                {
                    ArrayPool<StructureSnippetEntry>.Shared.Return(rentedEntries);
                }

                built++;
            }

            _lineIndexCache[file] = lineIndex;
        }

        return built;
    }
}

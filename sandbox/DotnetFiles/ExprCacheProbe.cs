#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core

using System.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

// ─── Build Large benchmark YAML (same as WorkflowYamlBuilder) ───
static string BuildWorkflowYaml(int jobCount, int stepsPerJob)
{
    var sb = new StringBuilder(capacity: 8_192);
    sb.AppendLine("name: bench");
    sb.AppendLine("run-name: Bench ${{ github.ref_name }}");
    sb.AppendLine("on:");
    sb.AppendLine("  push:");
    sb.AppendLine("    branches: [main, release/**]");
    sb.AppendLine("  workflow_dispatch:");
    sb.AppendLine("    inputs:");
    sb.AppendLine("      target:");
    sb.AppendLine("        type: choice");
    sb.AppendLine("        options: [dev, prod]");
    sb.AppendLine("        default: dev");
    sb.AppendLine("permissions:");
    sb.AppendLine("  contents: read");
    sb.AppendLine("env:");
    sb.AppendLine("  GLOBAL: value");
    sb.AppendLine("defaults:");
    sb.AppendLine("  run:");
    sb.AppendLine("    shell: bash");
    sb.AppendLine("concurrency:");
    sb.AppendLine("  group: bench-${{ github.ref }}");
    sb.AppendLine("  cancel-in-progress: true");
    sb.AppendLine("jobs:");
    for (var j = 0; j < jobCount; j++)
    {
        sb.Append("  job").Append(j).AppendLine(":");
        sb.AppendLine("    name: Build");
        sb.AppendLine("    runs-on: ubuntu-latest");
        sb.AppendLine("    timeout-minutes: 30");
        sb.AppendLine("    continue-on-error: false");
        sb.AppendLine("    strategy:");
        sb.AppendLine("      fail-fast: true");
        sb.AppendLine("      max-parallel: 2");
        sb.AppendLine("      matrix:");
        sb.AppendLine("        os: [ubuntu-latest, windows-latest]");
        sb.AppendLine("    steps:");
        for (var s = 0; s < stepsPerJob; s++)
        {
            if ((s & 1) == 0)
            {
                sb.AppendLine("      - name: Run");
                sb.AppendLine("        if: ${{ startsWith(github.ref, 'refs/heads/') && success() }}");
                sb.AppendLine("        run: echo ${{ matrix.os }}");
                sb.AppendLine("        env:");
                sb.AppendLine("          STEP_ENV: ${{ github.sha }}");
            }
            else
            {
                sb.AppendLine("      - name: Action");
                sb.AppendLine("        uses: actions/checkout@v4");
                sb.AppendLine("        with:");
                sb.AppendLine("          fetch-depth: '0'");
                sb.AppendLine("        if: ${{ !cancelled() && github.event_name == 'push' }}");
            }
        }
    }
    return sb.ToString().Replace("\r\n", "\n");
}

var yaml = BuildWorkflowYaml(jobCount: 20, stepsPerJob: 12);
var yamlBytes = Encoding.UTF8.GetBytes(yaml);

// Manually scan for ${{ ... }} expressions in the YAML
var testConfig = new LintConfig { Utf8Yaml = yamlBytes, FilePath = "bench.yml" };
var expressionTexts = new List<string>();
var uniqueTextSet = new HashSet<string>();
int parseCallCount = 0;

// Simple scan for ${{ ... }} patterns
var yamlSpan = yamlBytes.AsSpan();
var searchPos = 0;
while (searchPos < yamlBytes.Length - 4)
{
    var idx = yamlBytes.AsSpan(searchPos).IndexOf("${{"u8);
    if (idx < 0) break;
    var exprStart = searchPos + idx + 3;
    var endIdx = yamlBytes.AsSpan(exprStart).IndexOf("}}"u8);
    if (endIdx < 0) break;

    var exprBytes = yamlBytes.AsSpan(exprStart, endIdx);
    // Trim whitespace
    while (exprBytes.Length > 0 && (exprBytes[0] == (byte)' ' || exprBytes[0] == (byte)'\t')) exprBytes = exprBytes[1..];
    while (exprBytes.Length > 0 && (exprBytes[^1] == (byte)' ' || exprBytes[^1] == (byte)'\t')) exprBytes = exprBytes[..^1];

    if (exprBytes.Length > 0)
    {
        parseCallCount++;
        var text = Encoding.UTF8.GetString(exprBytes);
        expressionTexts.Add(text);
        uniqueTextSet.Add(text);
        testConfig.ParseExpression(exprBytes);
    }

    searchPos = exprStart + endIdx + 2;
}

// Use reflection to read the _expressionCache
var cacheField = typeof(LintConfig).GetField("_expressionCache", BindingFlags.NonPublic | BindingFlags.Instance);
var cacheObj = cacheField!.GetValue(testConfig);
var cacheCount = 0;
if (cacheObj is System.Collections.IDictionary dict)
{
    cacheCount = dict.Count;
}

Console.WriteLine($"Expression cache entries (content-based): {cacheCount}");
Console.WriteLine($"Total ParseExpression calls: {parseCallCount}");

// Count unique content
Console.WriteLine($"Unique expression texts: {uniqueTextSet.Count}");
Console.WriteLine($"Cache entries should equal unique texts: {cacheCount == uniqueTextSet.Count}");
Console.WriteLine();
Console.WriteLine("Expression text → count:");
var textCounts = new Dictionary<string, int>();
foreach (var t in expressionTexts)
{
    if (!textCounts.TryGetValue(t, out var c)) c = 0;
    textCounts[t] = c + 1;
}
foreach (var kv in textCounts.OrderByDescending(x => x.Value))
{
    Console.WriteLine($"  \"{kv.Key}\" × {kv.Value}");
}

Console.WriteLine();
Console.WriteLine($"Saved Parse() calls: {parseCallCount - cacheCount}");

// Measure expression-related allocations precisely.
// Run: dotnet run sandbox/DotnetFiles/ExpressionAllocProbe.cs

#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core/Seiton.Core.csproj
using System;
using System.Runtime.CompilerServices;
using System.Text;
using Seiton.Core.Parsing;

// Print struct sizes
Console.WriteLine($"ExpressionNode size: {Unsafe.SizeOf<ExpressionNode>()}B");
Console.WriteLine($"Utf8Slice size: {Unsafe.SizeOf<Utf8Slice>()}B");
Console.WriteLine($"TextRange size: {Unsafe.SizeOf<TextRange>()}B");
Console.WriteLine($"Diagnostic size: {Unsafe.SizeOf<Diagnostic>()}B");
Console.WriteLine($"ExpressionParseResult size: {Unsafe.SizeOf<ExpressionParseResult>()}B");
Console.WriteLine();

// Test expressions from benchmark
var expressions = new (string Name, string Expr)[]
{
    ("run-name ref", "github.ref_name"),
    ("concurrency ref", "github.ref"),
    ("if startsWith", "startsWith(github.ref, 'refs/heads/') && success()"),
    ("matrix.os", "matrix.os"),
    ("github.sha", "github.sha"),
    ("if cancelled", "!cancelled() && github.event_name == 'push'"),
};

Console.WriteLine("=== Per-expression parse allocations ===");
long totalNodeBytes = 0;
long totalArgBytes = 0;
long totalParseOverhead = 0;

foreach (var (name, expr) in expressions)
{
    var utf8 = Encoding.UTF8.GetBytes(expr);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetTotalAllocatedBytes(precise: true);
    var result = ExpressionParser.Parse(utf8);
    var after = GC.GetTotalAllocatedBytes(precise: true);
    var delta = after - before;

    var nodeArrayBytes = result.Nodes.Length > 0 ? 24 + result.Nodes.Length * Unsafe.SizeOf<ExpressionNode>() : 0;
    var argArrayBytes = result.Arguments.Length > 0 ? 24 + result.Arguments.Length * 4 : 0;
    var diagArrayBytes = result.Diagnostics.Length > 0 ? 24 + result.Diagnostics.Length * Unsafe.SizeOf<Diagnostic>() : 0;
    var overhead = delta - nodeArrayBytes - argArrayBytes - diagArrayBytes;

    Console.WriteLine($"  {name}: {delta}B total, {result.Nodes.Length} nodes ({nodeArrayBytes}B), {result.Arguments.Length} args ({argArrayBytes}B), {result.Diagnostics.Length} diags ({diagArrayBytes}B), overhead {overhead}B");

    totalNodeBytes += nodeArrayBytes;
    totalArgBytes += argArrayBytes;
    totalParseOverhead += overhead;
}
Console.WriteLine($"  Total node arrays: {totalNodeBytes}B, arg arrays: {totalArgBytes}B, overhead: {totalParseOverhead}B");
Console.WriteLine();

// Measure Validate allocations per expression
Console.WriteLine("=== Per-expression validate allocations ===");
long totalValidateBytes = 0;

foreach (var (name, expr) in expressions)
{
    var utf8 = Encoding.UTF8.GetBytes(expr);
    var parseResult = ExpressionParser.Parse(utf8);
    var loc = new TextRange(0, utf8.Length, 1, 1, 1, utf8.Length);

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetTotalAllocatedBytes(precise: true);
    var diags = ExpressionSemanticAnalyzer.Validate(
        parseResult, utf8, loc, ExpressionValidationContext.Step);
    var after = GC.GetTotalAllocatedBytes(precise: true);
    var delta = after - before;

    totalValidateBytes += delta;
    Console.WriteLine($"  {name}: {delta}B, {diags.Length} diags");
}
Console.WriteLine($"  Total validate: {totalValidateBytes}B");
Console.WriteLine();

// Scale to Large benchmark: 20 jobs × 12 steps (6 run, 6 action)
// Per run step: if(startsWith), run(matrix.os), env(github.sha) = 3 expressions
// Per action step: if(cancelled) = 1 expression
// Per job: 6*3 + 6*1 = 24 expressions
// Total: 20*24 + 2 (workflow-level) = 482 expressions
Console.WriteLine("=== Scaled estimate for Large (482 expressions) ===");

// Measure actual allocation for 482 representative calls
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

var exprs = new byte[][]
{
    Encoding.UTF8.GetBytes("startsWith(github.ref, 'refs/heads/') && success()"),
    Encoding.UTF8.GetBytes("matrix.os"),
    Encoding.UTF8.GetBytes("github.sha"),
    Encoding.UTF8.GetBytes("!cancelled() && github.event_name == 'push'"),
    Encoding.UTF8.GetBytes("github.ref_name"),
    Encoding.UTF8.GetBytes("github.ref"),
};
var loc2 = new TextRange(0, 100, 1, 1, 1, 100);

var parseAllocBefore = GC.GetTotalAllocatedBytes(precise: true);
for (int j = 0; j < 20; j++)
{
    for (int s = 0; s < 6; s++)
    {
        // Run step: 3 expressions
        _ = ExpressionParser.Parse(exprs[0]);
        _ = ExpressionParser.Parse(exprs[1]);
        _ = ExpressionParser.Parse(exprs[2]);
        // Action step: 1 expression
        _ = ExpressionParser.Parse(exprs[3]);
    }
}
_ = ExpressionParser.Parse(exprs[4]); // run-name
_ = ExpressionParser.Parse(exprs[5]); // concurrency
var parseAllocAfter = GC.GetTotalAllocatedBytes(precise: true);
Console.WriteLine($"Parse only (482 calls): {parseAllocAfter - parseAllocBefore:N0}B");

var validateAllocBefore = GC.GetTotalAllocatedBytes(precise: true);
for (int j = 0; j < 20; j++)
{
    for (int s = 0; s < 6; s++)
    {
        // Run step: 3 expressions (parse + validate each)
        for (int e = 0; e < 3; e++)
        {
            var idx = e; // 0=startsWith, 1=matrix.os, 2=github.sha
            var pr = ExpressionParser.Parse(exprs[idx]);
            _ = ExpressionSemanticAnalyzer.Validate(pr, exprs[idx], loc2, ExpressionValidationContext.Step);
        }
        // Action step: 1 expression
        {
            var pr = ExpressionParser.Parse(exprs[3]);
            _ = ExpressionSemanticAnalyzer.Validate(pr, exprs[3], loc2, ExpressionValidationContext.Step);
        }
    }
}
{
    var pr1 = ExpressionParser.Parse(exprs[4]);
    _ = ExpressionSemanticAnalyzer.Validate(pr1, exprs[4], loc2, ExpressionValidationContext.Workflow);
    var pr2 = ExpressionParser.Parse(exprs[5]);
    _ = ExpressionSemanticAnalyzer.Validate(pr2, exprs[5], loc2, ExpressionValidationContext.Workflow);
}
var validateAllocAfter = GC.GetTotalAllocatedBytes(precise: true);
Console.WriteLine($"Parse + Validate (482 calls): {validateAllocAfter - validateAllocBefore:N0}B");
Console.WriteLine($"Validate overhead: {(validateAllocAfter - validateAllocBefore) - (parseAllocAfter - parseAllocBefore):N0}B");

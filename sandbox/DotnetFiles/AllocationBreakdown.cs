#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core
using System.Runtime;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

// Build workflow YAML matching benchmark
var yaml = BuildWorkflowYaml(20, 12);
var bytes = System.Text.Encoding.UTF8.GetBytes(yaml);

// Warm up ThreadStatic
var warmup = WorkflowParser.Parse(bytes, "bench.yml");
warmup.Arena?.Dispose();

// ---- 1. Measure full parse with arena reuse ----
GC.Collect(2, GCCollectionMode.Forced, true, true);
var before = GC.GetTotalAllocatedBytes(precise: true);
var result = WorkflowParser.Parse(bytes, "bench.yml");
var totalAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;

Console.WriteLine($"Total parse allocations (arena reused): {totalAlloc:N0} bytes");
Console.WriteLine();

// ---- 2. Count EVERY class-type AST node to estimate class overhead ----
var wf = result.Workflow!;
var arena = result.Arena!;
int classInstances = 0;
long classBytes = 0;

void CountClass(string name, int count, int fieldsBytes)
{
    int size = 16 + fieldsBytes; // 16 = obj header + MT ptr on x64
    size = (size + 7) & ~7; // align to 8
    classInstances += count;
    classBytes += (long)count * size;
    if (count > 0) Console.WriteLine($"  {name}: {count} × {size}B = {(long)count * size:N0}B");
}

Console.WriteLine("=== Class instance breakdown ===");

// Workflow: 1
CountClass("Workflow", 1, 8*2 + 8 + 8*4 + 16 + 32); // 2 handles + On ref + 4 nullable refs + SliceMap<Job> + Range
// Events
int webhookCount = 0, schedCount = 0, dispatchCount = 0, callCount = 0, repoDispCount = 0, imgVerCount = 0;
int schedEntryCount = 0, dispInputCount = 0, callInputCount = 0, callSecretCount = 0, callOutputCount = 0;
int filterCount = 0;
if (wf.On is not null)
{
    foreach (var ev in wf.On)
    {
        if (ev is WebhookEvent wev)
        {
            webhookCount++;
            if (wev.Branches is not null) filterCount++;
            if (wev.BranchesIgnore is not null) filterCount++;
            if (wev.Tags is not null) filterCount++;
            if (wev.TagsIgnore is not null) filterCount++;
            if (wev.Paths is not null) filterCount++;
            if (wev.PathsIgnore is not null) filterCount++;
        }
        else if (ev is ScheduledEvent se) { schedCount++; schedEntryCount += se.Schedules.Count; }
        else if (ev is WorkflowDispatchEvent wd) { dispatchCount++; if (wd.Inputs is not null) dispInputCount += wd.Inputs.Value.Count; }
        else if (ev is WorkflowCallEvent wce) { callCount++; }
        else if (ev is RepositoryDispatchEvent) repoDispCount++;
        else if (ev is ImageVersionEvent) imgVerCount++;
    }
}
CountClass("WebhookEvent", webhookCount, 8*2 + 8*8 + 32); // base + class specifics
CountClass("ScheduledEvent", schedCount, 8*2 + 8 + 32);
CountClass("WorkflowDispatchEvent", dispatchCount, 8*2 + 16 + 32);
CountClass("DispatchInput", dispInputCount, 8*7 + 32);
CountClass("WebhookEventFilter", filterCount, 8 + 8 + 32);

// Jobs & composites
int jobCount = wf.Jobs.Count;
int runnerCount = 0, strategyCount = 0, matrixCount = 0, envCount = 0, defaultsCount = 0;
int defaultsRunCount = 0, concurrencyCount = 0, containerCount = 0, envVarUsedCount = 0;
int environmentCount = 0, permCount = 0, servicesCount = 0;
int matrixRowCount = 0, matrixComboCount = 0, rawYamlStringCount = 0;

// Workflow-level composites
if (wf.Permissions is not null) permCount++;
if (wf.Env is not null) { envCount++; if (wf.Env.Vars is not null) envVarUsedCount += wf.Env.Vars.Value.Count; }
if (wf.Defaults is not null) { defaultsCount++; defaultsRunCount++; }
if (wf.Concurrency is not null) concurrencyCount++;

foreach (var kv in wf.Jobs)
{
    var job = kv.Value;
    if (job.RunsOn is not null) runnerCount++;
    if (job.Permissions is not null) permCount++;
    if (job.Env is not null) { envCount++; if (job.Env.Vars is not null) envVarUsedCount += job.Env.Vars.Value.Count; }
    if (job.Defaults is not null) { defaultsCount++; defaultsRunCount++; }
    if (job.Concurrency is not null) concurrencyCount++;
    if (job.Environment is not null) environmentCount++;
    if (job.Container is not null) containerCount++;
    if (job.Services is not null) servicesCount++;
    if (job.Strategy is not null)
    {
        strategyCount++;
        if (job.Strategy.Matrix is not null)
        {
            matrixCount++;
            var m = job.Strategy.Matrix;
            if (m.Rows is not null)
            {
                matrixRowCount += m.Rows.Value.Count;
                foreach (var r in m.Rows.Value)
                {
                    if (r.Value.Values is not null)
                        foreach (var v in r.Value.Values)
                            if (v is RawYamlString) rawYamlStringCount++;
                }
            }
            if (m.Include is not null) matrixComboCount += m.Include.Count;
            if (m.Exclude is not null) matrixComboCount += m.Exclude.Count;
        }
    }
}

CountClass("Job", jobCount, 4*6 + 8*10 + 1 + 32); // 6 handles + 10 refs + bool + Range
CountClass("Runner", runnerCount, 8 + 4*2 + 32);
CountClass("Strategy", strategyCount, 8 + 4*2 + 32);
CountClass("Matrix", matrixCount, 4 + 8*3 + 16 + 32);
CountClass("MatrixRow", matrixRowCount, 4*2 + 8);
CountClass("MatrixCombinations", matrixComboCount, 4 + 8);
CountClass("RawYamlString", rawYamlStringCount, 4);
CountClass("Env", envCount, 4 + 16 + 32);
CountClass("Defaults", defaultsCount, 8 + 32);
CountClass("DefaultsRun", defaultsRunCount, 4*2 + 32);
CountClass("Concurrency", concurrencyCount, 4*2 + 32);
CountClass("Permissions", permCount, 4 + 16 + 32);
CountClass("Environment", environmentCount, 4*2 + 4 + 32);
CountClass("Container", containerCount, 4*2 + 8*4 + 32);

// Steps
int stepCount = 0, execRunCount = 0, execActionCount = 0, stepEnvCount = 0, stepEnvVarCount = 0;
foreach (var kv in wf.Jobs)
{
    if (kv.Value.Steps is null) continue;
    stepCount += kv.Value.Steps.Count;
    for (var i = 0; i < kv.Value.Steps.Count; i++)
    {
        var step = kv.Value.Steps[i];
        if (step.Exec is ExecRun) execRunCount++;
        else if (step.Exec is ExecAction) execActionCount++;
        if (step.Env is not null) { stepEnvCount++; if (step.Env.Vars is not null) stepEnvVarCount += step.Env.Vars.Value.Count; }
    }
}
CountClass("Step", stepCount, 4*5 + 8 + 32); // 5 handles + Exec ref + Range
CountClass("ExecRun", execRunCount, 4*3 + 4 + 32); // 3 handles + Kind enum + Range
CountClass("ExecAction", execActionCount, 4*3 + 8 + 16 + 4 + 32); // handles + Inputs + Range
CountClass("Env (step)", stepEnvCount, 4 + 16 + 32);

Console.WriteLine($"\n  --- Total class instances: {classInstances} ---");
Console.WriteLine($"  --- Total class bytes (estimated): {classBytes:N0} ---");

// ---- 3. Array allocations ----
Console.WriteLine("\n=== Array allocations ===");
int totalArrayBytes = 0;

// On events array
var onCount = wf.On?.Count ?? 0;
var onArrayBytes = 16 + onCount * 8; // array header + refs
Console.WriteLine($"  On events array: {onCount} × 8B + 16B header = {onArrayBytes}B");
totalArrayBytes += onArrayBytes;

// Steps arrays (one per job)
foreach (var kv in wf.Jobs)
{
    if (kv.Value.Steps is not null)
    {
        var stepsArrayBytes = 16 + kv.Value.Steps.Count * 8;
        totalArrayBytes += stepsArrayBytes;
    }
}
Console.WriteLine($"  Steps arrays: {jobCount} arrays × avg {stepCount / jobCount} items = ~{totalArrayBytes - onArrayBytes:N0}B");

// SliceMap Entry arrays
int sliceMapEntryBytes = 0;
// Jobs SliceMap
sliceMapEntryBytes += 16 + wf.Jobs.Count * 24; // Entry = Utf8Slice(8) + TValue(8..n)
Console.WriteLine($"  Jobs SliceMap entries: {wf.Jobs.Count} × ~24B = {16 + wf.Jobs.Count * 24}B");

// Outputs, with, Env.Vars, etc (many small arrays)
int smallSliceCount = envVarUsedCount + stepEnvVarCount;
int smallSliceBytes = smallSliceCount * 24 + 16 * (envCount + stepEnvCount);
Console.WriteLine($"  EnvVar SliceMap entries: ~{smallSliceCount} entries in ~{envCount + stepEnvCount} maps = ~{smallSliceBytes:N0}B");
totalArrayBytes += sliceMapEntryBytes + smallSliceBytes;

// Action with: inputs SliceMap
int withCount = 0;
int withEntries = 0;
foreach (var kv in wf.Jobs)
{
    if (kv.Value.Steps is null) continue;
    for (var i = 0; i < kv.Value.Steps.Count; i++)
    {
        if (kv.Value.Steps[i].Exec is ExecAction ea && ea.Inputs is not null)
        {
            withCount++;
            withEntries += ea.Inputs.Value.Count;
        }
    }
}
Console.WriteLine($"  Action with: inputs SliceMap: {withCount} maps, {withEntries} entries");

// StringNodeId[] arrays (needs, labels, types, etc)
int handleArrayCount = 0;
int handleArrayTotalItems = 0;
foreach (var kv in wf.Jobs)
{
    if (kv.Value.Needs is not null) { handleArrayCount++; handleArrayTotalItems += kv.Value.Needs.Length; }
    if (kv.Value.RunsOn?.Labels is not null) { handleArrayCount++; handleArrayTotalItems += kv.Value.RunsOn.Labels.Length; }
}
if (wf.On is not null)
{
    foreach (var ev in wf.On)
    {
        if (ev is WebhookEvent we)
        {
            if (we.Types is not null) { handleArrayCount++; handleArrayTotalItems += we.Types.Length; }
            if (we.Branches is not null) { handleArrayCount++; handleArrayTotalItems += we.Branches.Values.Length; }
        }
    }
}
Console.WriteLine($"  Handle arrays (Needs/Labels/Types/etc): {handleArrayCount} arrays, {handleArrayTotalItems} items = ~{handleArrayCount * 16 + handleArrayTotalItems * 4:N0}B");

// Matrix row values
foreach (var kv in wf.Jobs)
{
    if (kv.Value.Strategy?.Matrix?.Rows is not null)
    {
        foreach (var mr in kv.Value.Strategy.Matrix.Rows.Value)
        {
            if (mr.Value.Values is not null)
                handleArrayTotalItems += mr.Value.Values.Count;
        }
    }
}

Console.WriteLine($"\n  --- Total estimated array bytes: ~{totalArrayBytes:N0}B ---");

// ---- 4. Diagnostics ----
var diagBytes = result.Diagnostics.Length > 0 ? 16 + result.Diagnostics.Length * 64 : 16; // empty array
Console.WriteLine($"\n=== Diagnostics: {result.Diagnostics.Length} items, ~{diagBytes}B ===");

// ---- 5. Expression-related ----
Console.WriteLine($"\n=== Remaining = Total - Classes - Arrays - Diagnostics ===");
Console.WriteLine($"  Total: {totalAlloc:N0}B");
Console.WriteLine($"  Classes: ~{classBytes:N0}B");
Console.WriteLine($"  Arrays: ~{totalArrayBytes:N0}B");
Console.WriteLine($"  Unaccounted: ~{totalAlloc - classBytes - totalArrayBytes - diagBytes:N0}B");
Console.WriteLine($"  (includes: expression parsing, PooledBuffer intermediate arrays,");
Console.WriteLine($"   VYaml internal buffers, string materializations, Diagnostic objects)");

result.Arena?.Dispose();

static string BuildWorkflowYaml(int jobCount, int stepsPerJob)
{
    var sb = new System.Text.StringBuilder(capacity: 8_192);
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

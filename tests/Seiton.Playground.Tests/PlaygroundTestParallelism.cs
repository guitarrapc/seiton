namespace Seiton.Playground.Tests;

/// <summary>
/// Serializes all tests in this assembly. Playground UI tests publish WASM (peak RAM during AOT),
/// launch Chromium, and share static <see cref="Playground.PlaygroundLintRunner"/> state — running
/// in parallel with other tests in this assembly has caused multi‑tens‑of‑GB RAM use on dev machines.
/// </summary>
internal static class PlaygroundTestParallelism
{
    internal const string AssemblyLockKey = "seiton-playground-tests";
}

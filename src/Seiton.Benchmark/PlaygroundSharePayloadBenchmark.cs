using Seiton.Playground;

namespace Seiton.Benchmark;

/// <summary>
/// Encode/decode cost for playground Share URL hash payloads (v2 JSON + zlib).
/// </summary>
[MemoryDiagnoser]
public class PlaygroundSharePayloadBenchmark
{
    private PlaygroundSharePayload.State _small = null!;
    private PlaygroundSharePayload.State _large = null!;

    [GlobalSetup]
    public void Setup()
    {
        _small = new PlaygroundSharePayload.State(
            """
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
            """,
            """
            rules:
              runner-no-latest:
                severity: warning
            """,
            ".github/workflows/ci.yml");

        var yaml = File.ReadAllText(Path.Combine(
            RepoPaths.FindRepoRoot(),
            "tests",
            "Seiton.Core.Tests",
            "fixtures",
            "schema",
            "actionlint",
            "testdata",
            "bench",
            "large.yaml"));
        _large = new PlaygroundSharePayload.State(
            yaml,
            """
            fix:
              defaults:
                job-timeout-minutes: 15
            rules:
              runner-no-latest:
                fix-mapping:
                  ubuntu-latest: "ubuntu-24.04"
            """,
            ".github/workflows/large.yml");
    }

    [Benchmark]
    public string Encode_Small() => PlaygroundSharePayload.Encode(_small);

    [Benchmark]
    public string Encode_Large() => PlaygroundSharePayload.Encode(_large);

    [Benchmark]
    public bool Decode_Small()
    {
        var hash = PlaygroundSharePayload.Encode(_small);
        return PlaygroundSharePayload.TryDecode(hash, out _, out _);
    }

    [Benchmark]
    public bool Decode_Large()
    {
        var hash = PlaygroundSharePayload.Encode(_large);
        return PlaygroundSharePayload.TryDecode(hash, out _, out _);
    }
}

/// <summary>Minimal repo root lookup for benchmark fixtures (same idea as tests).</summary>
file static class RepoPaths
{
    public static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            if (File.Exists(Path.Combine(dir, "seiton.slnx")) || Directory.Exists(Path.Combine(dir, ".git")))
            {
                return dir;
            }

            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                break;
            }

            dir = parent.FullName;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}

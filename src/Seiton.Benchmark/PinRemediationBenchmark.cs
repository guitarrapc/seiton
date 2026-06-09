using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;

namespace Seiton.Benchmark;

/// <summary>Benchmarks pin-remediation hot paths (OCI skip resolution and engine orchestration).</summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class PinRemediationBenchmark
{
    private OciImageDigestResolver _resolver = null!;
    private PinRemediationEngine _engine = null!;
    private Diagnostic[] _skipDiagnostics = null!;
    private byte[] _yamlBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        var handler = new NoNetworkHandler();
        _resolver = new OciImageDigestResolver(
            new HttpClient(handler, disposeHandler: true),
            new FixImagesConfig(),
            dockerConfigPath: Path.Combine(Path.GetTempPath(), "__nonexistent_seiton_bench_docker_config__.json"));

        _engine = new PinRemediationEngine(
            null,
            _resolver,
            new FixPinningConfig(),
            new FixImagesConfig { EnableNetwork = true },
            new NetworkConfig());

        const string yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-24.04
                services:
                  redis:
                    image: redis
                  db:
                    image: postgres
                container:
                  image: node
                steps:
                  - uses: docker://ghcr.io/astral-sh/uv:latest
            """;

        _yamlBytes = Encoding.UTF8.GetBytes(yaml);
        _skipDiagnostics =
        [
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "job.services image 'redis' is not pinned by digest",
                new TextRange(0, _yamlBytes.Length, 1, 1, 6, 20),
                RuleId: "unpinned-image",
                Metadata: PinDiagnosticMetadata.ForImageRef("redis")),
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "job.services image 'postgres' is not pinned by digest",
                new TextRange(0, _yamlBytes.Length, 1, 1, 8, 24),
                RuleId: "unpinned-image",
                Metadata: PinDiagnosticMetadata.ForImageRef("postgres")),
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "job.container image 'node' is not pinned by digest",
                new TextRange(0, _yamlBytes.Length, 1, 1, 10, 22),
                RuleId: "unpinned-image",
                Metadata: PinDiagnosticMetadata.ForImageRef("node")),
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "'docker://ghcr.io/astral-sh/uv:latest' is not pinned by digest",
                new TextRange(0, _yamlBytes.Length, 1, 1, 12, 50),
                RuleId: "unpinned-image",
                Metadata: PinDiagnosticMetadata.ForImageRef("docker://ghcr.io/astral-sh/uv:latest")),
        ];
    }

    [Benchmark(Baseline = true, Description = "OciImageDigestResolver skip (implicit latest)")]
    public int ResolveImplicitLatestSkip()
    {
        var count = 0;
        for (var i = 0; i < 32; i++)
        {
            var resolution = _resolver.ResolveAsync("redis").GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(resolution.SkipReason))
            {
                count++;
            }
        }

        return count;
    }

    [Benchmark(Description = "PinRemediationEngine.RemediateAsync (4 skip diagnostics)")]
    public async Task<int> RemediateSkippedImages()
    {
        var result = await _engine.RemediateAsync(_skipDiagnostics, _yamlBytes);
        return result.SkippedCount;
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}

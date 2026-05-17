using System.Globalization;
using Seiton.Cli;
using Seiton.Commands;
using Seiton.Core.Parsing;

namespace Seiton.Tests;

public sealed class VerboseTimingTests
{
    // === TimeProvider integration ===

    [Test]
    public async Task VerboseLogger_GetTimestamp_DelegatesToTimeProvider()
    {
        using var sw = new StringWriter();
        var tp = new FixedTimeProvider();
        var logger = VerboseLogger.Create(verbose: true, sw, tp);

        var t1 = logger.GetTimestamp();
        tp.Advance(100);
        var t2 = logger.GetTimestamp();

        await Assert.That(t2).IsGreaterThan(t1);
    }

    [Test]
    public async Task VerboseLogger_GetElapsedTime_ReturnsCorrectDuration()
    {
        using var sw = new StringWriter();
        var tp = new FixedTimeProvider();
        var logger = VerboseLogger.Create(verbose: true, sw, tp);

        var start = logger.GetTimestamp();
        tp.Advance(5); // 5ms
        var elapsed = logger.GetElapsedTime(start);

        await Assert.That(elapsed.TotalMilliseconds).IsEqualTo(5.0);
    }

    [Test]
    public async Task VerboseLogger_Null_GetTimestamp_ReturnsZero()
    {
        // When verbose is disabled, GetTimestamp should return 0 (no-op)
        var logger = VerboseLogger.Null;

        var t = logger.GetTimestamp();

        await Assert.That(t).IsEqualTo(0L);
    }

    // === Per-file timing summary ===

    [Test]
    public async Task WriteFileTimingSummary_EmitsKindTimingDiagsSuppressed()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        CheckCommand.WriteFileTimingSummary(logger, ".github/workflows/ci.yml",
            DocumentKind.Workflow, TimeSpan.FromMilliseconds(1.2), diagnosticCount: 5, suppressedCount: 2);

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: .github/workflows/ci.yml: workflow, 1.2 ms, 5 diagnostics, 2 suppressed");
    }

    [Test]
    public async Task WriteFileTimingSummary_ActionMetadata_EmitsAction()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        CheckCommand.WriteFileTimingSummary(logger, "action.yml",
            DocumentKind.ActionMetadata, TimeSpan.FromMilliseconds(0.8), diagnosticCount: 3, suppressedCount: 0);

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: action.yml: action, 0.8 ms, 3 diagnostics, 0 suppressed");
    }

    [Test]
    public async Task WriteFileTimingSummary_VerboseDisabled_EmitsNothing()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: false, sw);

        CheckCommand.WriteFileTimingSummary(logger, "ci.yml",
            DocumentKind.Workflow, TimeSpan.FromMilliseconds(10), diagnosticCount: 1, suppressedCount: 0);

        await Assert.That(sw.ToString()).IsEqualTo("");
    }

    [Test]
    public async Task WriteFileTimingSummary_LargeElapsed_FormatsCorrectly()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        CheckCommand.WriteFileTimingSummary(logger, "big.yml",
            DocumentKind.Workflow, TimeSpan.FromMilliseconds(1234.5), diagnosticCount: 100, suppressedCount: 10);

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: big.yml: workflow, 1234.5 ms, 100 diagnostics, 10 suppressed");
    }

    // === Total timing summary ===

    [Test]
    public async Task WriteTotalTiming_Check_EmitsCheckedVerb()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        CheckCommand.WriteTotalTiming(logger, fileCount: 3, TimeSpan.FromMilliseconds(15.7));

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: total: 3 file(s) checked in 15.7 ms");
    }

    [Test]
    public async Task WriteTotalTiming_SingleFile_EmitsChecked()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        CheckCommand.WriteTotalTiming(logger, fileCount: 1, TimeSpan.FromMilliseconds(2.4));

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: total: 1 file(s) checked in 2.4 ms");
    }

    [Test]
    public async Task WriteTotalTiming_Fix_EmitsFixedVerb()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        CheckCommand.WriteTotalTiming(logger, fileCount: 1, TimeSpan.FromMilliseconds(450.0), "fixed");

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: total: 1 file(s) fixed in 450.0 ms");
    }

    [Test]
    public async Task WriteTotalTiming_VerboseDisabled_EmitsNothing()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: false, sw);

        CheckCommand.WriteTotalTiming(logger, fileCount: 5, TimeSpan.FromMilliseconds(100));

        await Assert.That(sw.ToString()).IsEqualTo("");
    }

    [Test]
    public async Task TimingFormatting_UsesInvariantCulture()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;

            CheckCommand.WriteFileTimingSummary(logger, "ci.yml",
                DocumentKind.Workflow, TimeSpan.FromMilliseconds(1.2), diagnosticCount: 5, suppressedCount: 2);
            CheckCommand.WriteTotalTiming(logger, fileCount: 3, TimeSpan.FromMilliseconds(15.7));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }

        var lines = sw.ToString().TrimEnd().Split(Environment.NewLine);
        await Assert.That(lines[0]).IsEqualTo("verbose: ci.yml: workflow, 1.2 ms, 5 diagnostics, 2 suppressed");
        await Assert.That(lines[1]).IsEqualTo("verbose: total: 3 file(s) checked in 15.7 ms");
    }

    // === Network timing ===

    [Test]
    public async Task NetworkTiming_EmitsResolvedPinsWithElapsed()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        // Simulates what FixCommand will emit
        logger.Log("network", $"resolved 3 pin(s) for .github/workflows/ci.yml in {CheckCommand.FormatMilliseconds(TimeSpan.FromMilliseconds(320.0))} ms");

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: network: resolved 3 pin(s) for .github/workflows/ci.yml in 320.0 ms");
    }

    /// <summary>
    /// A test-only TimeProvider that returns fixed, controllable timestamps.
    /// TimestampFrequency = 1000 means 1 tick = 1 millisecond.
    /// </summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1000; // 1 tick = 1ms
        public override long GetTimestamp() => _timestamp;

        /// <summary>Advances the clock by the specified number of ticks (milliseconds).</summary>
        public void Advance(long ticks) => _timestamp += ticks;
    }
}

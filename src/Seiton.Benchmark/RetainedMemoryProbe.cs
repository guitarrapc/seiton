namespace Seiton.Benchmark;

/// <summary>
/// Samples live managed heap size for peak-retained memory measurement in benchmarks.
/// Use this to distinguish cumulative allocation (BenchmarkDotNet <c>Allocated</c>)
/// from concurrent working-set during multi-file lint.
/// </summary>
internal static class RetainedMemoryProbe
{
    /// <summary>
    /// Forces a compacting full GC so subsequent heap samples reflect live objects only.
    /// </summary>
    public static void CompactHeap()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>
    /// Returns the approximate number of live bytes on the managed heap.
    /// Uses a non-compacting sample during work; call with <paramref name="forceFullCollection"/>
    /// only at measurement boundaries.
    /// </summary>
    public static long GetLiveHeapBytes(bool forceFullCollection = false) =>
        GC.GetTotalMemory(forceFullCollection);

    /// <summary>
    /// Runs <paramref name="work"/> and tracks the maximum live heap observed via <see cref="PeakSampler"/>.
    /// </summary>
    public static RetainedMemorySample MeasurePeak(Action<PeakSampler> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        CompactHeap();
        var baseline = GetLiveHeapBytes(forceFullCollection: true);
        var sampler = new PeakSampler(baseline);
        work(sampler);
        sampler.Record();
        CompactHeap();
        var postWork = GetLiveHeapBytes(forceFullCollection: true);

        return new RetainedMemorySample(baseline, sampler.PeakHeapBytes, postWork);
    }

    /// <summary>Peak live managed heap observed during a measurement window.</summary>
    internal readonly struct RetainedMemorySample(long baselineHeapBytes, long peakHeapBytes, long postWorkHeapBytes)
    {
        public long BaselineHeapBytes { get; } = baselineHeapBytes;

        public long PeakHeapBytes { get; } = peakHeapBytes;

        public long PostWorkHeapBytes { get; } = postWorkHeapBytes;

        public long PeakDeltaBytes => PeakHeapBytes - BaselineHeapBytes;

        public long RetainedDeltaBytes => PostWorkHeapBytes - BaselineHeapBytes;
    }

    /// <summary>
    /// Thread-safe peak tracker passed into workloads so they can sample after each file.
    /// </summary>
    internal sealed class PeakSampler
    {
        private long _peak;

        internal PeakSampler(long baseline)
        {
            BaselineHeapBytes = baseline;
            _peak = baseline;
        }

        public long BaselineHeapBytes { get; }

        public long PeakHeapBytes => _peak;

        public long PeakDeltaBytes => PeakHeapBytes - BaselineHeapBytes;

        public void Record()
        {
            var current = GetLiveHeapBytes();
            while (true)
            {
                var observedPeak = _peak;
                if (current <= observedPeak)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _peak, current, observedPeak) == observedPeak)
                {
                    return;
                }
            }
        }
    }
}

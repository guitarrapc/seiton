using Seiton.Core.Generated;

namespace Seiton.Benchmark;

[MemoryDiagnoser]
[RankColumn]
public class IanaTimeZoneLookupBenchmark
{
    private byte[][] _validTimezones = null!;
    private byte[][] _invalidTimezones = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Common valid timezone identifiers (happy path - no allocation expected after optimization)
        _validTimezones =
        [
            "America/New_York"u8.ToArray(),
            "Europe/London"u8.ToArray(),
            "Asia/Tokyo"u8.ToArray(),
            "US/Pacific"u8.ToArray(),
            "UTC"u8.ToArray(),
            "Australia/Sydney"u8.ToArray(),
            "America/Los_Angeles"u8.ToArray(),
            "Europe/Berlin"u8.ToArray(),
        ];

        // Invalid timezone identifiers (error path - allocation acceptable)
        _invalidTimezones =
        [
            "America/NewYork"u8.ToArray(),
            "Invalid/Zone"u8.ToArray(),
            "Foo/Bar"u8.ToArray(),
            "NotATimezone"u8.ToArray(),
        ];
    }

    [Benchmark]
    public int LookupValidAll()
    {
        var count = 0;
        for (var i = 0; i < _validTimezones.Length; i++)
        {
            if (IanaTimeZones.IsKnown(_validTimezones[i].AsSpan()))
                count++;
        }

        return count;
    }

    [Benchmark]
    public int LookupInvalidAll()
    {
        var count = 0;
        for (var i = 0; i < _invalidTimezones.Length; i++)
        {
            if (IanaTimeZones.IsKnown(_invalidTimezones[i].AsSpan()))
                count++;
        }

        return count;
    }

    [Benchmark]
    public bool LookupSingleValid()
    {
        return IanaTimeZones.IsKnown("America/New_York"u8);
    }

    [Benchmark]
    public bool LookupSingleInvalid()
    {
        return IanaTimeZones.IsKnown("Invalid/Zone"u8);
    }
}

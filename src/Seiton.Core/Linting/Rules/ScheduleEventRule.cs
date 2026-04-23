using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates <c>schedule:</c> cron expressions for syntax and common mistakes.</summary>
public sealed class ScheduleEventRule() : RuleBase(RuleId.ScheduleEvent)
{
    private const int MinIntervalMinutes = 5;

    private const uint AllDomMask = uint.MaxValue ^ 1u;
    private const byte AllDowNormMask = 0x7F;

    public override string Name => "Schedule Event Rule";

    public override void VisitEvent(Event ev)
    {
        if (ev is not ScheduledEvent scheduleEvent || Config.Utf8Yaml is null)
        {
            return;
        }

        for (var i = 0; i < scheduleEvent.Schedules.Count; i++)
        {
            ValidateScheduleEntry(scheduleEvent, scheduleEvent.Schedules[i]);
        }
    }

    private void ValidateScheduleEntry(ScheduledEvent scheduleEvent, ScheduleEntry entry)
    {
        if (entry.Cron.HasValue && !IsExpressionOrInterpolation(entry.Cron))
        {
            ValidateCron(scheduleEvent, entry.Cron);
        }

        if (entry.Timezone.HasValue && !IsExpressionOrInterpolation(entry.Timezone))
        {
            ValidateTimezone(scheduleEvent, entry.Timezone);
        }
    }

    private void ValidateCron(ScheduledEvent scheduleEvent, StringNodeId cronNode)
    {
        var yaml = Config.Utf8Yaml!;
        var cronUtf8 = Arena.GetStringSlice(cronNode).AsSpan(yaml);
        if (!TryParseCronUtf8(cronUtf8, out var cron, out var reason))
        {
            AddEventError(scheduleEvent, $"on.schedule cron '{Decode(Arena.GetStringSlice(cronNode))}' is invalid: {reason}", Arena.GetStringRange(cronNode));
            return;
        }

        if (!TryGetMinimumIntervalMinutes(in cron, out var minimumIntervalMinutes))
        {
            return;
        }

        if (minimumIntervalMinutes < MinIntervalMinutes)
        {
            AddEventError(
                scheduleEvent,
                $"on.schedule cron '{Decode(Arena.GetStringSlice(cronNode))}' runs too frequently; the shortest interval is once every {MinIntervalMinutes} minutes",
                Arena.GetStringRange(cronNode));
        }
    }

    private void ValidateTimezone(ScheduledEvent scheduleEvent, StringNodeId timezoneNode)
    {
        var yaml = Config.Utf8Yaml!;
        var span = TrimAscii(Arena.GetStringSlice(timezoneNode).AsSpan(yaml));
        if (span.IsEmpty)
        {
            return;
        }

        if (IsUtcOrLocalUtf8(span))
        {
            AddEventError(scheduleEvent, $"on.schedule timezone '{Decode(Arena.GetStringSlice(timezoneNode))}' is invalid", Arena.GetStringRange(timezoneNode));
            return;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(Encoding.UTF8.GetString(span));
        }
        catch
        {
            if (!LooksLikeIanaTimezoneUtf8(span))
            {
                AddEventError(scheduleEvent, $"on.schedule timezone '{Decode(Arena.GetStringSlice(timezoneNode))}' is invalid", Arena.GetStringRange(timezoneNode));
            }
        }
    }

    private bool IsExpressionOrInterpolation(StringNodeId node)
    {
        return Arena.GetStringExpression(node).HasValue || Arena.GetStringSlice(node).AsSpan(Config.Utf8Yaml!).IndexOf("${{"u8) >= 0;
    }

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> span)
    {
        var start = 0;
        var end = span.Length;
        while (start < end && IsAsciiWs(span[start]))
        {
            start++;
        }

        while (end > start && IsAsciiWs(span[end - 1]))
        {
            end--;
        }

        return span[start..end];
    }

    private static bool IsAsciiWs(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsUtcOrLocalUtf8(ReadOnlySpan<byte> span)
    {
        return Utf8EqualsAsciiIgnoreCase(span, "UTC"u8)
            || Utf8EqualsAsciiIgnoreCase(span, "Local"u8);
    }

    private static bool LooksLikeIanaTimezoneUtf8(ReadOnlySpan<byte> timezone)
    {
        if (timezone.Length < 3)
        {
            return false;
        }

        var slash = timezone.IndexOf((byte)'/');
        if (slash <= 0)
        {
            return false;
        }

        if (!TryMatchIanaArea(timezone[..slash]))
        {
            return false;
        }

        for (var i = 0; i < timezone.Length; i++)
        {
            var b = timezone[i];
            var isLetter = b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';
            var isDigit = b is >= (byte)'0' and <= (byte)'9';
            var isSym = b is (byte)'/' or (byte)'_' or (byte)'-' or (byte)'+';
            if (!isLetter && !isDigit && !isSym)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryMatchIanaArea(ReadOnlySpan<byte> area)
    {
        return Utf8EqualsAsciiIgnoreCase(area, "Africa"u8)
            || Utf8EqualsAsciiIgnoreCase(area, "America"u8)
            || Utf8EqualsAsciiIgnoreCase(area, "Antarctica"u8)
            || Utf8EqualsAsciiIgnoreCase(area, "Arctic"u8)
            || Utf8EqualsAsciiIgnoreCase(area, "Asia"u8)
            || Utf8EqualsAsciiIgnoreCase(area, "Atlantic"u8)
            || Utf8EqualsAsciiIgnoreCase(area, "Australia"u8)
            || Utf8EqualsAsciiIgnoreCase(area, "Etc"u8)
            || Utf8EqualsAsciiIgnoreCase(area, "Europe"u8)
            || Utf8EqualsAsciiIgnoreCase(area, "Indian"u8)
            || Utf8EqualsAsciiIgnoreCase(area, "Pacific"u8);
    }

    private static bool Utf8EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
            if (l is >= (byte)'A' and <= (byte)'Z')
            {
                l = (byte)(l + 32);
            }

            if (r is >= (byte)'A' and <= (byte)'Z')
            {
                r = (byte)(r + 32);
            }

            if (l != r)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseCronUtf8(ReadOnlySpan<byte> cron, out CronBitset result, out string reason)
    {
        result = default;
        reason = string.Empty;
        Span<(int Start, int Length)> fields = stackalloc (int, int)[5];
        if (!TrySplitCronFields(cron, fields, out reason))
        {
            return false;
        }

        if (!TryParseMinutesField(cron.Slice(fields[0].Start, fields[0].Length), ref result.Minutes, out reason)
            || !TryParseHoursField(cron.Slice(fields[1].Start, fields[1].Length), ref result.Hours, out reason)
            || !TryParseDomField(cron.Slice(fields[2].Start, fields[2].Length), ref result.DayOfMonth, out reason)
            || !TryParseMonthsField(cron.Slice(fields[3].Start, fields[3].Length), ref result.Months, out reason)
            || !TryParseDowField(cron.Slice(fields[4].Start, fields[4].Length), ref result.DayOfWeekRaw, out reason))
        {
            return false;
        }

        result.NormalizeDayOfWeek();
        return true;
    }

    private static bool TrySplitCronFields(ReadOnlySpan<byte> cron, Span<(int Start, int Length)> fields, out string reason)
    {
        reason = string.Empty;
        var idx = 0;
        var pos = 0;
        var len = cron.Length;

        while (pos < len && IsAsciiWs(cron[pos]))
        {
            pos++;
        }

        while (pos < len && idx < 5)
        {
            var start = pos;
            while (pos < len && !IsAsciiWs(cron[pos]))
            {
                pos++;
            }

            if (start == pos)
            {
                reason = "cron must have exactly 5 fields";
                return false;
            }

            fields[idx++] = (start, pos - start);
            while (pos < len && IsAsciiWs(cron[pos]))
            {
                pos++;
            }
        }

        if (idx != 5 || pos < len)
        {
            reason = "cron must have exactly 5 fields";
            return false;
        }

        return true;
    }

    private static bool TryGetMinimumIntervalMinutes(in CronBitset cron, out int minimumIntervalMinutes)
    {
        minimumIntervalMinutes = int.MaxValue;
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime? previous = null;
        var matches = 0;

        for (var i = 0; i < 366 * 24 * 60; i++)
        {
            var current = start.AddMinutes(i);
            if (!cron.IsMatch(current))
            {
                continue;
            }

            matches++;
            if (previous is not null)
            {
                var delta = (int)(current - previous.Value).TotalMinutes;
                if (delta < minimumIntervalMinutes)
                {
                    minimumIntervalMinutes = delta;
                }

                if (minimumIntervalMinutes < MinIntervalMinutes)
                {
                    return true;
                }
            }

            previous = current;
            if (matches >= 6 && minimumIntervalMinutes != int.MaxValue)
            {
                return true;
            }
        }

        return minimumIntervalMinutes != int.MaxValue;
    }

    private static bool TryParseMinutesField(ReadOnlySpan<byte> field, ref ulong mask, out string reason)
    {
        return TryParseCommaField(field, ref mask, TryApplyMinutesSegment, out reason);
    }

    private static bool TryParseHoursField(ReadOnlySpan<byte> field, ref uint mask, out string reason)
    {
        return TryParseCommaField(field, ref mask, TryApplyHoursSegment, out reason);
    }

    private static bool TryParseDomField(ReadOnlySpan<byte> field, ref uint mask, out string reason)
    {
        return TryParseCommaField(field, ref mask, TryApplyDomSegment, out reason);
    }

    private static bool TryParseMonthsField(ReadOnlySpan<byte> field, ref ushort mask, out string reason)
    {
        return TryParseCommaField(field, ref mask, TryApplyMonthsSegment, out reason);
    }

    private static bool TryParseDowField(ReadOnlySpan<byte> field, ref byte mask, out string reason)
    {
        return TryParseCommaField(field, ref mask, TryApplyDowSegment, out reason);
    }

    private delegate bool ApplySegment<T>(ReadOnlySpan<byte> segment, ref T mask, out string reason);

    private static bool TryParseCommaField<T>(ReadOnlySpan<byte> field, ref T mask, ApplySegment<T> apply, out string reason)
    {
        reason = string.Empty;
        if (field.IsEmpty)
        {
            reason = "cron field is empty";
            return false;
        }

        while (!field.IsEmpty)
        {
            var comma = field.IndexOf((byte)',');
            ReadOnlySpan<byte> segment;
            if (comma < 0)
            {
                segment = field;
                field = default;
            }
            else
            {
                segment = field[..comma];
                field = field[(comma + 1)..];
            }

            segment = TrimAscii(segment);
            if (segment.IsEmpty)
            {
                reason = "cron field segment is empty";
                return false;
            }

            if (!apply(segment, ref mask, out reason))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryApplyMinutesSegment(ReadOnlySpan<byte> segment, ref ulong mask, out string reason)
    {
        return TryApplyNumericCronSegment(segment, 0, 59, ref mask, static (ref ulong m, int v) => m |= 1UL << v, out reason);
    }

    private static bool TryApplyHoursSegment(ReadOnlySpan<byte> segment, ref uint mask, out string reason)
    {
        return TryApplyNumericCronSegment(segment, 0, 23, ref mask, static (ref uint m, int v) => m |= 1u << v, out reason);
    }

    private static bool TryApplyDomSegment(ReadOnlySpan<byte> segment, ref uint mask, out string reason)
    {
        return TryApplyNumericCronSegment(segment, 1, 31, ref mask, static (ref uint m, int v) => m |= 1u << v, out reason);
    }

    private static bool TryApplyMonthsSegment(ReadOnlySpan<byte> segment, ref ushort mask, out string reason)
    {
        return TryApplyNamedOrNumericSegment(segment, 1, 12, true, ref mask, static (ref ushort m, int v) => m |= (ushort)(1 << v), out reason);
    }

    private static bool TryApplyDowSegment(ReadOnlySpan<byte> segment, ref byte mask, out string reason)
    {
        return TryApplyNamedOrNumericSegment(segment, 0, 7, false, ref mask, static (ref byte m, int v) => m |= (byte)(1 << v), out reason);
    }

    private delegate void SetBit<T>(ref T mask, int value);

    private static bool TryApplyNumericCronSegment<T>(
        ReadOnlySpan<byte> segment,
        int min,
        int max,
        ref T mask,
        SetBit<T> setBit,
        out string reason)
    {
        return TryApplyNamedOrNumericSegment(segment, min, max, false, ref mask, setBit, out reason);
    }

    private static bool TryApplyNamedOrNumericSegment<T>(
        ReadOnlySpan<byte> segment,
        int min,
        int max,
        bool monthNames,
        ref T mask,
        SetBit<T> setBit,
        out string reason)
    {
        reason = string.Empty;
        var slash = segment.IndexOf((byte)'/');
        ReadOnlySpan<byte> basePart;
        var step = 1;
        if (slash >= 0)
        {
            basePart = segment[..slash];
            var stepSpan = segment[(slash + 1)..];
            if (stepSpan.IsEmpty || !TryParseUtf8Int(stepSpan, out step) || step <= 0)
            {
                reason = "step in cron segment is invalid";
                return false;
            }
        }
        else
        {
            basePart = segment;
        }

        var rangeMin = min;
        var rangeMax = max;
        if (!(basePart.Length == 1 && basePart[0] == (byte)'*'))
        {
            var dash = basePart.IndexOf((byte)'-');
            if (dash >= 0)
            {
                var left = basePart[..dash];
                var right = basePart[(dash + 1)..];
                if (!TryParseCronIntOrName(left, min, max, monthNames, out rangeMin, out reason)
                    || !TryParseCronIntOrName(right, min, max, monthNames, out rangeMax, out reason)
                    || rangeMin > rangeMax)
                {
                    if (string.IsNullOrEmpty(reason))
                    {
                        reason = "range in cron segment is invalid";
                    }

                    return false;
                }
            }
            else
            {
                if (!TryParseCronIntOrName(basePart, min, max, monthNames, out rangeMin, out reason))
                {
                    if (string.IsNullOrEmpty(reason))
                    {
                        reason = "value in cron segment is out of range";
                    }

                    return false;
                }

                rangeMax = rangeMin;
            }
        }

        for (var v = rangeMin; v <= rangeMax; v += step)
        {
            setBit(ref mask, v);
        }

        return true;
    }

    private static bool TryParseCronIntOrName(
        ReadOnlySpan<byte> text,
        int min,
        int max,
        bool monthNames,
        out int value,
        out string reason)
    {
        reason = string.Empty;
        if (TryParseUtf8Int(text, out value) && value >= min && value <= max)
        {
            return true;
        }

        if (monthNames && TryParseMonthName(text, out value) && value >= min && value <= max)
        {
            return true;
        }

        if (!monthNames && TryParseDowName(text, out value) && value >= min && value <= max)
        {
            return true;
        }

        reason = "invalid cron value";
        return false;
    }

    private static bool TryParseUtf8Int(ReadOnlySpan<byte> s, out int value)
    {
        value = 0;
        if (s.IsEmpty)
        {
            return false;
        }

        var acc = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var b = s[i];
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            var digit = b - (byte)'0';
            if (acc > (int.MaxValue - digit) / 10)
            {
                return false;
            }

            acc = acc * 10 + digit;
        }

        value = acc;
        return true;
    }

    private static bool TryParseMonthName(ReadOnlySpan<byte> s, out int month)
    {
        month = 0;
        if (s.Length != 3)
        {
            return false;
        }

        month = 0;
        if (Utf8EqualsAsciiIgnoreCase(s, "jan"u8))
        {
            month = 1;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "feb"u8))
        {
            month = 2;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "mar"u8))
        {
            month = 3;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "apr"u8))
        {
            month = 4;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "may"u8))
        {
            month = 5;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "jun"u8))
        {
            month = 6;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "jul"u8))
        {
            month = 7;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "aug"u8))
        {
            month = 8;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "sep"u8))
        {
            month = 9;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "oct"u8))
        {
            month = 10;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "nov"u8))
        {
            month = 11;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "dec"u8))
        {
            month = 12;
        }

        return month != 0;
    }

    private static bool TryParseDowName(ReadOnlySpan<byte> s, out int dow)
    {
        dow = -1;
        if (s.Length != 3)
        {
            return false;
        }

        if (Utf8EqualsAsciiIgnoreCase(s, "sun"u8))
        {
            dow = 0;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "mon"u8))
        {
            dow = 1;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "tue"u8))
        {
            dow = 2;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "wed"u8))
        {
            dow = 3;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "thu"u8))
        {
            dow = 4;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "fri"u8))
        {
            dow = 5;
        }
        else if (Utf8EqualsAsciiIgnoreCase(s, "sat"u8))
        {
            dow = 6;
        }

        return dow >= 0;
    }

    private struct CronBitset
    {
        public ulong Minutes;
        public uint Hours;
        public uint DayOfMonth;
        public ushort Months;
        public byte DayOfWeekRaw;
        public byte DayOfWeekNorm;

        public void NormalizeDayOfWeek()
        {
            byte norm = 0;
            for (var i = 0; i <= 6; i++)
            {
                if ((DayOfWeekRaw & (1 << i)) != 0)
                {
                    norm |= (byte)(1 << i);
                }
            }

            if ((DayOfWeekRaw & (1 << 7)) != 0)
            {
                norm |= 1;
            }

            DayOfWeekNorm = norm;
        }

        public readonly bool IsMatch(DateTime dt)
        {
            if ((Minutes & (1UL << dt.Minute)) == 0
                || (Hours & (1u << dt.Hour)) == 0
                || (Months & (ushort)(1 << dt.Month)) == 0)
            {
                return false;
            }

            var dayOfMonthMatch = (DayOfMonth & (1u << dt.Day)) != 0;
            var dayOfWeekMatch = (DayOfWeekNorm & (1 << (int)dt.DayOfWeek)) != 0;
            var domWildcard = DayOfMonth == AllDomMask;
            var dowWildcard = DayOfWeekNorm == AllDowNormMask;

            if (domWildcard && dowWildcard)
            {
                return true;
            }

            if (domWildcard)
            {
                return dayOfWeekMatch;
            }

            if (dowWildcard)
            {
                return dayOfMonthMatch;
            }

            return dayOfMonthMatch || dayOfWeekMatch;
        }
    }
}

using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class ScheduleEventRule : RuleBase
{
    private const int MinIntervalMinutes = 5;

    public override string Id => "schedule-event";

    public override string Name => "Schedule Event Rule";

    public override void VisitEvent(Event ev)
    {
        if (ev is not ScheduledEvent scheduleEvent || Config.Utf8Yaml is null)
        {
            return;
        }

        for (var i = 0; i < scheduleEvent.Schedules.Count; i++)
        {
            var entry = scheduleEvent.Schedules[i];
            ValidateScheduleEntry(scheduleEvent, entry);
        }
    }

    private void ValidateScheduleEntry(ScheduledEvent scheduleEvent, ScheduleEntry entry)
    {
        if (entry.Cron is not null && !IsExpressionOrInterpolation(entry.Cron))
        {
            ValidateCron(scheduleEvent, entry.Cron);
        }

        if (entry.Timezone is not null && !IsExpressionOrInterpolation(entry.Timezone))
        {
            ValidateTimezone(scheduleEvent, entry.Timezone);
        }
    }

    private void ValidateCron(ScheduledEvent scheduleEvent, StringNode cronNode)
    {
        var text = Decode(cronNode.Value);
        if (!TryParseCron(text, out var cron, out var reason))
        {
            AddEventError(scheduleEvent, $"on.schedule cron '{text}' is invalid: {reason}", cronNode.Range);
            return;
        }

        if (!TryGetMinimumIntervalMinutes(cron, out var minimumIntervalMinutes))
        {
            return;
        }

        if (minimumIntervalMinutes < MinIntervalMinutes)
        {
            AddEventError(
                scheduleEvent,
                $"on.schedule cron '{text}' runs too frequently; the shortest interval is once every {MinIntervalMinutes} minutes",
                cronNode.Range);
        }
    }

    private void ValidateTimezone(ScheduledEvent scheduleEvent, StringNode timezoneNode)
    {
        var timezone = Decode(timezoneNode.Value);
        if (string.IsNullOrWhiteSpace(timezone))
        {
            return;
        }

        if (string.Equals(timezone, "UTC", StringComparison.Ordinal) || string.Equals(timezone, "Local", StringComparison.Ordinal))
        {
            AddEventError(scheduleEvent, $"on.schedule timezone '{timezone}' is invalid", timezoneNode.Range);
            return;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch
        {
            // Cross-platform fallback: accept common IANA format and reject obvious invalid values.
            if (!LooksLikeIanaTimezone(timezone))
            {
                AddEventError(scheduleEvent, $"on.schedule timezone '{timezone}' is invalid", timezoneNode.Range);
            }
        }
    }

    private bool IsExpressionOrInterpolation(StringNode node)
    {
        return node.Expression is not null || node.Value.AsSpan(Config.Utf8Yaml).IndexOf("${{"u8) >= 0;
    }

    private static bool LooksLikeIanaTimezone(string timezone)
    {
        if (timezone.Length < 3 || !timezone.Contains('/'))
        {
            return false;
        }

        var slashIndex = timezone.IndexOf('/');
        if (slashIndex <= 0)
        {
            return false;
        }

        var area = timezone[..slashIndex];
        if (!s_ianaAreas.Contains(area))
        {
            return false;
        }

        foreach (var c in timezone)
        {
            var isLetter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            var isDigit = c >= '0' && c <= '9';
            var isAllowedSymbol = c is '/' or '_' or '-' or '+';
            if (!isLetter && !isDigit && !isAllowedSymbol)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseCron(string text, out CronExpression cron, out string reason)
    {
        cron = default;
        reason = string.Empty;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            reason = "cron must have exactly 5 fields";
            return false;
        }

        if (!TryParseField(parts[0], 0, 59, null, out cron.Minutes, out reason))
        {
            return false;
        }

        if (!TryParseField(parts[1], 0, 23, null, out cron.Hours, out reason))
        {
            return false;
        }

        if (!TryParseField(parts[2], 1, 31, null, out cron.DaysOfMonth, out reason))
        {
            return false;
        }

        if (!TryParseField(parts[3], 1, 12, s_monthNames, out cron.Months, out reason))
        {
            return false;
        }

        if (!TryParseField(parts[4], 0, 7, s_dayOfWeekNames, out cron.DaysOfWeekRaw, out reason))
        {
            return false;
        }

        cron.NormalizeDayOfWeek();
        return true;
    }

    private static bool TryGetMinimumIntervalMinutes(CronExpression cron, out int minimumIntervalMinutes)
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

    private static bool TryParseField(
        string fieldText,
        int min,
        int max,
        IReadOnlyDictionary<string, int>? namedValues,
        out bool[] values,
        out string reason)
    {
        values = new bool[max + 1];
        reason = string.Empty;
        var segments = fieldText.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            reason = $"field '{fieldText}' is empty";
            return false;
        }

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (!TryApplySegment(segment, min, max, namedValues, values, out reason))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryApplySegment(
        string segment,
        int min,
        int max,
        IReadOnlyDictionary<string, int>? namedValues,
        bool[] values,
        out string reason)
    {
        reason = string.Empty;
        var slashIndex = segment.IndexOf('/');
        var basePart = slashIndex >= 0 ? segment[..slashIndex] : segment;
        var step = 1;

        if (slashIndex >= 0)
        {
            if (slashIndex == segment.Length - 1 || !int.TryParse(segment[(slashIndex + 1)..], out step) || step <= 0)
            {
                reason = $"step in segment '{segment}' is invalid";
                return false;
            }
        }

        var rangeMin = min;
        var rangeMax = max;

        if (basePart != "*")
        {
            var dashIndex = basePart.IndexOf('-');
            if (dashIndex >= 0)
            {
                if (!TryParseValue(basePart[..dashIndex], min, max, namedValues, out rangeMin)
                    || !TryParseValue(basePart[(dashIndex + 1)..], min, max, namedValues, out rangeMax)
                    || rangeMin > rangeMax)
                {
                    reason = $"range in segment '{segment}' is invalid";
                    return false;
                }
            }
            else
            {
                if (!TryParseValue(basePart, min, max, namedValues, out rangeMin))
                {
                    reason = $"value '{basePart}' is out of range";
                    return false;
                }

                rangeMax = rangeMin;
            }
        }

        for (var v = rangeMin; v <= rangeMax; v += step)
        {
            values[v] = true;
        }

        return true;
    }

    private static bool TryParseValue(
        string text,
        int min,
        int max,
        IReadOnlyDictionary<string, int>? namedValues,
        out int value)
    {
        if (namedValues is not null && namedValues.TryGetValue(text, out value))
        {
            return value >= min && value <= max;
        }

        if (!int.TryParse(text, out value))
        {
            return false;
        }

        return value >= min && value <= max;
    }

    private static readonly IReadOnlyDictionary<string, int> s_monthNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1,
        ["feb"] = 2,
        ["mar"] = 3,
        ["apr"] = 4,
        ["may"] = 5,
        ["jun"] = 6,
        ["jul"] = 7,
        ["aug"] = 8,
        ["sep"] = 9,
        ["oct"] = 10,
        ["nov"] = 11,
        ["dec"] = 12,
    };

    private static readonly IReadOnlyDictionary<string, int> s_dayOfWeekNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["sun"] = 0,
        ["mon"] = 1,
        ["tue"] = 2,
        ["wed"] = 3,
        ["thu"] = 4,
        ["fri"] = 5,
        ["sat"] = 6,
    };

    private static readonly IReadOnlySet<string> s_ianaAreas = new HashSet<string>(StringComparer.Ordinal)
    {
        "Africa",
        "America",
        "Antarctica",
        "Arctic",
        "Asia",
        "Atlantic",
        "Australia",
        "Etc",
        "Europe",
        "Indian",
        "Pacific",
    };

    private struct CronExpression
    {
        public bool[] Minutes;
        public bool[] Hours;
        public bool[] DaysOfMonth;
        public bool[] Months;
        public bool[] DaysOfWeekRaw;
        public bool[] DaysOfWeek;

        public void NormalizeDayOfWeek()
        {
            DaysOfWeek = new bool[7];
            for (var i = 0; i < DaysOfWeekRaw.Length; i++)
            {
                if (!DaysOfWeekRaw[i])
                {
                    continue;
                }

                var day = i == 7 ? 0 : i;
                if (day >= 0 && day < DaysOfWeek.Length)
                {
                    DaysOfWeek[day] = true;
                }
            }
        }

        public bool IsMatch(DateTime dt)
        {
            if (!Minutes[dt.Minute] || !Hours[dt.Hour] || !Months[dt.Month])
            {
                return false;
            }

            var dayOfMonthMatch = DaysOfMonth[dt.Day];
            var dayOfWeekMatch = DaysOfWeek[(int)dt.DayOfWeek];
            var domWildcard = IsWildcard(DaysOfMonth, 1);
            var dowWildcard = IsWildcard(DaysOfWeek, 0);

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

        private static bool IsWildcard(bool[] values, int start)
        {
            for (var i = start; i < values.Length; i++)
            {
                if (!values[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}

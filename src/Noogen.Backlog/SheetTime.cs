using System.Globalization;

namespace Noogen.Backlog
{
    /// <summary>
    /// Converts between the instants we reason about and the wall-clock serial numbers Sheets
    /// stores.
    ///
    /// A Sheets datetime cell is a count of days since 1899-12-30, interpreted against the
    /// spreadsheet's own timezone. It carries no offset, which buys native rendering, correct
    /// numeric sorting, and working date filters — at the cost of the fall-back hour being
    /// genuinely ambiguous on read-back. Both edge cases are resolved deterministically below.
    /// </summary>
    public static class SheetTime
    {
        /// <summary>Sheets' epoch. 1899-12-30, not 1900-01-01 — it inherits the Lotus leap-year bug.</summary>
        public static readonly DateTime Epoch = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);

        /// <summary>Rendered by Sheets in the spreadsheet's timezone. Unambiguous and sortable.</summary>
        public const string DisplayPattern = "yyyy-mm-dd hh:mm";

        const double SecondsPerDay = 86400d;

        /// <summary>
        /// Quantised to whole seconds. A serial is a fractional day held in a double, which cannot
        /// represent most instants exactly — without this, writing 23:59:00 and reading it back
        /// yields 23:58:59.9999998. Sheets renders to the minute anyway, so sub-second precision
        /// buys nothing and costs exact round-trips.
        /// </summary>
        public static double ToSerial(DateTimeOffset instant, TimeZoneInfo zone)
        {
            var wallClock = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
            return Math.Round((wallClock - Epoch).TotalSeconds) / SecondsPerDay;
        }

        public static DateTimeOffset FromSerial(double serial, TimeZoneInfo zone)
        {
            var wallClock = Epoch.AddSeconds(Math.Round(serial * SecondsPerDay));
            return FromWallClock(wallClock, zone);
        }

        /// <summary>
        /// Resolves a naive wall-clock time against a zone, handling both DST discontinuities:
        ///
        /// - <b>Spring forward</b> leaves a gap where the wall clock never existed. We shift
        ///   forward by the gap so the value stays monotonic rather than throwing.
        /// - <b>Fall back</b> repeats an hour, so one wall clock maps to two instants. We pick
        ///   the larger UTC offset, which is the <em>earlier</em> of the two. Choosing the earlier
        ///   one keeps an activity log monotonic, and the worst-case error is under an hour, once
        ///   a year, on a metric reported in days.
        /// </summary>
        public static DateTimeOffset FromWallClock(DateTime wallClock, TimeZoneInfo zone)
        {
            var naive = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

            if (zone.IsInvalidTime(naive))
            {
                var adjustment = zone.GetUtcOffset(naive.AddDays(1)) - zone.GetUtcOffset(naive.AddDays(-1));
                return new DateTimeOffset(naive.Add(adjustment), zone.GetUtcOffset(naive.Add(adjustment)));
            }

            if (zone.IsAmbiguousTime(naive))
            {
                var offsets = zone.GetAmbiguousTimeOffsets(naive);
                return new DateTimeOffset(naive, offsets.Max());
            }

            return new DateTimeOffset(naive, zone.GetUtcOffset(naive));
        }

        /// <summary>
        /// Resolves an IANA id such as America/New_York. Works on Windows, Linux, and macOS on
        /// .NET 6+ via ICU; the failure mode worth naming is invariant globalization, where the
        /// timezone database is absent and everything would silently collapse to UTC.
        /// </summary>
        public static TimeZoneInfo ResolveZone(string? timeZoneId)
        {
            if (string.IsNullOrWhiteSpace(timeZoneId))
                return TimeZoneInfo.Utc;

            var id = timeZoneId.Trim();

            if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase))
                return TimeZoneInfo.Utc;

            if (AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) && invariant)
            {
                throw new InvalidOperationException(
                    $"Cannot resolve timezone '{id}': this build runs in invariant globalization mode, which ships no timezone database. " +
                    "Set InvariantGlobalization to false, or set the backlog timezone to UTC.");
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // A Windows id (e.g. "Eastern Standard Time") in the config is a likely mistake
                // rather than a fatal one — translate it and tell the user to write IANA.
                if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana))
                    return TimeZoneInfo.FindSystemTimeZoneById(iana);

                throw new InvalidOperationException(
                    $"Unknown timezone '{id}'. Use an IANA id such as America/New_York, Europe/London, or UTC. " +
                    $"Set it on the Config tab under '{BacklogSettings.TimeZoneKey}'.");
            }
        }

        /// <summary>The machine's zone as an IANA id, for seeding the config at init.</summary>
        public static string LocalIanaId()
        {
            var local = TimeZoneInfo.Local.Id;

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(local, out var iana))
                return iana;

            return local;
        }

        public static string Format(DateTimeOffset instant, TimeZoneInfo zone) =>
            TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        public static string FormatWithZone(DateTimeOffset instant, TimeZoneInfo zone)
        {
            var local = TimeZoneInfo.ConvertTime(instant, zone);
            var abbreviation = zone == TimeZoneInfo.Utc
                ? "UTC"
                : local.ToString("zzz", CultureInfo.InvariantCulture);

            return $"{local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {abbreviation}";
        }

        /// <summary>
        /// Culture-safe read of a cell that may arrive as a double, a long, or a string depending
        /// on how the value was written. Never routes through the current culture.
        /// </summary>
        public static double? AsNumber(object? cell)
        {
            switch (cell)
            {
                case null:
                    return null;
                case double value:
                    return value;
                case float value:
                    return value;
                case long value:
                    return value;
                case int value:
                    return value;
                case decimal value:
                    return (double)value;
                case string text:
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
                default:
                    return double.TryParse(cell.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fallback) ? fallback : null;
            }
        }
    }
}

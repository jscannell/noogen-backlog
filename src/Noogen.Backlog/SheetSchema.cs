using System.Globalization;

namespace Noogen.Backlog
{
    /// <summary>
    /// Column names, one ordered list per lifecycle tab. These are the names written into the
    /// header row at init; at runtime every read resolves columns by looking the name up in the
    /// header rather than assuming a position, so a human may reorder or add columns freely.
    /// </summary>
    public static class SheetSchema
    {
        public const string Id = "id";
        public const string Title = "title";
        public const string Type = "type";
        public const string Area = "area";
        public const string Owner = "owner";
        public const string Bv = "bv";
        public const string Tc = "tc";
        public const string Rroe = "rroe";
        public const string Size = "size";
        public const string Cod = "cod";
        public const string Wsjf = "wsjf";
        public const string Rank = "rank";
        public const string State = "state";
        public const string BlockedReason = "blocked_reason";
        public const string BlockedAt = "blocked_at";
        public const string StartedAt = "started_at";
        public const string Outcome = "outcome";
        public const string ArchivedAt = "archived_at";
        public const string LeadDays = "lead_days";
        public const string CycleDays = "cycle_days";
        public const string Created = "created";
        public const string Updated = "updated";
        public const string DocId = "doc_id";
        public const string DocUrl = "doc_url";

        public const string ConfigTabName = "Config";

        static readonly string[] Shared =
        [
            Id, Title, Type, Area, Owner, Bv, Tc, Rroe, Size, Cod, Wsjf
        ];

        static readonly string[] Trailing =
        [
            Created, Updated, DocId, DocUrl
        ];

        public static IReadOnlyList<string> Columns(BacklogPhase phase)
        {
            var columns = new List<string>(Shared);

            switch (phase)
            {
                case BacklogPhase.Backlog:
                    columns.Add(Rank);
                    break;
                case BacklogPhase.InProgress:
                    columns.AddRange([State, BlockedReason, BlockedAt, StartedAt]);
                    break;
                case BacklogPhase.Archive:
                    columns.AddRange([Outcome, StartedAt, ArchivedAt, LeadDays, CycleDays]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown backlog phase.");
            }

            columns.AddRange(Trailing);
            return columns;
        }

        /// <summary>Owned by the Sheet on the Backlog tab. The store must never write these.</summary>
        public static readonly IReadOnlyList<string> FormulaColumns = [Cod, Wsjf, Rank];

        /// <summary>
        /// Formatted as plain text at init so Sheets does not silently coerce an ISO-8601 string
        /// into a locale-formatted date serial and break the round-trip.
        /// </summary>
        public static readonly IReadOnlyList<string> TimestampColumns = [Created, Updated, StartedAt, BlockedAt, ArchivedAt];

        /// <summary>Machine plumbing. Hidden from humans, read by reindex/doctor.</summary>
        public static readonly IReadOnlyList<string> HiddenColumns = [DocId, DocUrl];
    }

    /// <summary>A single compact UTC representation everywhere: Sheet cells, frontmatter, and JSON.</summary>
    public static class Iso
    {
        public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        public static string ToText(DateTimeOffset value) =>
            value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

        public static string? ToText(DateTimeOffset? value) => value.HasValue ? ToText(value.Value) : null;

        public static DateTimeOffset Parse(string text, string field)
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed;

            throw new FormatException($"'{text}' in '{field}' is not a valid timestamp. Expected ISO-8601, e.g. 2026-08-06T17:22:31Z.");
        }

        public static DateTimeOffset? ParseOptional(string? text, string field) =>
            string.IsNullOrWhiteSpace(text) ? null : Parse(text.Trim(), field);
    }
}

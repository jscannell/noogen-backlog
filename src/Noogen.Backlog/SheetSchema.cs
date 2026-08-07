using System.Globalization;
using System.Text;

namespace Noogen.Backlog
{
    /// <summary>
    /// Column names, one ordered list per lifecycle tab. These are the names written into the
    /// header row at init; at runtime every read resolves columns by looking the name up in the
    /// header rather than assuming a position, so a human may reorder or add columns freely.
    ///
    /// The names are the words a person would use, not our abbreviations — the Sheet is the face
    /// of the backlog and nobody remembers what 'rroe' meant. Earlier backlogs were created with
    /// the short names, so <see cref="Canonical"/> accepts those too and nothing relabels an
    /// existing header row; see <see cref="SheetTable"/>.
    /// </summary>
    public static class SheetSchema
    {
        public const string Id = "ID";
        public const string Title = "Title";
        public const string Type = "Type";
        public const string Area = "Area";
        public const string Owner = "Owner";
        public const string BusinessValue = "Business Value";
        public const string TimeCriticality = "Time Criticality";
        public const string RiskOpportunity = "Risk & Opportunity";
        public const string JobSize = "Job Size";
        public const string CostOfDelay = "Cost of Delay";
        public const string Wsjf = "WSJF";
        public const string Rank = "Rank";
        public const string State = "State";
        public const string BlockedReason = "Blocked Reason";
        public const string BlockedAt = "Blocked At";
        public const string StartedAt = "Started At";
        public const string Outcome = "Outcome";
        public const string ArchivedAt = "Archived At";
        public const string LeadTime = "Lead Time (days)";
        public const string CycleTime = "Cycle Time (days)";
        public const string Created = "Created";
        public const string Updated = "Updated";

        /// <summary>Drive's own file id for the ticket document. Drive has no paths, so this is the
        /// only handle we have for reading, editing, and moving that file.</summary>
        public const string DriveFileId = "Drive File ID";

        /// <summary>A cached Drive webViewLink, so the Title cell can be hyperlinked on every row
        /// write without a files.get round trip.</summary>
        public const string DriveFileLink = "Drive File Link";

        public const string ConfigTabName = "Config";

        static readonly string[] Shared =
        [
            Id, Title, Type, Area, Owner, BusinessValue, TimeCriticality, RiskOpportunity, JobSize, CostOfDelay, Wsjf
        ];

        static readonly string[] Trailing =
        [
            Created, Updated, DriveFileId, DriveFileLink
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
                    columns.AddRange([Outcome, StartedAt, ArchivedAt, LeadTime, CycleTime]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown backlog phase.");
            }

            columns.AddRange(Trailing);
            return columns;
        }

        /// <summary>
        /// Stored as real Sheets datetime serials, not text, and given a DATE_TIME number format
        /// at init. Sheets then renders them in the spreadsheet's timezone, sorts them correctly
        /// across DST, and can filter on them — none of which works with an opaque string.
        /// </summary>
        public static readonly IReadOnlyList<string> TimestampColumns = [Created, Updated, StartedAt, BlockedAt, ArchivedAt];

        /// <summary>Machine plumbing. Hidden from humans, read by reindex/doctor.</summary>
        public static readonly IReadOnlyList<string> HiddenColumns = [DriveFileId, DriveFileLink];

        /// <summary>
        /// The short names this schema used before the columns were spelled out. A backlog created
        /// then still carries them in its header row, and nothing relabels a header row — it is a
        /// human's to edit — so every read goes through <see cref="Canonical"/> and understands
        /// both spellings. Ticket documents use the same lookup, which is what lets a person write
        /// `job size` or `bv` by hand; there the current spelling is written back on the next save.
        /// </summary>
        static readonly KeyValuePair<string, string>[] LegacyNames =
        [
            new("bv", BusinessValue),
            new("tc", TimeCriticality),
            new("rroe", RiskOpportunity),
            new("size", JobSize),
            new("cod", CostOfDelay),
            new("lead_days", LeadTime),
            new("cycle_days", CycleTime),
            new("doc_id", DriveFileId),
            new("doc_url", DriveFileLink)
        ];

        static readonly IReadOnlyDictionary<string, string> Spellings = BuildSpellings();

        static Dictionary<string, string> BuildSpellings()
        {
            var spellings = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var phase in BacklogPhaseExtensions.All)
            {
                foreach (var column in Columns(phase))
                    spellings[Normalize(column)] = column;
            }

            foreach (var legacy in LegacyNames)
                spellings[Normalize(legacy.Key)] = legacy.Value;

            return spellings;
        }

        /// <summary>
        /// The canonical name for a header a human may have written in any accepted spelling — the
        /// name we write, one of the legacy short names, or the same words punctuated differently.
        /// Null for a column we do not own, which must be left exactly as it is.
        /// </summary>
        public static string? Canonical(string? header)
        {
            if (string.IsNullOrWhiteSpace(header))
                return null;

            return Spellings.TryGetValue(Normalize(header), out var canonical) ? canonical : null;
        }

        /// <summary>
        /// Letters and digits only, lowercased, so 'Blocked Reason', 'blocked_reason' and
        /// 'blockedreason' are one column. Whole strings only — a prefix match here would confuse
        /// <see cref="Id"/> with the id half of <see cref="DriveFileId"/>.
        /// </summary>
        static string Normalize(string text)
        {
            var normalized = new StringBuilder(text.Length);

            foreach (var character in text)
            {
                if (char.IsLetterOrDigit(character))
                    normalized.Append(char.ToLowerInvariant(character));
            }

            return normalized.ToString();
        }
    }

    /// <summary>
    /// Compact UTC text. Used for the CLI's JSON contract and for reading any timestamp a human
    /// typed as text. Sheet cells are datetime serials — see <see cref="SheetTime"/>.
    /// </summary>
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

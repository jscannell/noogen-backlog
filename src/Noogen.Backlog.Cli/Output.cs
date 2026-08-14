using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Cli
{
    /// <summary>
    /// Human output is a compact table; `--json` is the machine contract. Agents parse the JSON,
    /// which is why every verb supports it and why the shapes here are stable and null-elided.
    /// </summary>
    public static class Output
    {
        static readonly JsonSerializerOptions Json = new()
        {
            // Not indented, deliberately. The agent reading this pays for every byte, and
            // indentation is a quarter of a list response — 22,598 characters of `list --json`
            // over a 44-ticket backlog, of which 5,284 were spaces and newlines. Whitespace was
            // never part of the contract the shapes promise, so compacting costs nothing; pipe
            // through a formatter when reading one of these by hand.
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // Console output, not HTML. The default encoder escapes quotes, angle brackets, and
            // ampersands into \uXXXX noise, which makes messages containing paths and examples
            // hard to read for both people and models.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static void WriteJson(object payload) => Console.WriteLine(JsonSerializer.Serialize(payload, Json));

        /// <summary>
        /// The names <c>--fields</c> accepts, taken from <see cref="TicketView"/> itself rather
        /// than listed here, so a property added there is selectable the same day. They are the
        /// names as they appear on the wire, which is what the caller has in front of them.
        /// </summary>
        static readonly HashSet<string> TicketFieldNames = new(
            typeof(TicketView)
                .GetProperties()
                .Where(property => property.CanRead)
                .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)),
            StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reads a <c>--fields</c> value into the set <see cref="Project"/> keeps, or null for
        /// "everything". An unrecognised name is a usage error naming the alternatives: the whole
        /// point of the option is to ask for less, and silently ignoring a typo would answer with
        /// a column the caller did not get and cannot see is missing.
        /// </summary>
        public static IReadOnlySet<string>? ParseFields(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var names = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (names.Length == 0)
                throw new UsageException("--fields needs at least one name. It accepts: " + KnownFields() + ".");

            var unknown = names.Where(name => !TicketFieldNames.Contains(name)).ToList();

            if (unknown.Count > 0)
                throw new UsageException(
                    $"--fields does not know {string.Join(", ", unknown.Select(name => "'" + name + "'"))}. "
                    + "It accepts: " + KnownFields() + ".");

            return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        static string KnownFields() =>
            string.Join(", ", typeof(TicketView)
                .GetProperties()
                .Where(property => property.CanRead)
                .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)));

        /// <summary>
        /// A ticket as JSON, narrowed to the named fields. Null keeps everything.
        ///
        /// It projects the serialised node rather than a hand-written dictionary so the shapes
        /// stay identical to an unprojected response — same casing, same null-elision, same
        /// values. Asking for a field a given ticket does not carry leaves it absent, exactly as
        /// it would be without <c>--fields</c>: absent still means absent.
        /// </summary>
        public static JsonNode Project(TicketView view, IReadOnlySet<string>? fields)
        {
            var node = JsonSerializer.SerializeToNode(view, Json)!;

            if (fields is null)
                return node;

            var projected = node.AsObject();

            foreach (var key in projected.Select(pair => pair.Key).Where(key => !fields.Contains(key)).ToList())
                projected.Remove(key);

            return projected;
        }

        public static void WriteLine(string text = "") => Console.WriteLine(text);

        public static void WriteError(string text) => Console.Error.WriteLine(text);

        public static void WriteTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
        {
            if (rows.Count == 0)
            {
                Console.WriteLine("(nothing)");
                return;
            }

            var widths = new int[headers.Count];
            for (var i = 0; i < headers.Count; i++)
            {
                widths[i] = headers[i].Length;
                foreach (var row in rows)
                {
                    if (i < row.Count)
                        widths[i] = Math.Max(widths[i], row[i].Length);
                }
            }

            Console.WriteLine(Render(headers, widths));
            Console.WriteLine(string.Join("  ", widths.Select(width => new string('-', width))));

            foreach (var row in rows)
                Console.WriteLine(Render(row, widths));
        }

        static string Render(IReadOnlyList<string> cells, int[] widths)
        {
            var builder = new StringBuilder();

            for (var i = 0; i < widths.Length; i++)
            {
                if (i > 0)
                    builder.Append("  ");

                var value = i < cells.Count ? cells[i] : string.Empty;
                builder.Append(value.PadRight(widths[i]));
            }

            return builder.ToString().TrimEnd();
        }

        public static string Number(double? value) =>
            value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : "-";

        public static string Number(int? value) =>
            value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";

        public static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    /// <summary>
    /// Says out loud that a command is waiting on Google rather than hung. Stderr, always: stdout
    /// under `--json` is a single document and an agent parses it, so nothing else may land there.
    /// </summary>
    public class ConsoleRetryListener : IRetryListener
    {
        public void RateLimited(int attempt, int maxAttempts, TimeSpan delay) =>
            Output.WriteError(string.Format(
                CultureInfo.InvariantCulture,
                "Google is rate limiting requests; waiting {0:0.#}s before retry {1} of {2}.",
                delay.TotalSeconds,
                attempt,
                maxAttempts - 1));
    }

    /// <summary>Stable JSON projection of a ticket. Nulls are elided, so absent means absent.</summary>
    public class TicketView
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string Phase { get; set; } = string.Empty;

        public string? Area { get; set; }

        public string? Owner { get; set; }

        public int? Bv { get; set; }

        public int? Tc { get; set; }

        public int? Rroe { get; set; }

        public int? Size { get; set; }

        public int? Cod { get; set; }

        public double? Wsjf { get; set; }

        public int? Rank { get; set; }

        public string? State { get; set; }

        public string? BlockedReason { get; set; }

        public string? BlockedAt { get; set; }

        public string? StartedAt { get; set; }

        public double? AgeDays { get; set; }

        public bool? Aging { get; set; }

        public string? Outcome { get; set; }

        public string? ArchivedAt { get; set; }

        public double? LeadDays { get; set; }

        public double? CycleDays { get; set; }

        public string Created { get; set; } = string.Empty;

        public string Updated { get; set; } = string.Empty;

        public string? DocUrl { get; set; }

        /// <summary>
        /// Where a search hit — `name`, `body`, or both. Absent on every other verb, the same way
        /// <see cref="Aging"/> is absent outside `wip`: a ticket does not have a match, a search
        /// result does.
        /// </summary>
        public IReadOnlyList<string>? Match { get; set; }

        public static TicketView From(Ticket ticket, DateTimeOffset? now = null, double? agingThreshold = null)
        {
            var view = new TicketView
            {
                Id = ticket.Id,
                Title = ticket.Title,
                Type = Vocabulary.ToWire(ticket.Type),
                Phase = Vocabulary.ToWire(ticket.Phase),
                Area = NullIfEmpty(ticket.Area),
                Owner = NullIfEmpty(ticket.Owner),
                Bv = ticket.Score.BusinessValue,
                Tc = ticket.Score.TimeCriticality,
                Rroe = ticket.Score.RiskReductionOpportunityEnablement,
                Size = ticket.Score.JobSize,
                Cod = ticket.Score.CostOfDelay,
                Wsjf = ticket.Score.Value,
                Rank = ticket.Rank,
                State = ticket.State.HasValue ? Vocabulary.ToWire(ticket.State.Value) : null,
                BlockedReason = NullIfEmpty(ticket.BlockedReason),
                BlockedAt = Iso.ToText(ticket.BlockedAt),
                StartedAt = Iso.ToText(ticket.StartedAt),
                Outcome = ticket.Outcome.HasValue ? Vocabulary.ToWire(ticket.Outcome.Value) : null,
                ArchivedAt = Iso.ToText(ticket.ArchivedAt),
                LeadDays = ticket.LeadDays,
                CycleDays = ticket.CycleDays,
                Created = Iso.ToText(ticket.Created),
                Updated = Iso.ToText(ticket.Updated),
                DocUrl = NullIfEmpty(ticket.DocUrl)
            };

            if (now.HasValue)
            {
                view.AgeDays = ticket.AgeDays(now.Value);

                if (agingThreshold.HasValue && view.AgeDays.HasValue)
                    view.Aging = view.AgeDays.Value > agingThreshold.Value;
            }

            return view;
        }

        public static TicketView From(TicketMatch match, DateTimeOffset? now = null)
        {
            var view = From(match.Ticket, now);
            view.Match = match.Where;

            return view;
        }

        static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

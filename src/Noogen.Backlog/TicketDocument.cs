using System.Globalization;
using System.Text;

namespace Noogen.Backlog
{
    /// <summary>
    /// The markdown ticket: `---` delimited frontmatter mirroring the Sheet row, then the body.
    ///
    /// The Sheet is the source of truth — <c>doctor</c> reports drift and <c>reindex</c> can
    /// rebuild Sheet rows from these documents if the index is ever damaged. Unrecognised
    /// frontmatter keys round-trip untouched so a field a human adds by hand is not eaten.
    /// </summary>
    public class TicketDocument
    {
        public const string Delimiter = "---";

        public Ticket Ticket { get; set; } = new();

        public string Body { get; set; } = string.Empty;

        public static TicketDocument Parse(string content)
        {
            ArgumentNullException.ThrowIfNull(content);

            var normalized = content.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');

            if (lines.Length == 0 || lines[0].Trim() != Delimiter)
                throw new FormatException($"Ticket document must open with a '{Delimiter}' frontmatter delimiter.");

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var bodyStart = -1;

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.Trim() == Delimiter)
                {
                    bodyStart = i + 1;
                    break;
                }

                if (line.Trim().Length == 0)
                    continue;

                var colonIndex = line.IndexOf(':');
                if (colonIndex < 0)
                    throw new FormatException($"Invalid frontmatter line {i + 1}: '{line}'. Expected 'key: value'.");

                var key = line[..colonIndex].Trim();
                var value = line[(colonIndex + 1)..].Trim();
                fields[key] = value;
            }

            if (bodyStart < 0)
                throw new FormatException($"Ticket document frontmatter is not closed with a '{Delimiter}' delimiter.");

            var body = bodyStart < lines.Length
                ? string.Join('\n', lines[bodyStart..]).Trim('\n')
                : string.Empty;

            return new TicketDocument
            {
                Ticket = ToTicket(fields),
                Body = body
            };
        }

        public static string Serialize(Ticket ticket, string body)
        {
            ArgumentNullException.ThrowIfNull(ticket);

            var builder = new StringBuilder();
            builder.Append(Delimiter).Append('\n');

            foreach (var field in ToFields(ticket))
                builder.Append(field.Key).Append(": ").Append(field.Value).Append('\n');

            builder.Append(Delimiter).Append('\n').Append('\n');
            builder.Append((body ?? string.Empty).Replace("\r\n", "\n").Trim('\n')).Append('\n');

            return builder.ToString();
        }

        public string Serialize() => Serialize(Ticket, Body);

        static Ticket ToTicket(IDictionary<string, string> fields)
        {
            var ticket = new Ticket();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string? Take(string key)
            {
                known.Add(key);
                return fields.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
            }

            ticket.Id = Take(SheetSchema.Id) ?? throw new FormatException("Ticket document is missing the required 'id' field.");
            ticket.Title = Take(SheetSchema.Title) ?? throw new FormatException($"Ticket '{ticket.Id}' is missing the required 'title' field.");
            ticket.Type = Vocabulary.Parse<TicketType>(Take(SheetSchema.Type) ?? "feature", SheetSchema.Type);
            ticket.Area = Take(SheetSchema.Area) ?? string.Empty;
            ticket.Owner = Take(SheetSchema.Owner);

            ticket.Score = new WsjfScore
            {
                BusinessValue = ParseScore(Take(SheetSchema.Bv), SheetSchema.Bv),
                TimeCriticality = ParseScore(Take(SheetSchema.Tc), SheetSchema.Tc),
                RiskReductionOpportunityEnablement = ParseScore(Take(SheetSchema.Rroe), SheetSchema.Rroe),
                JobSize = ParseScore(Take(SheetSchema.Size), SheetSchema.Size)
            };

            ticket.Phase = ParsePhase(Take("phase"));
            ticket.State = Vocabulary.ParseOptional<WorkState>(Take(SheetSchema.State), SheetSchema.State);
            ticket.BlockedReason = Take(SheetSchema.BlockedReason);
            ticket.BlockedAt = Iso.ParseOptional(Take(SheetSchema.BlockedAt), SheetSchema.BlockedAt);
            ticket.StartedAt = Iso.ParseOptional(Take(SheetSchema.StartedAt), SheetSchema.StartedAt);
            ticket.Outcome = Vocabulary.ParseOptional<Outcome>(Take(SheetSchema.Outcome), SheetSchema.Outcome);
            ticket.ArchivedAt = Iso.ParseOptional(Take(SheetSchema.ArchivedAt), SheetSchema.ArchivedAt);

            var created = Take(SheetSchema.Created);
            ticket.Created = created is null ? default : Iso.Parse(created, SheetSchema.Created);

            var updated = Take(SheetSchema.Updated);
            ticket.Updated = updated is null ? ticket.Created : Iso.Parse(updated, SheetSchema.Updated);

            foreach (var field in fields)
            {
                if (!known.Contains(field.Key))
                    ticket.ExtraFields[field.Key] = field.Value;
            }

            return ticket;
        }

        static IEnumerable<KeyValuePair<string, string>> ToFields(Ticket ticket)
        {
            var fields = new List<KeyValuePair<string, string>>();

            void Add(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    fields.Add(new KeyValuePair<string, string>(key, value));
            }

            Add(SheetSchema.Id, ticket.Id);
            Add(SheetSchema.Title, ticket.Title);
            Add(SheetSchema.Type, Vocabulary.ToWire(ticket.Type));
            Add(SheetSchema.Area, ticket.Area);
            Add(SheetSchema.Owner, ticket.Owner);
            Add(SheetSchema.Bv, Format(ticket.Score.BusinessValue));
            Add(SheetSchema.Tc, Format(ticket.Score.TimeCriticality));
            Add(SheetSchema.Rroe, Format(ticket.Score.RiskReductionOpportunityEnablement));
            Add(SheetSchema.Size, Format(ticket.Score.JobSize));
            Add("phase", PhaseToWire(ticket.Phase));
            Add(SheetSchema.State, ticket.State.HasValue ? Vocabulary.ToWire(ticket.State.Value) : null);
            Add(SheetSchema.BlockedReason, ticket.BlockedReason);
            Add(SheetSchema.BlockedAt, Iso.ToText(ticket.BlockedAt));
            Add(SheetSchema.StartedAt, Iso.ToText(ticket.StartedAt));
            Add(SheetSchema.Outcome, ticket.Outcome.HasValue ? Vocabulary.ToWire(ticket.Outcome.Value) : null);
            Add(SheetSchema.ArchivedAt, Iso.ToText(ticket.ArchivedAt));
            Add(SheetSchema.Created, Iso.ToText(ticket.Created));
            Add(SheetSchema.Updated, Iso.ToText(ticket.Updated));

            foreach (var extra in ticket.ExtraFields)
                Add(extra.Key, extra.Value);

            return fields;
        }

        static string? Format(int? value) => value?.ToString(CultureInfo.InvariantCulture);

        static int? ParseScore(string? text, string field)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                throw new FormatException($"'{text}' in '{field}' is not a whole number.");

            // Surfaces as a malformed *document* rather than a bad argument, so doctor can report
            // the file and carry on instead of aborting the whole sweep.
            if (!WsjfScore.AllowedValues.Contains(parsed))
            {
                throw new FormatException(
                    $"'{text}' in '{field}' is off the WSJF scale. Use one of {string.Join(", ", WsjfScore.AllowedValues)}.");
            }

            return parsed;
        }

        internal static string PhaseToWire(BacklogPhase phase) => Vocabulary.ToWire(phase);

        internal static BacklogPhase ParsePhase(string? wire) =>
            string.IsNullOrWhiteSpace(wire) ? BacklogPhase.Backlog : Vocabulary.Parse<BacklogPhase>(wire.Trim(), "phase");

        public static string BuildInitialBody(Ticket ticket, string? description)
        {
            var builder = new StringBuilder();

            builder.Append("# ").Append(ticket.Id).Append(" — ").Append(ticket.Title).Append("\n\n");
            builder.Append("## Description\n\n");
            builder.Append(string.IsNullOrWhiteSpace(description) ? "_TODO_" : description.Trim()).Append("\n\n");
            builder.Append("## Acceptance Criteria\n\n- [ ] _TODO_\n\n");
            builder.Append("## Notes\n\n");
            builder.Append("## Activity Log\n\n");
            builder.Append("- ").Append(Iso.ToText(ticket.Created)).Append(" — created\n");

            return builder.ToString();
        }

        const string ActivityHeading = "## Activity Log";

        public static string AppendActivity(string body, DateTimeOffset when, string note)
        {
            var normalized = (body ?? string.Empty).Replace("\r\n", "\n").TrimEnd('\n');
            var entry = $"- {Iso.ToText(when)} — {note.Trim()}";

            if (!normalized.Contains(ActivityHeading, StringComparison.Ordinal))
                return $"{normalized}\n\n{ActivityHeading}\n\n{entry}\n";

            return $"{normalized}\n{entry}\n";
        }
    }
}

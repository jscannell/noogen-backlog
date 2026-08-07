using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // Console output, not HTML. The default encoder escapes quotes, angle brackets, and
            // ampersands into \uXXXX noise, which makes messages containing paths and examples
            // hard to read for both people and models.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static void WriteJson(object payload) => Console.WriteLine(JsonSerializer.Serialize(payload, Json));

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

        static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

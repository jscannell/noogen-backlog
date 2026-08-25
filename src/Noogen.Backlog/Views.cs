using System.Text.Json.Nodes;

namespace Noogen.Backlog
{
    /// <summary>Stable JSON projection of a ticket. Nulls are elided, so absent means absent.</summary>
    public class TicketView : IBacklogView
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

        /// <summary>
        /// This ticket as JSON, narrowed to <paramref name="fields"/>. Null keeps everything.
        ///
        /// It projects the serialised node rather than a hand-written dictionary so the shapes stay
        /// identical to an unprojected response — same casing, same null-elision, same values.
        /// Asking for a field a given ticket does not carry leaves it absent, exactly as it would
        /// be without narrowing: absent still means absent.
        /// </summary>
        public JsonNode ToNode(IReadOnlySet<string>? fields)
        {
            var node = BacklogJson.ToNode(this);

            if (fields is null)
                return node;

            var projected = node.AsObject();

            foreach (var key in projected.Select(pair => pair.Key).Where(key => !fields.Contains(key)).ToList())
                projected.Remove(key);

            return projected;
        }

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

    /// <summary>
    /// A queue: `list`, `next` and `find` all answer with an array of tickets and nothing around
    /// it. The wrapper exists so the narrowing rule is stated once rather than at each caller.
    /// </summary>
    public class TicketListView : IBacklogView
    {
        public TicketListView(IReadOnlyList<TicketView> tickets)
        {
            Tickets = tickets;
        }

        public IReadOnlyList<TicketView> Tickets { get; }

        public JsonNode ToNode(IReadOnlySet<string>? fields) =>
            new JsonArray([.. Tickets.Select(ticket => ticket.ToNode(fields))]);
    }

    /// <summary>
    /// Work in flight, with the two numbers that say whether there is room for more and which
    /// items have been out too long.
    /// </summary>
    public class WipView : IBacklogView
    {
        public int WipLimit { get; set; }

        public int InFlight { get; set; }

        /// <summary>The p85 cycle time, or 0 on a backlog with no finished work to measure.</summary>
        public double AgingThresholdDays { get; set; }

        public IReadOnlyList<TicketView> Tickets { get; set; } = [];

        public JsonNode ToNode(IReadOnlySet<string>? fields) => new JsonObject
        {
            ["wipLimit"] = WipLimit,
            ["inFlight"] = InFlight,
            ["agingThresholdDays"] = AgingThresholdDays,
            ["tickets"] = new JsonArray([.. Tickets.Select(ticket => ticket.ToNode(fields))])
        };
    }

    /// <summary>One ticket and as much of its document as the caller asked for.</summary>
    public class TicketDetailView : IBacklogView
    {
        public TicketView Ticket { get; set; } = new();

        public string Body { get; set; } = string.Empty;

        /// <summary>
        /// Narrowing is ignored here on purpose: `show` answers about one ticket that was named,
        /// so there is no list to trim, and the body is the reason the caller asked. `section` is
        /// what narrows this shape.
        /// </summary>
        public JsonNode ToNode(IReadOnlySet<string>? fields) => new JsonObject
        {
            ["ticket"] = Ticket.ToNode(null),
            ["body"] = Body
        };
    }

    /// <summary>What `doctor` found. Healthy is the whole answer when there is nothing to list.</summary>
    public class DoctorView : IBacklogView
    {
        public bool Healthy { get; set; }

        public int TicketCount { get; set; }

        public IReadOnlyList<DoctorIssue> Issues { get; set; } = [];

        public JsonNode ToNode(IReadOnlySet<string>? fields) => BacklogJson.ToNode(this);
    }

    /// <summary>How many rows `reindex` rewrote from their documents.</summary>
    public class ReindexView : IBacklogView
    {
        public int Repaired { get; set; }

        public JsonNode ToNode(IReadOnlySet<string>? fields) => BacklogJson.ToNode(this);
    }

    /// <summary>
    /// Flow metrics, wrapped so every result reaching a front end answers to one interface.
    /// </summary>
    public class FlowView : IBacklogView
    {
        public FlowView(FlowMetrics metrics)
        {
            Metrics = metrics;
        }

        public FlowMetrics Metrics { get; }

        public JsonNode ToNode(IReadOnlySet<string>? fields) => BacklogJson.ToNode(Metrics);
    }

    /// <summary>
    /// A filed ticket, and which prose sections were left saying `*TODO*`.
    ///
    /// The machine contract is the ticket alone — <see cref="ToNode"/> emits exactly what every
    /// other write emits, because that is what callers already parse. The reminder rides beside it
    /// rather than inside it: a placeholder is something to *tell somebody about*, not a field.
    /// The CLI says it on stderr because stdout under `--json` is one document; the MCP server says
    /// it in the text half of the result, where the model reads it.
    /// </summary>
    public class NewTicketView : IBacklogView
    {
        public TicketView Ticket { get; set; } = new();

        /// <summary>`description`, `acceptance criteria`, or both. Empty when the ticket arrived whole.</summary>
        public IReadOnlyList<string> MissingSections { get; set; } = [];

        /// <summary>
        /// What to tell the caller, or null when nothing was left unwritten.
        ///
        /// Filing fast is worth keeping, so a missing section is a placeholder rather than a
        /// refusal — but nothing used to say the placeholder was there, and an unwritten acceptance
        /// criterion looked exactly like a finished ticket to everybody downstream.
        /// </summary>
        public string? Reminder => MissingSections.Count == 0
            ? null
            : $"{Ticket.Id} has no {string.Join(" and no ", MissingSections)} — the section(s) say *TODO*. "
              + $"Fill them in with an edit of {Ticket.Id}.";

        public JsonNode ToNode(IReadOnlySet<string>? fields) => Ticket.ToNode(fields);
    }
}

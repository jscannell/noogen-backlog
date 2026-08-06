namespace Noogen.Backlog
{
    public class Ticket
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public TicketType Type { get; set; } = TicketType.Feature;

        public string Area { get; set; } = string.Empty;

        public string? Owner { get; set; }

        public WsjfScore Score { get; set; } = new();

        public BacklogPhase Phase { get; set; } = BacklogPhase.Backlog;

        public DateTimeOffset Created { get; set; }

        public DateTimeOffset Updated { get; set; }

        public string? DocId { get; set; }

        public string? DocUrl { get; set; }

        /// <summary>Read back from the Sheet's formula; never written by the store.</summary>
        public int? Rank { get; set; }

        // --- In Progress ---

        public WorkState? State { get; set; }

        public string? BlockedReason { get; set; }

        public DateTimeOffset? BlockedAt { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        // --- Archive ---

        public Outcome? Outcome { get; set; }

        public DateTimeOffset? ArchivedAt { get; set; }

        public double? LeadDays { get; set; }

        public double? CycleDays { get; set; }

        /// <summary>Days since work started, for the Kanban aging-WIP signal.</summary>
        public double? AgeDays(DateTimeOffset now) =>
            StartedAt.HasValue ? Math.Round((now - StartedAt.Value).TotalDays, 1) : null;

        /// <summary>
        /// Frontmatter keys the store did not recognise. Preserved so an unknown field a human
        /// added by hand survives a round-trip instead of being silently dropped.
        /// </summary>
        public IDictionary<string, string> ExtraFields { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}

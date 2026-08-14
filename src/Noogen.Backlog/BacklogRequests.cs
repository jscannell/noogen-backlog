namespace Noogen.Backlog
{
    public class NewTicket
    {
        public string Title { get; set; } = string.Empty;

        public TicketType Type { get; set; } = TicketType.Feature;

        public string Area { get; set; } = string.Empty;

        public string? Owner { get; set; }

        public WsjfScore Score { get; set; } = new();

        public string? Description { get; set; }

        /// <summary>
        /// The document's Acceptance Criteria section, or null to leave the placeholder in. It is
        /// a request field rather than something only Docs can supply because a ticket nobody has
        /// said "done" for is not a ticket anyone can pick up.
        /// </summary>
        public string? AcceptanceCriteria { get; set; }
    }

    /// <summary>
    /// Fields a plain edit may touch. Notably absent: anything that would change phase. Moving
    /// between columns goes through the lifecycle verbs so a transition is always deliberate.
    /// </summary>
    public class TicketEdit
    {
        public string? Title { get; set; }

        public string? Area { get; set; }

        public string? Owner { get; set; }

        public TicketType? Type { get; set; }

        /// <summary>
        /// New text for the document's Description section, or null to leave it alone.
        ///
        /// One of the two sections the store will rewrite, and only those two — see
        /// <see cref="TicketDocument.ReplaceSection"/>. Blank is refused rather than treated as
        /// "clear it": every scalar field here is one the Sheet also holds, so blanking one loses
        /// nothing, while blanking this one throws away writing with no CLI way back. Google Docs'
        /// own revision history is what recovers an overwrite.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// New text for the document's Acceptance Criteria section, or null to leave it alone.
        /// Blank is refused for the same reason a blank description is.
        /// </summary>
        public string? AcceptanceCriteria { get; set; }

        /// <summary>
        /// A line for the Activity Log, or null for none. Opt-in, and the caller's own words.
        ///
        /// An edit does not record itself: Docs' revision history is what recovers an overwrite,
        /// and a log entry saying "description edited" would say nothing that history does not.
        /// But when the change *is* worth recording, saying so used to mean a second command and
        /// a second round trip against Drive for the same document. This carries it in the write
        /// that is already happening.
        /// </summary>
        public string? Note { get; set; }
    }

    public class TicketFilter
    {
        public string? Area { get; set; }

        public string? Owner { get; set; }

        public int? Top { get; set; }

        public bool Matches(Ticket ticket)
        {
            if (!string.IsNullOrWhiteSpace(Area) && !string.Equals(ticket.Area, Area, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(Owner) && !string.Equals(ticket.Owner, Owner, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
    }

    /// <summary>
    /// One search hit, and where the text was found. The two sources answer different questions
    /// and fail in different ways, so which one matched is part of the answer rather than an
    /// implementation detail: a name hit is exact and current, a body hit came from an index that
    /// lags and matches whole words.
    /// </summary>
    public class TicketMatch
    {
        public Ticket Ticket { get; set; } = null!;

        /// <summary>Matched the id, title, area or owner the Sheet holds.</summary>
        public bool InName { get; set; }

        /// <summary>Matched the text of the document, via Drive's full-text index.</summary>
        public bool InBody { get; set; }

        /// <summary>Where it hit, in the order a reader should weigh them.</summary>
        public IReadOnlyList<string> Where
        {
            get
            {
                var where = new List<string>();

                if (InName)
                    where.Add("name");

                if (InBody)
                    where.Add("body");

                return where;
            }
        }
    }

    public class DoctorIssue
    {
        public string Id { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string Detail { get; set; } = string.Empty;
    }

    public class DoctorReport
    {
        public IList<DoctorIssue> Issues { get; } = [];

        public int TicketCount { get; set; }

        public bool IsHealthy => Issues.Count == 0;

        public void Add(string id, string kind, string detail) =>
            Issues.Add(new DoctorIssue { Id = id, Kind = kind, Detail = detail });
    }

    /// <summary>Raised when a caller asks for a transition the lifecycle does not permit.</summary>
    public class BacklogTransitionException : InvalidOperationException
    {
        public BacklogTransitionException(string message) : base(message)
        {
        }
    }

    /// <summary>Raised when starting work would breach the Kanban WIP limit.</summary>
    public class WipLimitExceededException : InvalidOperationException
    {
        public WipLimitExceededException(string message, int limit, IReadOnlyList<Ticket> inFlight) : base(message)
        {
            Limit = limit;
            InFlight = inFlight;
        }

        public int Limit { get; }

        public IReadOnlyList<Ticket> InFlight { get; }
    }
}

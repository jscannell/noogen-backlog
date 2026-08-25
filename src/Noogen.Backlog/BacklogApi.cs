namespace Noogen.Backlog
{
    /// <summary>
    /// Every verb, as one call each, answering with the shapes a front end puts on the wire.
    ///
    /// <see cref="IBacklogStore"/> is the storage seam; this is the seam above it, and it exists
    /// because there is more than one front end. Three verbs compose more than one store call —
    /// <c>wip</c> needs the flow percentiles and the settings to say what is aging, <c>show</c>
    /// needs the document and then a decision about how much of it to return, and <c>new</c> has to
    /// report which sections it left as placeholders. Left in a front end, those would be written
    /// twice and would diverge quietly: both callers would still answer, just not the same way.
    ///
    /// What is *not* here is rendering, and nothing here knows about a transport. A front end
    /// decides between a table and JSON, how to say a failure, and what a request looked like on
    /// the way in; this decides what the answer is. Every method takes a
    /// <see cref="CancellationToken"/> and touches no console, no configuration file and no
    /// ambient clock, so a host that wants to serve these over HTTP has nothing to unpick first.
    /// </summary>
    public class BacklogApi
    {
        readonly IBacklogStore _store;
        readonly Func<DateTimeOffset> _clock;

        public BacklogApi(IBacklogStore store, Func<DateTimeOffset>? clock = null)
        {
            _store = store;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public IBacklogStore Store => _store;

        DateTimeOffset Now => _clock();

        public Task<BacklogSettings> SettingsAsync(CancellationToken cancellationToken = default) =>
            _store.GetSettingsAsync(cancellationToken);

        // --- queries ---

        public async Task<TicketListView> ListAsync(TicketFilter filter, CancellationToken cancellationToken = default)
        {
            var tickets = await _store.ListAsync(filter, cancellationToken);
            return new TicketListView([.. tickets.Select(ticket => TicketView.From(ticket))]);
        }

        /// <summary>
        /// The queue capped at one. Same call as <see cref="ListAsync"/> — the difference is the
        /// question, and answering "what should I work on?" with the whole queue is what makes a
        /// cheap read expensive.
        /// </summary>
        public Task<TicketListView> NextAsync(TicketFilter filter, CancellationToken cancellationToken = default)
        {
            filter.Top ??= 1;
            return ListAsync(filter, cancellationToken);
        }

        public async Task<WipView> WipAsync(TicketFilter filter, CancellationToken cancellationToken = default)
        {
            var now = Now;

            var tickets = await _store.WipAsync(filter, cancellationToken);
            var flow = await _store.FlowAsync(null, cancellationToken);
            var settings = await _store.GetSettingsAsync(cancellationToken);

            var threshold = flow.CycleTimeP85;

            return new WipView
            {
                WipLimit = settings.WipLimit,
                InFlight = tickets.Count,
                AgingThresholdDays = threshold ?? 0,
                Tickets = [.. tickets.Select(ticket => TicketView.From(ticket, now, threshold))]
            };
        }

        /// <summary>
        /// The only verb that reads a ticket's prose without being told which ticket. It spans all
        /// three tabs deliberately — "have we discussed this before?" is most often answered by
        /// something already archived, and every other query verb is scoped to one column.
        /// </summary>
        public async Task<TicketListView> FindAsync(string text, TicketFilter filter, CancellationToken cancellationToken = default)
        {
            var now = Now;
            var matches = await _store.SearchAsync(text, filter, cancellationToken);

            return new TicketListView([.. matches.Select(match => TicketView.From(match, now))]);
        }

        public async Task<FlowView> FlowAsync(DateTimeOffset? since, CancellationToken cancellationToken = default) =>
            new(await _store.FlowAsync(since, cancellationToken));

        public async Task<TicketDetailView> ShowAsync(
            string id,
            string? section = null,
            bool full = false,
            CancellationToken cancellationToken = default)
        {
            var ticket = await _store.GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"No ticket '{id}'.");
            var body = await _store.GetBodyAsync(id, cancellationToken);

            return new TicketDetailView
            {
                Ticket = TicketView.From(ticket, Now),
                Body = Narrow(body, section, full)
            };
        }

        /// <summary>How many Activity Log entries <c>show</c> keeps unless asked for all of them.</summary>
        public const int ActivityLogEntriesShown = 3;

        /// <summary>
        /// What of the body a read returns. Display only — this never touches what is stored, and
        /// nothing here may be handed to a write. See <see cref="TicketDocument.TrimActivityLog"/>.
        ///
        /// The default trims the Activity Log because it is the part that grows without bound:
        /// every lifecycle event appends a line, so on a ticket that has been worked it is most of
        /// the document, and it is rarely what the reader came for. The recent entries are, so
        /// those are the ones kept.
        ///
        /// <c>section</c> narrows to one heading instead, which is what a read-before-write wants:
        /// a prose option replaces a whole section, so the caller needs that section and nothing
        /// else. Asking for the log by name gives it whole — trimming what was explicitly requested
        /// would be answering a different question.
        /// </summary>
        static string Narrow(string body, string? section, bool full)
        {
            if (!string.IsNullOrWhiteSpace(section))
            {
                var heading = section.Replace('-', ' ').Trim();

                return TicketDocument.SectionOf(body, heading)
                    ?? throw new UsageException(
                        $"This ticket has no '{heading}' section. It has: "
                        + $"{string.Join(", ", TicketDocument.HeadingsOf(body))}.");
            }

            return full ? body : TicketDocument.TrimActivityLog(body, ActivityLogEntriesShown);
        }

        // --- capture and edit ---

        public async Task<NewTicketView> CreateAsync(NewTicket request, CancellationToken cancellationToken = default)
        {
            var ticket = await _store.CreateAsync(request, cancellationToken);

            var missing = new List<string>();

            if (request.Description is null)
                missing.Add("description");

            if (request.AcceptanceCriteria is null)
                missing.Add("acceptance criteria");

            return new NewTicketView
            {
                Ticket = TicketView.From(ticket, Now),
                MissingSections = missing
            };
        }

        public async Task<TicketView> UpdateAsync(string id, TicketEdit edit, CancellationToken cancellationToken = default) =>
            TicketView.From(await _store.UpdateAsync(id, edit, cancellationToken), Now);

        public async Task<TicketView> ScoreAsync(string id, WsjfScore score, CancellationToken cancellationToken = default)
        {
            if (!score.BusinessValue.HasValue && !score.TimeCriticality.HasValue
                && !score.RiskReductionOpportunityEnablement.HasValue && !score.JobSize.HasValue)
            {
                throw new UsageException("Pass at least one of bv, tc, rroe, size.");
            }

            return TicketView.From(await _store.ScoreAsync(id, score, cancellationToken), Now);
        }

        public async Task<TicketView> NoteAsync(string id, string text, CancellationToken cancellationToken = default) =>
            TicketView.From(await _store.AppendNoteAsync(id, text, cancellationToken), Now);

        // --- lifecycle ---

        public async Task<TicketView> StartAsync(string id, string? owner, bool force, CancellationToken cancellationToken = default) =>
            TicketView.From(await _store.StartAsync(id, owner, force, cancellationToken), Now);

        public async Task<TicketView> SetStateAsync(string id, WorkState state, string? blockedReason, CancellationToken cancellationToken = default) =>
            TicketView.From(await _store.SetStateAsync(id, state, blockedReason, cancellationToken), Now);

        public async Task<TicketView> ArchiveAsync(string id, Outcome outcome, string? note, CancellationToken cancellationToken = default) =>
            TicketView.From(await _store.ArchiveAsync(id, outcome, note, cancellationToken), Now);

        public async Task<TicketView> RestoreAsync(string id, CancellationToken cancellationToken = default) =>
            TicketView.From(await _store.RestoreAsync(id, cancellationToken), Now);

        // --- maintenance ---

        public async Task<DoctorView> DoctorAsync(CancellationToken cancellationToken = default)
        {
            var report = await _store.DoctorAsync(cancellationToken);

            return new DoctorView
            {
                Healthy = report.IsHealthy,
                TicketCount = report.TicketCount,
                Issues = [.. report.Issues]
            };
        }

        public async Task<ReindexView> ReindexAsync(CancellationToken cancellationToken = default) =>
            new() { Repaired = await _store.ReindexAsync(cancellationToken) };
    }
}

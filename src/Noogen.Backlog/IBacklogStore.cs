namespace Noogen.Backlog
{
    /// <summary>
    /// Every backlog operation. The CLI is a thin shell over this interface, which is also the
    /// seam the Noogen agent will consume later — adding [AgentTool] wrappers rather than
    /// reimplementing any of it.
    /// </summary>
    public interface IBacklogStore
    {
        /// <summary>The WSJF queue: unstarted work in rank order.</summary>
        Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, CancellationToken cancellationToken = default);

        /// <summary>Work in flight, oldest first, so aging items surface at the top.</summary>
        Task<IReadOnlyList<Ticket>> WipAsync(TicketFilter filter, CancellationToken cancellationToken = default);

        Task<FlowMetrics> FlowAsync(DateTimeOffset? since, CancellationToken cancellationToken = default);

        Task<Ticket?> GetAsync(string id, CancellationToken cancellationToken = default);

        Task<string> GetBodyAsync(string id, CancellationToken cancellationToken = default);

        Task<Ticket> CreateAsync(NewTicket request, CancellationToken cancellationToken = default);

        Task<Ticket> UpdateAsync(string id, TicketEdit edit, CancellationToken cancellationToken = default);

        Task<Ticket> ScoreAsync(string id, WsjfScore score, CancellationToken cancellationToken = default);

        Task<Ticket> AppendNoteAsync(string id, string note, CancellationToken cancellationToken = default);

        Task<Ticket> StartAsync(string id, string? owner, bool force, CancellationToken cancellationToken = default);

        Task<Ticket> SetStateAsync(string id, WorkState state, string? blockedReason, CancellationToken cancellationToken = default);

        Task<Ticket> ArchiveAsync(string id, Outcome outcome, string? note, CancellationToken cancellationToken = default);

        Task<Ticket> RestoreAsync(string id, CancellationToken cancellationToken = default);

        Task<DoctorReport> DoctorAsync(CancellationToken cancellationToken = default);

        Task<int> ReindexAsync(CancellationToken cancellationToken = default);

        Task<BacklogSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    }
}

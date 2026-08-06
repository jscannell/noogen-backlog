namespace Noogen.Backlog
{
    /// <summary>
    /// The three Kanban columns. The tab a ticket lives on <em>is</em> its state, so everything
    /// downstream — whether WSJF ranks it, whether its scores are live formulas or frozen
    /// values, which transitions are legal — is a property of the phase rather than a string
    /// test repeated at every call site.
    /// </summary>
    public enum BacklogPhase
    {
        Backlog,
        InProgress,
        Archive
    }

    public static class BacklogPhaseExtensions
    {
        public static readonly IReadOnlyList<BacklogPhase> All =
        [
            BacklogPhase.Backlog,
            BacklogPhase.InProgress,
            BacklogPhase.Archive
        ];

        public static string TabName(this BacklogPhase phase)
        {
            switch (phase)
            {
                case BacklogPhase.Backlog:
                    return "Backlog";
                case BacklogPhase.InProgress:
                    return "In Progress";
                case BacklogPhase.Archive:
                    return "Archive";
                default:
                    throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown backlog phase.");
            }
        }

        public static BacklogPhase FromTabName(string tabName)
        {
            foreach (var phase in All)
            {
                if (string.Equals(phase.TabName(), tabName, StringComparison.OrdinalIgnoreCase))
                    return phase;
            }

            throw new ArgumentException($"'{tabName}' is not a backlog lifecycle tab.", nameof(tabName));
        }

        /// <summary>WSJF sequences what to start, so only unstarted work competes for rank.</summary>
        public static bool IsRanked(this BacklogPhase phase) => phase == BacklogPhase.Backlog;

        /// <summary>
        /// Only the Backlog tab carries live cod/wsjf/rank formulas. Once work starts, the scores
        /// freeze as static values: a historical record for calibrating estimates, never recomputed.
        /// </summary>
        public static bool UsesLiveFormulas(this BacklogPhase phase) => phase == BacklogPhase.Backlog;

        public static bool CanTransitionTo(this BacklogPhase from, BacklogPhase to)
        {
            if (from == to)
                return false;

            switch (from)
            {
                case BacklogPhase.Backlog:
                    // Straight to Archive covers cancelled/duplicate without pretending work happened.
                    return to == BacklogPhase.InProgress || to == BacklogPhase.Archive;
                case BacklogPhase.InProgress:
                    // Back to Backlog is "stop work and re-queue", not a rollback of history.
                    return to == BacklogPhase.Archive || to == BacklogPhase.Backlog;
                case BacklogPhase.Archive:
                    return to == BacklogPhase.Backlog;
                default:
                    return false;
            }
        }
    }
}

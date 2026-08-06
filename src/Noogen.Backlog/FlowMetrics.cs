namespace Noogen.Backlog
{
    /// <summary>
    /// The Kanban feedback loop. Lead time is created → archived; cycle time is started →
    /// archived. Both are frozen onto the Archive row when work finishes, so this is a pure
    /// aggregation over rows that already carry their numbers.
    /// </summary>
    public class FlowMetrics
    {
        public DateTimeOffset? Since { get; set; }

        public int Throughput { get; set; }

        public double? CycleTimeP50 { get; set; }

        public double? CycleTimeP85 { get; set; }

        public double? LeadTimeP50 { get; set; }

        public double? LeadTimeP85 { get; set; }

        public static FlowMetrics From(IEnumerable<Ticket> archived, DateTimeOffset? since)
        {
            var considered = archived
                .Where(ticket => ticket.Outcome == Outcome.Done)
                .Where(ticket => !since.HasValue || (ticket.ArchivedAt.HasValue && ticket.ArchivedAt.Value >= since.Value))
                .ToList();

            var cycle = considered.Where(t => t.CycleDays.HasValue).Select(t => t.CycleDays!.Value).OrderBy(v => v).ToList();
            var lead = considered.Where(t => t.LeadDays.HasValue).Select(t => t.LeadDays!.Value).OrderBy(v => v).ToList();

            return new FlowMetrics
            {
                Since = since,
                Throughput = considered.Count,
                CycleTimeP50 = Percentile(cycle, 0.50),
                CycleTimeP85 = Percentile(cycle, 0.85),
                LeadTimeP50 = Percentile(lead, 0.50),
                LeadTimeP85 = Percentile(lead, 0.85)
            };
        }

        /// <summary>
        /// Nearest-rank percentile over an ascending list. Returns null for an empty sample
        /// rather than throwing — a brand-new backlog has no history and that is not an error.
        /// </summary>
        internal static double? Percentile(IReadOnlyList<double> ascending, double percentile)
        {
            if (ascending.Count == 0)
                return null;

            var rank = (int)Math.Ceiling(percentile * ascending.Count);
            var index = Math.Clamp(rank - 1, 0, ascending.Count - 1);

            return ascending[index];
        }

        public static double Days(DateTimeOffset from, DateTimeOffset to) =>
            Math.Round(Math.Max(0, (to - from).TotalDays), 2);
    }
}

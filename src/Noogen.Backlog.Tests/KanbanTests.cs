namespace Noogen.Backlog.Tests
{
    public class WipLimitTests
    {
        [Fact]
        public async Task Refuses_to_start_past_the_limit_and_names_what_is_in_flight()
        {
            var backlog = await TestBacklog.CreateAsync(wipLimit: 1);

            var first = await backlog.AddAsync("First");
            var second = await backlog.AddAsync("Second");

            await backlog.Store.StartAsync(first.Id, "jason", false);

            var exception = await Assert.ThrowsAsync<WipLimitExceededException>(
                () => backlog.Store.StartAsync(second.Id, "jason", false));

            Assert.Equal(1, exception.Limit);
            Assert.Contains(first.Id, exception.Message);
            Assert.Contains("--force", exception.Message);

            // The refused item stayed put rather than half-moving.
            Assert.Equal(1, backlog.RowCount(BacklogPhase.Backlog));
            Assert.Equal(1, backlog.RowCount(BacklogPhase.InProgress));
        }

        [Fact]
        public async Task Force_overrides_the_limit_and_records_that_it_did()
        {
            var backlog = await TestBacklog.CreateAsync(wipLimit: 1);

            var first = await backlog.AddAsync("First");
            var second = await backlog.AddAsync("Second");

            await backlog.Store.StartAsync(first.Id, "jason", false);
            await backlog.Store.StartAsync(second.Id, "jason", true);

            Assert.Equal(2, backlog.RowCount(BacklogPhase.InProgress));

            var document = backlog.Drive.ContentOf(second.DocId!);
            Assert.Contains("--force", document);
        }

        [Fact]
        public async Task Starting_up_to_the_limit_is_fine()
        {
            var backlog = await TestBacklog.CreateAsync(wipLimit: 2);

            var first = await backlog.AddAsync("First");
            var second = await backlog.AddAsync("Second");

            await backlog.Store.StartAsync(first.Id, "jason", false);
            await backlog.Store.StartAsync(second.Id, "jason", false);

            Assert.Equal(2, backlog.RowCount(BacklogPhase.InProgress));
        }

        [Fact]
        public async Task Finishing_something_frees_a_slot()
        {
            var backlog = await TestBacklog.CreateAsync(wipLimit: 1);

            var first = await backlog.AddAsync("First");
            var second = await backlog.AddAsync("Second");

            await backlog.Store.StartAsync(first.Id, "jason", false);
            await backlog.Store.ArchiveAsync(first.Id, Outcome.Done, null);
            await backlog.Store.StartAsync(second.Id, "jason", false);

            Assert.Equal(1, backlog.RowCount(BacklogPhase.InProgress));
        }
    }

    public class FlowMetricsTests
    {
        static Ticket Archived(double cycleDays, double leadDays, DateTimeOffset archivedAt, Outcome outcome = Outcome.Done) => new()
        {
            Id = "NG-0001",
            Phase = BacklogPhase.Archive,
            Outcome = outcome,
            ArchivedAt = archivedAt,
            CycleDays = cycleDays,
            LeadDays = leadDays
        };

        [Fact]
        public void An_empty_archive_reports_zero_throughput_and_null_percentiles()
        {
            var metrics = FlowMetrics.From([], null);

            Assert.Equal(0, metrics.Throughput);
            Assert.Null(metrics.CycleTimeP50);
            Assert.Null(metrics.CycleTimeP85);
        }

        [Fact]
        public void A_three_item_sample_does_not_throw()
        {
            var now = DateTimeOffset.UtcNow;
            var metrics = FlowMetrics.From(
                [Archived(1, 2, now), Archived(3, 4, now), Archived(5, 6, now)],
                null);

            Assert.Equal(3, metrics.Throughput);
            Assert.Equal(3, metrics.CycleTimeP50);
            Assert.Equal(5, metrics.CycleTimeP85);
        }

        [Fact]
        public void A_single_item_reports_itself_for_every_percentile()
        {
            var metrics = FlowMetrics.From([Archived(4, 9, DateTimeOffset.UtcNow)], null);

            Assert.Equal(4, metrics.CycleTimeP50);
            Assert.Equal(4, metrics.CycleTimeP85);
            Assert.Equal(9, metrics.LeadTimeP50);
        }

        [Fact]
        public void Only_completed_work_counts_toward_throughput()
        {
            var now = DateTimeOffset.UtcNow;
            var metrics = FlowMetrics.From(
                [
                    Archived(1, 1, now),
                    Archived(2, 2, now, Outcome.Cancelled),
                    Archived(3, 3, now, Outcome.Duplicate)
                ],
                null);

            Assert.Equal(1, metrics.Throughput);
        }

        [Fact]
        public void Honours_the_since_window()
        {
            var now = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
            var metrics = FlowMetrics.From(
                [
                    Archived(1, 1, now.AddDays(-5)),
                    Archived(9, 9, now.AddDays(-200))
                ],
                now.AddDays(-30));

            Assert.Equal(1, metrics.Throughput);
            Assert.Equal(1, metrics.CycleTimeP50);
        }

        [Theory]
        [InlineData(0.5, 3)]
        [InlineData(0.85, 5)]
        [InlineData(1.0, 5)]
        public void Uses_nearest_rank(double percentile, double expected) =>
            Assert.Equal(expected, FlowMetrics.Percentile([1, 2, 3, 4, 5], percentile));

        [Fact]
        public void Percentile_of_an_empty_sample_is_null() =>
            Assert.Null(FlowMetrics.Percentile([], 0.5));

        [Fact]
        public async Task Flow_reads_the_frozen_numbers_off_the_archive_tab()
        {
            var backlog = await TestBacklog.CreateAsync();

            var first = await backlog.AddAsync("First");
            await backlog.Store.StartAsync(first.Id, "jason", false);
            backlog.Clock.Advance(TimeSpan.FromDays(2));
            await backlog.Store.ArchiveAsync(first.Id, Outcome.Done, null);

            var metrics = await backlog.Store.FlowAsync(null);

            Assert.Equal(1, metrics.Throughput);
            Assert.Equal(2, metrics.CycleTimeP50);
        }
    }

    public class AgingTests
    {
        [Fact]
        public async Task Wip_is_ordered_oldest_first_so_stuck_work_surfaces()
        {
            var backlog = await TestBacklog.CreateAsync(wipLimit: 10);

            var old = await backlog.AddAsync("Old");
            await backlog.Store.StartAsync(old.Id, "jason", false);

            backlog.Clock.Advance(TimeSpan.FromDays(10));

            var fresh = await backlog.AddAsync("Fresh");
            await backlog.Store.StartAsync(fresh.Id, "jason", false);

            var wip = await backlog.Store.WipAsync(new TicketFilter());

            Assert.Equal(["Old", "Fresh"], wip.Select(ticket => ticket.Title));
        }

        [Fact]
        public void Age_is_measured_from_the_start_of_work()
        {
            var now = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
            var ticket = new Ticket { StartedAt = now.AddDays(-4.5) };

            Assert.Equal(4.5, ticket.AgeDays(now));
        }

        [Fact]
        public void Unstarted_work_has_no_age() =>
            Assert.Null(new Ticket().AgeDays(DateTimeOffset.UtcNow));
    }
}

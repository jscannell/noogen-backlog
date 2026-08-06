namespace Noogen.Backlog.Tests
{
    public class BacklogPhaseTests
    {
        [Theory]
        [InlineData(BacklogPhase.Backlog, BacklogPhase.InProgress, true)]
        [InlineData(BacklogPhase.Backlog, BacklogPhase.Archive, true)]
        [InlineData(BacklogPhase.InProgress, BacklogPhase.Archive, true)]
        [InlineData(BacklogPhase.InProgress, BacklogPhase.Backlog, true)]
        [InlineData(BacklogPhase.Archive, BacklogPhase.Backlog, true)]
        [InlineData(BacklogPhase.Archive, BacklogPhase.InProgress, false)]
        [InlineData(BacklogPhase.Backlog, BacklogPhase.Backlog, false)]
        [InlineData(BacklogPhase.Archive, BacklogPhase.Archive, false)]
        public void Enforces_the_transition_table(BacklogPhase from, BacklogPhase to, bool allowed) =>
            Assert.Equal(allowed, from.CanTransitionTo(to));

        [Fact]
        public void Only_the_backlog_is_ranked_and_carries_live_formulas()
        {
            Assert.True(BacklogPhase.Backlog.IsRanked());
            Assert.False(BacklogPhase.InProgress.IsRanked());
            Assert.False(BacklogPhase.Archive.IsRanked());

            Assert.True(BacklogPhase.Backlog.UsesLiveFormulas());
            Assert.False(BacklogPhase.InProgress.UsesLiveFormulas());
            Assert.False(BacklogPhase.Archive.UsesLiveFormulas());
        }

        [Fact]
        public void Tab_names_round_trip() =>
            Assert.All(BacklogPhaseExtensions.All, phase =>
                Assert.Equal(phase, BacklogPhaseExtensions.FromTabName(phase.TabName())));
    }

    public class BacklogStoreTests
    {
        [Fact]
        public async Task Creates_a_ticket_with_a_document_and_an_index_row()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("WSJF index tool");

            Assert.Equal("NG-0001", ticket.Id);
            Assert.Equal(BacklogPhase.Backlog, ticket.Phase);
            Assert.NotNull(ticket.DocId);
            Assert.Equal(1, backlog.RowCount(BacklogPhase.Backlog));

            var document = TicketDocument.Parse(backlog.Drive.ContentOf(ticket.DocId!));
            Assert.Equal("WSJF index tool", document.Ticket.Title);
            Assert.Contains("## Acceptance Criteria", document.Body);
        }

        [Fact]
        public async Task Allocates_ids_as_max_plus_one_across_every_tab()
        {
            var backlog = await TestBacklog.CreateAsync();

            var first = await backlog.AddAsync("One");
            var second = await backlog.AddAsync("Two");
            await backlog.Store.StartAsync(second.Id, "jason", false);
            await backlog.Store.ArchiveAsync(second.Id, Outcome.Done, null);

            // NG-0002 now lives on Archive; the next id must still be 3, not 2.
            var third = await backlog.AddAsync("Three");

            Assert.Equal("NG-0001", first.Id);
            Assert.Equal("NG-0002", second.Id);
            Assert.Equal("NG-0003", third.Id);
        }

        [Fact]
        public async Task Backlog_rows_carry_formulas_and_started_rows_carry_frozen_values()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something", bv: 8, tc: 3, rroe: 2, size: 5);

            Assert.StartsWith("=", backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.Wsjf));
            Assert.StartsWith("=", backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.Rank));

            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            // Frozen: a plain number, no formula, and no rank column at all on this tab.
            Assert.Equal("2.6", backlog.CellText(BacklogPhase.InProgress, ticket.Id, SheetSchema.Wsjf));
            Assert.Equal("13", backlog.CellText(BacklogPhase.InProgress, ticket.Id, SheetSchema.Cod));
            Assert.DoesNotContain(SheetSchema.Rank, SheetSchema.Columns(BacklogPhase.InProgress));
        }

        [Fact]
        public async Task Title_cell_keeps_its_plain_text_and_gains_a_link()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("WSJF index tool");

            // The stored value is the title, not a HYPERLINK() formula — that is the whole point
            // of using a rich-text run.
            Assert.Equal("WSJF index tool", backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.Title));
            Assert.Contains(backlog.Sheets.Links, link => link.Value.Contains(ticket.DocId!));
        }

        [Fact]
        public async Task Ranks_scored_items_by_wsjf_and_sorts_unscored_last()
        {
            var backlog = await TestBacklog.CreateAsync();

            await backlog.AddAsync("Low", bv: 1, tc: 1, rroe: 1, size: 13);      // 0.23
            await backlog.AddAsync("High", bv: 13, tc: 8, rroe: 5, size: 2);     // 13
            await backlog.AddAsync("Unscored", bv: null, tc: null, rroe: null, size: null);
            await backlog.AddAsync("Middle", bv: 8, tc: 3, rroe: 2, size: 5);    // 2.6

            var queue = await backlog.Store.ListAsync(new TicketFilter());

            Assert.Equal(["High", "Middle", "Low", "Unscored"], queue.Select(t => t.Title));
        }

        [Fact]
        public async Task Starting_work_moves_the_row_but_leaves_the_document_in_place()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");
            var ticketsFolder = backlog.Init.TicketsFolderId;

            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            Assert.Equal(0, backlog.RowCount(BacklogPhase.Backlog));
            Assert.Equal(1, backlog.RowCount(BacklogPhase.InProgress));
            Assert.Equal(ticketsFolder, backlog.Drive.ParentOf(ticket.DocId!));

            var reloaded = await backlog.Store.GetAsync(ticket.Id);
            Assert.Equal(BacklogPhase.InProgress, reloaded!.Phase);
            Assert.Equal(WorkState.InProgress, reloaded.State);
            Assert.NotNull(reloaded.StartedAt);
        }

        [Fact]
        public async Task Started_items_leave_the_queue_and_appear_in_wip()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            Assert.Empty(await backlog.Store.ListAsync(new TicketFilter()));
            Assert.Single(await backlog.Store.WipAsync(new TicketFilter()));
        }

        [Fact]
        public async Task Scoring_a_started_item_is_refused_with_the_reason()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");
            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            var exception = await Assert.ThrowsAsync<BacklogTransitionException>(
                () => backlog.Store.ScoreAsync(ticket.Id, new WsjfScore { BusinessValue = 20 }));

            Assert.Contains("no longer subject to WSJF", exception.Message);
        }

        [Fact]
        public async Task Blocking_records_a_reason_and_a_timestamp_then_clears_on_unblock()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");
            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            await backlog.Store.SetStateAsync(ticket.Id, WorkState.Blocked, "waiting on Drive API quota");
            var blocked = await backlog.Store.GetAsync(ticket.Id);

            Assert.Equal(WorkState.Blocked, blocked!.State);
            Assert.Equal("waiting on Drive API quota", blocked.BlockedReason);
            Assert.NotNull(blocked.BlockedAt);

            await backlog.Store.SetStateAsync(ticket.Id, WorkState.InProgress, null);
            var unblocked = await backlog.Store.GetAsync(ticket.Id);

            Assert.Equal(WorkState.InProgress, unblocked!.State);
            Assert.Null(unblocked.BlockedReason);
            Assert.Null(unblocked.BlockedAt);
        }

        [Fact]
        public async Task Blocking_without_a_reason_is_refused()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");
            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            await Assert.ThrowsAsync<ArgumentException>(
                () => backlog.Store.SetStateAsync(ticket.Id, WorkState.Blocked, "  "));
        }

        [Fact]
        public async Task Work_state_cannot_be_set_on_an_unstarted_item()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            var exception = await Assert.ThrowsAsync<BacklogTransitionException>(
                () => backlog.Store.SetStateAsync(ticket.Id, WorkState.InReview, null));

            Assert.Contains("backlog start", exception.Message);
        }
    }

    public class ArchiveTests
    {
        [Fact]
        public async Task Archiving_moves_the_row_and_the_document_but_deletes_nothing()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");
            var filesBefore = backlog.Drive.FileCount;

            await backlog.Store.StartAsync(ticket.Id, "jason", false);
            backlog.Clock.Advance(TimeSpan.FromDays(3));
            await backlog.Store.ArchiveAsync(ticket.Id, Outcome.Done, "shipped");

            Assert.Equal(0, backlog.RowCount(BacklogPhase.InProgress));
            Assert.Equal(1, backlog.RowCount(BacklogPhase.Archive));

            // The archive-not-delete guarantee: the file still exists, it just lives elsewhere.
            Assert.True(backlog.Drive.Exists(ticket.DocId!));
            Assert.NotEqual(backlog.Init.TicketsFolderId, backlog.Drive.ParentOf(ticket.DocId!));
            Assert.True(backlog.Drive.FileCount >= filesBefore);
        }

        [Fact]
        public async Task Freezes_lead_and_cycle_time_at_archive_time()
        {
            var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
            var backlog = await TestBacklog.CreateAsync(start);

            var ticket = await backlog.AddAsync("Something");
            backlog.Clock.Advance(TimeSpan.FromDays(2));
            await backlog.Store.StartAsync(ticket.Id, "jason", false);
            backlog.Clock.Advance(TimeSpan.FromDays(3));
            var archived = await backlog.Store.ArchiveAsync(ticket.Id, Outcome.Done, null);

            Assert.Equal(5, archived.LeadDays);    // created -> archived
            Assert.Equal(3, archived.CycleDays);   // started -> archived
        }

        [Fact]
        public async Task Same_day_work_yields_zero_not_a_negative_number()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            await backlog.Store.StartAsync(ticket.Id, "jason", false);
            var archived = await backlog.Store.ArchiveAsync(ticket.Id, Outcome.Done, null);

            Assert.Equal(0, archived.CycleDays);
            Assert.Equal(0, archived.LeadDays);
        }

        [Fact]
        public async Task Cancelling_straight_from_the_backlog_is_allowed_and_has_no_cycle_time()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Never starting this");

            var archived = await backlog.Store.ArchiveAsync(ticket.Id, Outcome.Cancelled, "out of scope");

            Assert.Equal(Outcome.Cancelled, archived.Outcome);
            Assert.Null(archived.CycleDays);
            Assert.NotNull(archived.LeadDays);
        }

        [Fact]
        public async Task Archiving_twice_is_refused()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");
            await backlog.Store.ArchiveAsync(ticket.Id, Outcome.Done, null);

            await Assert.ThrowsAsync<BacklogTransitionException>(
                () => backlog.Store.ArchiveAsync(ticket.Id, Outcome.Done, null));
        }

        [Fact]
        public async Task Restore_returns_it_to_the_queue_with_live_formulas()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            await backlog.Store.StartAsync(ticket.Id, "jason", false);
            await backlog.Store.ArchiveAsync(ticket.Id, Outcome.Done, null);
            var restored = await backlog.Store.RestoreAsync(ticket.Id);

            Assert.Equal(BacklogPhase.Backlog, restored.Phase);
            Assert.Null(restored.Outcome);
            Assert.Null(restored.ArchivedAt);
            Assert.Equal(1, backlog.RowCount(BacklogPhase.Backlog));
            Assert.Equal(0, backlog.RowCount(BacklogPhase.Archive));

            Assert.StartsWith("=", backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.Wsjf));
            Assert.Equal(backlog.Init.TicketsFolderId, backlog.Drive.ParentOf(ticket.DocId!));
        }

        [Fact]
        public async Task Restoring_something_that_is_not_archived_is_refused()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            await Assert.ThrowsAsync<BacklogTransitionException>(() => backlog.Store.RestoreAsync(ticket.Id));
        }
    }

    public class MoveOrderingTests
    {
        [Fact]
        public async Task An_interrupted_move_duplicates_the_ticket_rather_than_losing_it()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            // Fail between the append and the delete — the exact window the ordering guards.
            backlog.Sheets.FailNextDeleteRow = true;
            await Assert.ThrowsAsync<IOException>(() => backlog.Store.StartAsync(ticket.Id, "jason", false));

            // Appended to the destination, never removed from the source: two rows, zero losses.
            Assert.Equal(1, backlog.RowCount(BacklogPhase.Backlog));
            Assert.Equal(1, backlog.RowCount(BacklogPhase.InProgress));
        }

        [Fact]
        public async Task Doctor_reports_the_duplicate_an_interrupted_move_leaves_behind()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            backlog.Sheets.FailNextDeleteRow = true;
            await Assert.ThrowsAsync<IOException>(() => backlog.Store.StartAsync(ticket.Id, "jason", false));

            var report = await backlog.Store.DoctorAsync();

            Assert.False(report.IsHealthy);
            Assert.Contains(report.Issues, issue => issue.Kind == "duplicate" && issue.Id == ticket.Id);
        }

        [Fact]
        public async Task Appends_before_it_deletes()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            var appendsBefore = backlog.Sheets.AppendRowCallCount;
            var deletesBefore = backlog.Sheets.DeleteRowCallCount;

            backlog.Sheets.FailNextDeleteRow = true;
            await Assert.ThrowsAsync<IOException>(() => backlog.Store.StartAsync(ticket.Id, "jason", false));

            // The append happened; the delete was attempted after it and failed.
            Assert.True(backlog.Sheets.AppendRowCallCount > appendsBefore);
            Assert.True(backlog.Sheets.DeleteRowCallCount > deletesBefore);
        }
    }
}

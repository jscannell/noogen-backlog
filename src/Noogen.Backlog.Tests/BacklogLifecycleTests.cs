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
        public void CanTransitionTo_EveryPairOfPhases_FollowsTheTransitionTable(BacklogPhase from, BacklogPhase to, bool allowed) =>
            Assert.Equal(allowed, from.CanTransitionTo(to));

        [Fact]
        public void IsRanked_EveryPhase_IsTrueOnlyForTheBacklog()
        {
            Assert.True(BacklogPhase.Backlog.IsRanked());
            Assert.False(BacklogPhase.InProgress.IsRanked());
            Assert.False(BacklogPhase.Archive.IsRanked());

            Assert.True(BacklogPhase.Backlog.UsesLiveFormulas());
            Assert.False(BacklogPhase.InProgress.UsesLiveFormulas());
            Assert.False(BacklogPhase.Archive.UsesLiveFormulas());
        }

        [Fact]
        public void FromTabName_NameProducedByTabName_ReturnsTheSamePhase() =>
            Assert.All(BacklogPhaseExtensions.All, phase =>
                Assert.Equal(phase, BacklogPhaseExtensions.FromTabName(phase.TabName())));
    }

    public class BacklogStoreTests
    {
        [Fact]
        public async Task AddAsync_NewTicket_CreatesADocumentAndAnIndexRow()
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
        public async Task AddAsync_EarlierIdsLiveOnOtherTabs_AllocatesMaxPlusOneAcrossThemAll()
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
        public async Task StartAsync_RowLeavesTheBacklogTab_FreezesTheFormulasIntoValues()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something", bv: 8, tc: 3, rroe: 2, size: 5);

            Assert.StartsWith("=", backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.Wsjf));
            Assert.StartsWith("=", backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.Rank));

            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            // Frozen: a plain number, no formula, and no rank column at all on this tab.
            Assert.Equal("2.6", backlog.CellText(BacklogPhase.InProgress, ticket.Id, SheetSchema.Wsjf));
            Assert.Equal("13", backlog.CellText(BacklogPhase.InProgress, ticket.Id, SheetSchema.CostOfDelay));
            Assert.DoesNotContain(SheetSchema.Rank, SheetSchema.Columns(BacklogPhase.InProgress));
        }

        [Fact]
        public async Task AddAsync_NewTicket_LeavesTheTitleCellAsPlainTextCarryingALink()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("WSJF index tool");

            // The stored value is the title, not a HYPERLINK() formula — that is the whole point
            // of using a rich-text run.
            Assert.Equal("WSJF index tool", backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.Title));
            Assert.Contains(backlog.Sheets.Links, link => link.Value.Contains(ticket.DocId!));
        }

        [Fact]
        public async Task ListAsync_QueueMixesScoredAndUnscored_RanksByWsjfAndSortsUnscoredLast()
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
        public async Task StartAsync_WorkBegins_MovesTheRowButLeavesTheDocumentInPlace()
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
        public async Task StartAsync_WorkBegins_RemovesItFromTheQueueAndAddsItToWip()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            Assert.Empty(await backlog.Store.ListAsync(new TicketFilter()));
            Assert.Single(await backlog.Store.WipAsync(new TicketFilter()));
        }

        [Fact]
        public async Task ScoreAsync_WorkHasAlreadyStarted_ThrowsExplainingWhy()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");
            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            var exception = await Assert.ThrowsAsync<BacklogTransitionException>(
                () => backlog.Store.ScoreAsync(ticket.Id, new WsjfScore { BusinessValue = 20 }));

            Assert.Contains("no longer subject to WSJF", exception.Message);
        }

        [Fact]
        public async Task SetStateAsync_BlockedThenUnblocked_RecordsTheReasonThenClearsIt()
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
        public async Task SetStateAsync_BlockedWithNoReason_Throws()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");
            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            await Assert.ThrowsAsync<ArgumentException>(
                () => backlog.Store.SetStateAsync(ticket.Id, WorkState.Blocked, "  "));
        }

        [Fact]
        public async Task SetStateAsync_WorkHasNotStarted_ThrowsPointingAtBacklogStart()
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
        public async Task ArchiveAsync_TicketIsArchived_MovesTheRowAndDocumentWithoutDeletingAnything()
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
        public async Task ArchiveAsync_TicketIsArchived_FreezesLeadAndCycleTime()
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
        public async Task ArchiveAsync_StartedAndArchivedTheSameDay_ReportsZeroRatherThanANegativeNumber()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            await backlog.Store.StartAsync(ticket.Id, "jason", false);
            var archived = await backlog.Store.ArchiveAsync(ticket.Id, Outcome.Done, null);

            Assert.Equal(0, archived.CycleDays);
            Assert.Equal(0, archived.LeadDays);
        }

        [Fact]
        public async Task ArchiveAsync_CancelledStraightFromTheBacklog_HasLeadTimeButNoCycleTime()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Never starting this");

            var archived = await backlog.Store.ArchiveAsync(ticket.Id, Outcome.Cancelled, "out of scope");

            Assert.Equal(Outcome.Cancelled, archived.Outcome);
            Assert.Null(archived.CycleDays);
            Assert.NotNull(archived.LeadDays);
        }

        [Fact]
        public async Task ArchiveAsync_TicketIsAlreadyArchived_Throws()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");
            await backlog.Store.ArchiveAsync(ticket.Id, Outcome.Done, null);

            await Assert.ThrowsAsync<BacklogTransitionException>(
                () => backlog.Store.ArchiveAsync(ticket.Id, Outcome.Done, null));
        }

        [Fact]
        public async Task RestoreAsync_TicketIsArchived_ReturnsItToTheQueueWithLiveFormulas()
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
        public async Task RestoreAsync_TicketIsNotArchived_Throws()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            await Assert.ThrowsAsync<BacklogTransitionException>(() => backlog.Store.RestoreAsync(ticket.Id));
        }
    }

    public class MoveOrderingTests
    {
        [Fact]
        public async Task StartAsync_DeleteFailsAfterTheAppend_DuplicatesTheTicketRatherThanLosingIt()
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
        public async Task DoctorAsync_AnInterruptedMoveLeftADuplicate_ReportsIt()
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
        public async Task StartAsync_MovingARowAcrossTabs_AppendsBeforeItDeletes()
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

        [Fact]
        public async Task CreateAsync_BacklogStillHasTheLegacyShortHeaders_WritesAFullRowAndKeepsTheHeadersAsTheyAre()
        {
            var backlog = await TestBacklog.CreateAsync();
            await backlog.UseLegacyHeadersAsync();

            var ticket = await backlog.AddAsync("Something", bv: 8, tc: 3, rroe: 2, size: 5);

            Assert.Equal("8", backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.BusinessValue));
            Assert.Equal("2", backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.RiskOpportunity));
            Assert.StartsWith("=", backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.Wsjf));
            Assert.Equal(ticket.DocId, backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.DriveFileId));

            // Untouched: we understand the old header row, we do not rewrite it.
            Assert.Equal("rroe", backlog.Sheets.Rows(BacklogPhase.Backlog.TabName())[0][7]?.ToString());
        }

        [Fact]
        public async Task DoctorAsync_BacklogStillHasTheLegacyShortHeaders_ReportsNoMissingColumns()
        {
            var backlog = await TestBacklog.CreateAsync();
            await backlog.UseLegacyHeadersAsync();
            await backlog.AddAsync("Something");

            var report = await backlog.Store.DoctorAsync();

            Assert.DoesNotContain(report.Issues, issue => issue.Kind == "missing-column");
            Assert.True(report.IsHealthy);
        }

        [Fact]
        public async Task StartAsync_BacklogStillHasTheLegacyShortHeaders_FreezesTheFormulasAcrossTheMove()
        {
            var backlog = await TestBacklog.CreateAsync();
            await backlog.UseLegacyHeadersAsync();

            var ticket = await backlog.AddAsync("Something", bv: 8, tc: 3, rroe: 2, size: 5);
            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            Assert.Equal("2.6", backlog.CellText(BacklogPhase.InProgress, ticket.Id, SheetSchema.Wsjf));
            Assert.Equal("13", backlog.CellText(BacklogPhase.InProgress, ticket.Id, SheetSchema.CostOfDelay));
        }
    }
}

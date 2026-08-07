namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// `edit --description` end to end. The description could only ever be seeded at `new`, so
    /// correcting one meant opening the document — this is the store side of closing that gap,
    /// and the assertions are mostly about the rest of the document surviving it.
    /// </summary>
    public class TicketDescriptionTests
    {
        static Task<Ticket> AddAsync(TestBacklog backlog, string description) =>
            backlog.Store.CreateAsync(new NewTicket
            {
                Title = "WSJF index tool",
                Area = "agent",
                Description = description
            });

        [Fact]
        public async Task UpdateAsync_WithDescription_ReplacesTheDescriptionSection()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "The first attempt at explaining this.");

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { Description = "A much clearer explanation." });

            var body = await backlog.Store.GetBodyAsync(ticket.Id);

            Assert.Contains("## Description\n\nA much clearer explanation.", body, StringComparison.Ordinal);
            Assert.DoesNotContain("The first attempt", body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task UpdateAsync_WithDescription_LeavesEveryOtherSectionAlone()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "Original.");

            await backlog.Store.AppendNoteAsync(ticket.Id, "a note somebody left");
            var before = await backlog.Store.GetBodyAsync(ticket.Id);

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { Description = "Rewritten." });
            var after = await backlog.Store.GetBodyAsync(ticket.Id);

            var tail = "## Acceptance Criteria";
            Assert.Equal(before[before.IndexOf(tail, StringComparison.Ordinal)..], after[after.IndexOf(tail, StringComparison.Ordinal)..]);
            Assert.Contains("a note somebody left", after, StringComparison.Ordinal);
        }

        /// <summary>
        /// No automatic log entry: `edit` does not log a title or an owner change either, and the
        /// document's own revision history in Docs is what recovers an overwrite. Someone who
        /// wants the change on the record uses `note`.
        /// </summary>
        [Fact]
        public async Task UpdateAsync_WithDescription_DoesNotWriteToTheActivityLog()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "Original.");

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { Description = "Rewritten." });

            var log = (await backlog.Store.GetBodyAsync(ticket.Id));
            log = log[log.IndexOf("## Activity Log", StringComparison.Ordinal)..];

            Assert.Equal(1, log.Split("\n- ").Length - 1);
            Assert.Contains("created", log, StringComparison.Ordinal);
        }

        [Fact]
        public async Task UpdateAsync_WithDescriptionAndOtherFields_AppliesBoth()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "Original.");

            var updated = await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit
            {
                Title = "Rank the queue",
                Description = "Rewritten."
            });

            var body = await backlog.Store.GetBodyAsync(ticket.Id);

            Assert.Equal("Rank the queue", updated.Title);
            Assert.Equal("Rank the queue", backlog.CellText(BacklogPhase.Backlog, ticket.Id, SheetSchema.Title));
            Assert.Contains("Rewritten.", body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task UpdateAsync_WithoutDescription_LeavesTheBodyByteIdentical()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "Original.");

            var before = await backlog.Store.GetBodyAsync(ticket.Id);
            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { Area = "cli" });

            Assert.Equal(before, await backlog.Store.GetBodyAsync(ticket.Id));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\n")]
        public async Task UpdateAsync_DescriptionIsBlank_ThrowsRatherThanEmptyingIt(string description)
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "Original.");

            await Assert.ThrowsAsync<ArgumentException>(
                () => backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { Description = description }));

            Assert.Contains("Original.", await backlog.Store.GetBodyAsync(ticket.Id), StringComparison.Ordinal);
        }

        /// <summary>
        /// A row whose Drive File ID was lost is damage <c>doctor</c> reports. Reporting a
        /// successful edit of a document that is not there would be the silent no-op this flag
        /// exists to end.
        /// </summary>
        [Fact]
        public async Task UpdateAsync_RowHasNoDocument_ThrowsRatherThanReportingSuccess()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "Original.");

            await backlog.SetCellAsync(BacklogPhase.Backlog, ticket.Id, SheetSchema.DriveFileId, string.Empty);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { Description = "Rewritten." }));

            Assert.Contains("doctor", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task UpdateAsync_TicketIsInProgress_StillEditsTheDescription()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "Original.");
            await backlog.Store.StartAsync(ticket.Id, "someone@noogen.ai", false);

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { Description = "Rewritten." });

            Assert.Contains("Rewritten.", await backlog.Store.GetBodyAsync(ticket.Id), StringComparison.Ordinal);
        }

        [Fact]
        public async Task UpdateAsync_DescriptionSpansLines_KeepsTheShapeTheAuthorTyped()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "Original.");

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit
            {
                Description = "Why this matters.\n\n- the first reason\n- the second"
            });

            var body = await backlog.Store.GetBodyAsync(ticket.Id);

            Assert.Contains("Why this matters.\n\n- the first reason\n- the second", body, StringComparison.Ordinal);
            Assert.Contains("## Acceptance Criteria", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The heading and the bullets stay the store's, and there is still only one of each —
        /// rewriting a body section must not disturb what Serialize regenerates above it.
        /// </summary>
        [Fact]
        public async Task UpdateAsync_WithDescription_LeavesTheMetadataBlockIntact()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "Original.");

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { Description = "Rewritten." });

            var raw = await backlog.Drive.ReadDocAsync(ticket.DocId!);
            var parsed = TicketDocument.Parse(raw);

            Assert.Equal(ticket.Id, parsed.Ticket.Id);
            Assert.Equal("WSJF index tool", parsed.Ticket.Title);
            Assert.Equal("agent", parsed.Ticket.Area);
            Assert.StartsWith($"# {ticket.Id} — WSJF index tool", raw, StringComparison.Ordinal);
        }
    }
}

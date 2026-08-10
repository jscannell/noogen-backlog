namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// Acceptance criteria end to end. Until this existed the section could only be written by
    /// opening the document in Docs, so every ticket an agent filed kept `- [ ] *TODO*` and read
    /// as ready when nobody had said what "done" meant. The assertions are the same shape as
    /// <see cref="TicketDescriptionTests"/>: mostly about what the rewrite must *not* touch.
    /// </summary>
    public class TicketAcceptanceCriteriaTests
    {
        const string Criteria = "- [ ] doctor reports the duplicate\n- [ ] the row survives a restart";

        static Task<Ticket> AddAsync(TestBacklog backlog, string? criteria) =>
            backlog.Store.CreateAsync(new NewTicket
            {
                Title = "WSJF index tool",
                Area = "agent",
                Description = "Why this matters.",
                AcceptanceCriteria = criteria
            });

        [Fact]
        public async Task CreateAsync_WithAcceptanceCriteria_WritesThemInsteadOfThePlaceholder()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, Criteria);

            var body = await backlog.Store.GetBodyAsync(ticket.Id);

            Assert.Contains($"## Acceptance Criteria\n\n{Criteria}", body, StringComparison.Ordinal);
            Assert.DoesNotContain(TicketDocument.UnwrittenCriteria, body, StringComparison.Ordinal);
        }

        /// <summary>
        /// Filing fast is still worth keeping, so the placeholder stays for a ticket captured
        /// without them — what changed is that it is now fillable without leaving the CLI.
        /// </summary>
        [Fact]
        public async Task CreateAsync_WithoutAcceptanceCriteria_LeavesThePlaceholder()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, null);

            Assert.Contains(
                $"## Acceptance Criteria\n\n{TicketDocument.UnwrittenCriteria}",
                await backlog.Store.GetBodyAsync(ticket.Id),
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task UpdateAsync_WithAcceptanceCriteria_ReplacesThePlaceholder()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, null);

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { AcceptanceCriteria = Criteria });

            var body = await backlog.Store.GetBodyAsync(ticket.Id);

            Assert.Contains($"## Acceptance Criteria\n\n{Criteria}", body, StringComparison.Ordinal);
            Assert.DoesNotContain(TicketDocument.UnwrittenCriteria, body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task UpdateAsync_WithAcceptanceCriteria_LeavesEveryOtherSectionAlone()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, null);

            await backlog.Store.AppendNoteAsync(ticket.Id, "a note somebody left");
            var before = await backlog.Store.GetBodyAsync(ticket.Id);

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { AcceptanceCriteria = Criteria });
            var after = await backlog.Store.GetBodyAsync(ticket.Id);

            Assert.Equal(
                before[..before.IndexOf("## Acceptance Criteria", StringComparison.Ordinal)],
                after[..after.IndexOf("## Acceptance Criteria", StringComparison.Ordinal)]);

            const string tail = "## Notes";
            Assert.Equal(before[before.IndexOf(tail, StringComparison.Ordinal)..], after[after.IndexOf(tail, StringComparison.Ordinal)..]);
        }

        /// <summary>
        /// Both sections in one write. They are replaced independently, so neither may consume the
        /// other — the failure would be one rewrite ending at the wrong heading.
        /// </summary>
        [Fact]
        public async Task UpdateAsync_WithBothSections_ReplacesEachInPlace()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, null);

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit
            {
                Description = "A much clearer explanation.",
                AcceptanceCriteria = Criteria
            });

            var body = await backlog.Store.GetBodyAsync(ticket.Id);

            Assert.Contains("## Description\n\nA much clearer explanation.", body, StringComparison.Ordinal);
            Assert.Contains($"## Acceptance Criteria\n\n{Criteria}", body, StringComparison.Ordinal);
            Assert.Contains("## Notes", body, StringComparison.Ordinal);
            Assert.Contains("## Activity Log", body, StringComparison.Ordinal);
        }

        /// <summary>Same reasoning as a blank description: emptying it throws away writing.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\n")]
        public async Task UpdateAsync_AcceptanceCriteriaAreBlank_ThrowsRatherThanEmptyingThem(string criteria)
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, Criteria);

            await Assert.ThrowsAsync<ArgumentException>(
                () => backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { AcceptanceCriteria = criteria }));

            Assert.Contains(Criteria, await backlog.Store.GetBodyAsync(ticket.Id), StringComparison.Ordinal);
        }

        [Fact]
        public async Task UpdateAsync_WithAcceptanceCriteria_DoesNotWriteToTheActivityLog()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, null);

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { AcceptanceCriteria = Criteria });

            var log = await backlog.Store.GetBodyAsync(ticket.Id);
            log = log[log.IndexOf("## Activity Log", StringComparison.Ordinal)..];

            Assert.Equal(1, log.Split("\n- ").Length - 1);
            Assert.Contains("created", log, StringComparison.Ordinal);
        }

        /// <summary>
        /// A ticket already in flight is exactly when criteria get written — the work started
        /// before anyone spelled out what finishing it means.
        /// </summary>
        [Fact]
        public async Task UpdateAsync_TicketIsInProgress_StillEditsTheAcceptanceCriteria()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, null);
            await backlog.Store.StartAsync(ticket.Id, "someone@noogen.ai", false);

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { AcceptanceCriteria = Criteria });

            Assert.Contains(Criteria, await backlog.Store.GetBodyAsync(ticket.Id), StringComparison.Ordinal);
        }

        [Fact]
        public async Task UpdateAsync_RowHasNoDocument_ThrowsRatherThanReportingSuccess()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, null);

            await backlog.SetCellAsync(BacklogPhase.Backlog, ticket.Id, SheetSchema.DriveFileId, string.Empty);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { AcceptanceCriteria = Criteria }));

            Assert.Contains("doctor", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>The heading and the bullets stay the store's, and there is still one of each.</summary>
        [Fact]
        public async Task UpdateAsync_WithAcceptanceCriteria_LeavesTheMetadataBlockIntact()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, null);

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { AcceptanceCriteria = Criteria });

            var raw = await backlog.Drive.ReadDocAsync(ticket.DocId!);
            var parsed = TicketDocument.Parse(raw);

            Assert.Equal(ticket.Id, parsed.Ticket.Id);
            Assert.Equal("WSJF index tool", parsed.Ticket.Title);
            Assert.Equal("agent", parsed.Ticket.Area);
            Assert.StartsWith($"# {ticket.Id} — WSJF index tool", raw, StringComparison.Ordinal);
        }

        /// <summary>
        /// Checkbox syntax is markdown Docs has no widget for, so it exports as the literal text
        /// that went in. The parser must keep treating all of it as body — a `- [ ] ...` line is
        /// one bullet away from the `- **Key:** value` shape the metadata block is made of.
        /// </summary>
        [Fact]
        public async Task UpdateAsync_CriteriaAreCheckboxes_AreNotMistakenForMetadataBullets()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, null);

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { AcceptanceCriteria = Criteria });

            var parsed = TicketDocument.Parse(await backlog.Drive.ReadDocAsync(ticket.DocId!));

            Assert.Empty(parsed.Ticket.ExtraFields);
            Assert.Contains(Criteria, parsed.Body, StringComparison.Ordinal);
        }
    }
}

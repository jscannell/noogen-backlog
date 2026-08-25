using System.Text.Json;

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// The seam more than one front end sits on. What is pinned here is what would otherwise be
    /// written twice: the verbs that compose several store calls, the ones that have something to
    /// report beside their result, and the exact keys each answer carries.
    ///
    /// The keys matter most. `--json` is what the skill parses today and what the MCP server will
    /// return tomorrow, and "both front ends answer" is not the same promise as "both answer the
    /// same way" — the second one is only kept if something checks.
    /// </summary>
    public class BacklogApiTests
    {
        static JsonElement Node(IBacklogView view, IReadOnlySet<string>? fields = null) =>
            JsonDocument.Parse(BacklogJson.Serialize(view.ToNode(fields))).RootElement;

        static IReadOnlyList<string> KeysOf(JsonElement element) =>
            [.. element.EnumerateObject().Select(property => property.Name)];

        // --- the queue ---

        [Fact]
        public async Task NextAsync_NoTopGiven_AnswersWithOneTicket()
        {
            var backlog = await TestBacklog.CreateAsync();

            await backlog.AddAsync("First", bv: 20, size: 1);
            await backlog.AddAsync("Second", bv: 1, size: 20);

            var queue = await backlog.Api.NextAsync(new TicketFilter());

            Assert.Single(queue.Tickets);
            Assert.Equal("First", queue.Tickets[0].Title);
        }

        [Fact]
        public async Task NextAsync_TopGiven_LeavesTheCallersCapAlone()
        {
            var backlog = await TestBacklog.CreateAsync();

            await backlog.AddAsync("First");
            await backlog.AddAsync("Second");

            var queue = await backlog.Api.NextAsync(new TicketFilter { Top = 2 });

            Assert.Equal(2, queue.Tickets.Count);
        }

        [Fact]
        public async Task ListAsync_FieldsAreNamed_NarrowsEveryTicketInTheArray()
        {
            var backlog = await TestBacklog.CreateAsync();
            await backlog.AddAsync("First");

            var queue = await backlog.Api.ListAsync(new TicketFilter());
            var array = Node(queue, BacklogJson.ParseFields("id,title"));

            Assert.Equal(JsonValueKind.Array, array.ValueKind);
            Assert.Equal(["id", "title"], KeysOf(array[0]).Order());
        }

        // --- wip ---

        /// <summary>
        /// `wip` is three store calls — the items, the flow percentiles, and the WIP limit — and
        /// the answer is useless without all three: a count means nothing without the limit, and
        /// "aging" means nothing without the threshold it is measured against.
        /// </summary>
        [Fact]
        public async Task WipAsync_ItemsInFlight_CarriesTheLimitAndTheAgingThreshold()
        {
            var backlog = await TestBacklog.CreateAsync(wipLimit: 3);

            var ticket = await backlog.AddAsync("Started");
            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            var wip = await backlog.Api.WipAsync(new TicketFilter());

            Assert.Equal(3, wip.WipLimit);
            Assert.Equal(1, wip.InFlight);
            Assert.Single(wip.Tickets);
        }

        [Fact]
        public async Task WipAsync_AnyBacklog_EmitsTheKeysTheContractPromises()
        {
            var backlog = await TestBacklog.CreateAsync();
            var wip = await backlog.Api.WipAsync(new TicketFilter());

            Assert.Equal(
                ["agingThresholdDays", "inFlight", "tickets", "wipLimit"],
                KeysOf(Node(wip)).Order());
        }

        [Fact]
        public async Task WipAsync_FieldsAreNamed_NarrowsTheTicketsInsideTheEnvelope()
        {
            var backlog = await TestBacklog.CreateAsync();

            var ticket = await backlog.AddAsync("Started");
            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            var node = Node(await backlog.Api.WipAsync(new TicketFilter()), BacklogJson.ParseFields("id,state"));

            Assert.Equal(["id", "state"], KeysOf(node.GetProperty("tickets")[0]).Order());

            // The envelope is not a ticket, so narrowing must not touch it.
            Assert.True(node.TryGetProperty("wipLimit", out _));
        }

        // --- show ---

        [Fact]
        public async Task ShowAsync_SectionIsNamed_ReturnsOnlyThatSection()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Some work");

            await backlog.Store.UpdateAsync(ticket.Id, new TicketEdit { Description = "Only this." });

            var detail = await backlog.Api.ShowAsync(ticket.Id, section: "description");

            Assert.Contains("Only this.", detail.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("## Acceptance Criteria", detail.Body, StringComparison.Ordinal);
        }

        /// <summary>
        /// A hyphenated section name is how a caller spells a two-word heading without quoting it,
        /// on either surface.
        /// </summary>
        [Fact]
        public async Task ShowAsync_SectionNameIsHyphenated_ResolvesToTheHeading()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Some work");

            var detail = await backlog.Api.ShowAsync(ticket.Id, section: "acceptance-criteria");

            Assert.Contains("TODO", detail.Body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ShowAsync_SectionDoesNotExist_RefusesAndNamesTheOnesThatDo()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Some work");

            var exception = await Assert.ThrowsAsync<UsageException>(
                () => backlog.Api.ShowAsync(ticket.Id, section: "design"));

            Assert.Contains("design", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Description", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Trimming is display only. It must never be what a write is handed — see invariant 9 —
        /// so the check that matters is that the untrimmed body is still reachable.
        /// </summary>
        [Fact]
        public async Task ShowAsync_FullIsAsked_KeepsEveryActivityLogEntry()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Some work");

            for (var i = 0; i < BacklogApi.ActivityLogEntriesShown + 2; i++)
                await backlog.Store.AppendNoteAsync(ticket.Id, $"note {i}");

            var trimmed = await backlog.Api.ShowAsync(ticket.Id);
            var whole = await backlog.Api.ShowAsync(ticket.Id, full: true);

            Assert.DoesNotContain("note 0", trimmed.Body, StringComparison.Ordinal);
            Assert.Contains("note 0", whole.Body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ShowAsync_AnyTicket_EmitsTheTicketAndBodyKeys()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Some work");

            Assert.Equal(["body", "ticket"], KeysOf(Node(await backlog.Api.ShowAsync(ticket.Id))).Order());
        }

        // --- filing ---

        /// <summary>
        /// Filing fast is worth keeping, so an unwritten section is a placeholder rather than a
        /// refusal. What must not come back is a *silent* placeholder: an unwritten acceptance
        /// criterion reads downstream as a finished ticket.
        /// </summary>
        [Fact]
        public async Task CreateAsync_NoProseGiven_NamesBothSectionsItLeftAsPlaceholders()
        {
            var backlog = await TestBacklog.CreateAsync();

            var filed = await backlog.Api.CreateAsync(new NewTicket { Title = "Unwritten" });

            Assert.Equal(["description", "acceptance criteria"], filed.MissingSections);
            Assert.Contains("*TODO*", filed.Reminder!, StringComparison.Ordinal);
            Assert.Contains(filed.Ticket.Id, filed.Reminder!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CreateAsync_OnlyCriteriaOmitted_NamesJustThatOne()
        {
            var backlog = await TestBacklog.CreateAsync();

            var filed = await backlog.Api.CreateAsync(new NewTicket { Title = "Half written", Description = "Something." });

            Assert.Equal(["acceptance criteria"], filed.MissingSections);
        }

        [Fact]
        public async Task CreateAsync_BothSectionsGiven_HasNothingToReport()
        {
            var backlog = await TestBacklog.CreateAsync();

            var filed = await backlog.Api.CreateAsync(new NewTicket
            {
                Title = "Whole",
                Description = "Something.",
                AcceptanceCriteria = "- [ ] it works"
            });

            Assert.Empty(filed.MissingSections);
            Assert.Null(filed.Reminder);
        }

        /// <summary>
        /// The reminder rides beside the result, never inside it: `new` has always answered with a
        /// ticket and nothing else, and a caller parsing that shape must not start seeing a key it
        /// does not know.
        /// </summary>
        [Fact]
        public async Task CreateAsync_SectionsWereLeftUnwritten_StillEmitsOnlyTheTicketOnTheWire()
        {
            var backlog = await TestBacklog.CreateAsync();

            var filed = await backlog.Api.CreateAsync(new NewTicket { Title = "Unwritten" });
            var keys = KeysOf(Node(filed));

            Assert.Contains("id", keys, StringComparer.Ordinal);
            Assert.DoesNotContain("reminder", keys, StringComparer.Ordinal);
            Assert.DoesNotContain("missingSections", keys, StringComparer.Ordinal);
        }

        // --- refusals ---

        [Fact]
        public async Task ScoreAsync_NoDimensionGiven_RefusesRatherThanWritingNothing()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Some work");

            var exception = await Assert.ThrowsAsync<UsageException>(
                () => backlog.Api.ScoreAsync(ticket.Id, new WsjfScore()));

            Assert.Contains("bv", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ShowAsync_NoSuchTicket_ReportsItAsNotFound()
        {
            var backlog = await TestBacklog.CreateAsync();

            var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => backlog.Api.ShowAsync("NG-9999"));

            Assert.Equal(BacklogFault.NotFound, BacklogFault.KindOf(exception));
        }

        // --- maintenance ---

        [Fact]
        public async Task DoctorAsync_HealthyBacklog_EmitsTheKeysTheContractPromises()
        {
            var backlog = await TestBacklog.CreateAsync();
            await backlog.AddAsync("Some work");

            var report = await backlog.Api.DoctorAsync();

            Assert.True(report.Healthy);
            Assert.Equal(1, report.TicketCount);
            Assert.Equal(["healthy", "issues", "ticketCount"], KeysOf(Node(report)).Order());
        }

        [Fact]
        public async Task ReindexAsync_AnyBacklog_ReportsHowManyRowsItRewrote()
        {
            var backlog = await TestBacklog.CreateAsync();

            Assert.Equal(["repaired"], KeysOf(Node(await backlog.Api.ReindexAsync())));
        }
    }
}

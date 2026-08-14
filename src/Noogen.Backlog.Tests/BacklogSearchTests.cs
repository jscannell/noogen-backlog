namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// Search reads two sources that fail differently, so most of what matters here is which one
    /// answered: the Sheet half is exact and immediate, the Drive half reaches prose the Sheet does
    /// not hold but matches whole words and lags a write. The fake mirrors both properties —
    /// <c>FakeDriveGateway.SearchTextAsync</c> tokenises rather than matching substrings, and
    /// <c>SearchFindsNothing</c> is the index before it has caught up.
    /// </summary>
    public class BacklogSearchTests
    {
        static TicketFilter Everything => new();

        static Task<Ticket> AddAsync(TestBacklog backlog, string title, string? description = null, string area = "agent", string? owner = null) =>
            backlog.Store.CreateAsync(new NewTicket
            {
                Title = title,
                Area = area,
                Owner = owner,
                Description = description,
                Score = new WsjfScore
                {
                    BusinessValue = 8,
                    TimeCriticality = 3,
                    RiskReductionOpportunityEnablement = 2,
                    JobSize = 5
                }
            });

        [Fact]
        public async Task SearchAsync_TextIsInTheTitle_ReturnsTheTicket()
        {
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Retry the rate limiter");
            await AddAsync(backlog, "Publish the privacy policy");

            var matches = await backlog.Store.SearchAsync("rate limiter", Everything);

            var match = Assert.Single(matches);
            Assert.Equal("Retry the rate limiter", match.Ticket.Title);
            Assert.True(match.InName);
        }

        [Fact]
        public async Task SearchAsync_TextIsOnlyPartOfAWord_StillMatchesTheName()
        {
            // The Sheet half matches substrings, which is exactly what Drive's term index will not
            // do: 'sign' is not a token of 'Redesign'. Without this half, searching for a fragment
            // of a title finds nothing.
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Redesign the intake form");

            var matches = await backlog.Store.SearchAsync("sign", Everything);

            var match = Assert.Single(matches);
            Assert.True(match.InName);
            Assert.False(match.InBody);
        }

        [Fact]
        public async Task SearchAsync_TextIsInTheId_ReturnsTheTicket()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "Retry the rate limiter");

            var matches = await backlog.Store.SearchAsync(ticket.Id, Everything);

            Assert.Equal(ticket.Id, Assert.Single(matches).Ticket.Id);
        }

        [Fact]
        public async Task SearchAsync_TextIsInTheAreaOrOwner_ReturnsTheTicket()
        {
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Retry the rate limiter", area: "platform", owner: "j@noogen.ai");

            Assert.Single(await backlog.Store.SearchAsync("platform", Everything));
            Assert.Single(await backlog.Store.SearchAsync("j@noogen.ai", Everything));
        }

        [Fact]
        public async Task SearchAsync_TextIsOnlyInTheDocumentBody_ReturnsTheTicketAsABodyMatch()
        {
            // The Sheet holds no prose at all, so this hit can only come from Drive.
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Retry the rate limiter", "Honour the Retry-After header Google sends.");

            var matches = await backlog.Store.SearchAsync("header", Everything);

            var match = Assert.Single(matches);
            Assert.True(match.InBody);
            Assert.False(match.InName);
            Assert.Equal(["body"], match.Where);
        }

        [Fact]
        public async Task SearchAsync_TextIsInBothTheTitleAndTheBody_ReturnsOneMatchNamingBoth()
        {
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Retry the limiter", "The limiter refuses rather than half-applying.");

            var match = Assert.Single(await backlog.Store.SearchAsync("limiter", Everything));

            Assert.True(match.InName);
            Assert.True(match.InBody);
            Assert.Equal(["name", "body"], match.Where);
        }

        [Fact]
        public async Task SearchAsync_NothingMatches_ReturnsEmpty()
        {
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Retry the rate limiter");

            Assert.Empty(await backlog.Store.SearchAsync("kubernetes", Everything));
        }

        [Fact]
        public async Task SearchAsync_TicketsAreOnEveryTab_SearchesAllOfThem()
        {
            // Every other query verb is scoped to one column. "Have we discussed this before?" is
            // most often answered by something already archived.
            var backlog = await TestBacklog.CreateAsync();

            await AddAsync(backlog, "Widget in the backlog");
            var started = await AddAsync(backlog, "Widget in flight");
            var archived = await AddAsync(backlog, "Widget that shipped");

            await backlog.Store.StartAsync(started.Id, "j@noogen.ai", false);
            await backlog.Store.StartAsync(archived.Id, "j@noogen.ai", false);
            await backlog.Store.ArchiveAsync(archived.Id, Outcome.Done, null);

            var matches = await backlog.Store.SearchAsync("widget", Everything);

            Assert.Equal(3, matches.Count);
            Assert.Equal(
                [BacklogPhase.Backlog, BacklogPhase.InProgress, BacklogPhase.Archive],
                matches.Select(match => match.Ticket.Phase).OrderBy(phase => phase));
        }

        [Fact]
        public async Task SearchAsync_TicketIsArchived_ReturnsTheRowFromTheSheetRatherThanTheDocument()
        {
            // The join is on the document id, but what comes back is the Sheet's row — which is
            // the only place the phase and the outcome live.
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await AddAsync(backlog, "Retry the rate limiter", "Honour the Retry-After header.");

            await backlog.Store.StartAsync(ticket.Id, "j@noogen.ai", false);
            await backlog.Store.ArchiveAsync(ticket.Id, Outcome.Done, null);

            var match = Assert.Single(await backlog.Store.SearchAsync("header", Everything));

            Assert.Equal(BacklogPhase.Archive, match.Ticket.Phase);
            Assert.Equal(Outcome.Done, match.Ticket.Outcome);
        }

        [Fact]
        public async Task SearchAsync_DriveHoldsAMatchingFileThatIsNotATicket_LeavesItOut()
        {
            // The backlog's own index spreadsheet sits in the same drive, and anyone may drop a
            // document into the folder. The join on Drive File ID is what keeps those out.
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Retry the rate limiter");

            await backlog.Drive.CreateDocAsync(backlog.Init.TicketsFolderId, "meeting notes", "We discussed kubernetes.");

            Assert.Empty(await backlog.Store.SearchAsync("kubernetes", Everything));
        }

        [Fact]
        public async Task SearchAsync_DriveHasNotIndexedTheDocumentYet_StillReturnsTheNameMatch()
        {
            // The case the two sources exist for: Drive's index lags a write, and the ticket
            // somebody filed a minute ago is the one they are about to file again.
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Retry the rate limiter", "Honour the Retry-After header.");

            backlog.Drive.SearchFindsNothing = true;

            var match = Assert.Single(await backlog.Store.SearchAsync("rate limiter", Everything));

            Assert.True(match.InName);
            Assert.False(match.InBody);
        }

        [Fact]
        public async Task SearchAsync_Always_ConfinesTheDriveQueryToTheBacklogsOwnSharedDrive()
        {
            // The tool holds full Drive scope, so an unconfined query would sweep everything the
            // signed-in person can read.
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Retry the rate limiter");

            await backlog.Store.SearchAsync("rate limiter", Everything);

            Assert.Equal("shared-drive-1", Assert.Single(backlog.Drive.SearchDriveIds));
        }

        [Fact]
        public async Task SearchAsync_BacklogIsNotOnASharedDrive_SearchesWithoutADriveIdRatherThanFailing()
        {
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Retry the rate limiter", "Honour the Retry-After header.");

            backlog.Drive.DriveId = null;

            var match = Assert.Single(await backlog.Store.SearchAsync("header", Everything));

            Assert.True(match.InBody);
            Assert.Null(Assert.Single(backlog.Drive.SearchDriveIds));
        }

        [Fact]
        public async Task SearchAsync_NameAndBodyBothMatchDifferentTickets_PutsTheNameMatchFirst()
        {
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Something else entirely", "The limiter is discussed here.");
            await AddAsync(backlog, "Retry the limiter");

            var matches = await backlog.Store.SearchAsync("limiter", Everything);

            Assert.Equal(2, matches.Count);
            Assert.Equal("Retry the limiter", matches[0].Ticket.Title);
            Assert.True(matches[0].InName);
            Assert.False(matches[1].InName);
        }

        [Fact]
        public async Task SearchAsync_TopGiven_CapsTheResults()
        {
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Widget one");
            await AddAsync(backlog, "Widget two");
            await AddAsync(backlog, "Widget three");

            var matches = await backlog.Store.SearchAsync("widget", new TicketFilter { Top = 2 });

            Assert.Equal(2, matches.Count);
        }

        [Fact]
        public async Task SearchAsync_AreaGiven_NarrowsToThatArea()
        {
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Widget one", area: "platform");
            await AddAsync(backlog, "Widget two", area: "website");

            var match = Assert.Single(await backlog.Store.SearchAsync("widget", new TicketFilter { Area = "platform" }));

            Assert.Equal("Widget one", match.Ticket.Title);
        }

        [Fact]
        public async Task SearchAsync_TextIsBlank_Throws()
        {
            var backlog = await TestBacklog.CreateAsync();

            await Assert.ThrowsAsync<ArgumentException>(() => backlog.Store.SearchAsync("   ", Everything));
        }

        [Fact]
        public async Task SearchAsync_TextDiffersInCase_StillMatches()
        {
            var backlog = await TestBacklog.CreateAsync();
            await AddAsync(backlog, "Retry the Rate Limiter");

            Assert.Single(await backlog.Store.SearchAsync("RATE LIMITER", Everything));
        }
    }
}

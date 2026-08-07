namespace Noogen.Backlog.Tests
{
    public class TimeZoneIntegrationTests
    {
        [Fact]
        public async Task InitializeAsync_TimeZoneConfigured_StampsItOnBothTheSpreadsheetAndTheConfigTab()
        {
            var backlog = await TestBacklog.CreateAsync(timeZoneId: "America/New_York");

            // Both must agree: a datetime cell is a wall-clock value read against the
            // spreadsheet's own timezone.
            Assert.Equal("America/New_York", backlog.Sheets.TimeZoneId);
            Assert.Equal("America/New_York", backlog.Init.TimeZoneId);

            var settings = await backlog.Store.GetSettingsAsync();
            Assert.Equal("America/New_York", settings.TimeZoneId);
        }

        [Fact]
        public async Task AddAsync_NewTicket_WritesTimestampsAsNumericSerialsNotText()
        {
            var backlog = await TestBacklog.CreateAsync(timeZoneId: "America/New_York");
            var ticket = await backlog.AddAsync("Something");

            var cell = backlog.RawCell(BacklogPhase.Backlog, ticket.Id, SheetSchema.Created);

            Assert.IsType<double>(cell);
            Assert.True((double)cell > 40000);   // somewhere this century, not a stray 0
        }

        [Fact]
        public async Task InitializeAsync_Always_FormatsTheTimestampColumnsUnboundedBelowTheHeader()
        {
            var backlog = await TestBacklog.CreateAsync();

            var call = backlog.Sheets.DateTimeFormats.Single(entry => entry.TabName == BacklogPhase.Backlog.TabName());

            // Unbounded, so re-running init is also the repair for rows that arrived unformatted.
            Assert.Equal(1, call.StartRowIndex);
            Assert.Null(call.EndRowIndex);
            Assert.Equal(SheetTime.DisplayPattern, call.Pattern);
            Assert.NotEmpty(call.ColumnIndexes);
        }

        [Fact]
        public async Task AddAsync_NewTicket_FormatsTheAppendedRowsOwnTimestampCells()
        {
            // Sheets *inserts* an appended row rather than writing into the formatted blank one
            // below the data, and an inserted row carries no formatting — so the column-wide
            // format from init never reaches it and Created/Updated render as five-digit serials.
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            foreach (var column in new[] { SheetSchema.Created, SheetSchema.Updated })
            {
                var covering = backlog.DateTimeFormatsCovering(BacklogPhase.Backlog, ticket.Id, column);
                Assert.Contains(covering, call => call.EndRowIndex.HasValue);
            }
        }

        [Fact]
        public async Task StartAsync_TicketMovesToAnotherTab_FormatsTheTimestampCellsOfTheRowItLandsIn()
        {
            // A transition is an append onto the destination tab, so it inherits the same problem.
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            foreach (var column in new[] { SheetSchema.Created, SheetSchema.StartedAt })
            {
                var covering = backlog.DateTimeFormatsCovering(BacklogPhase.InProgress, ticket.Id, column);
                Assert.Contains(covering, call => call.EndRowIndex.HasValue);
            }
        }

        [Theory]
        [InlineData("UTC")]
        [InlineData("America/New_York")]
        [InlineData("Australia/Sydney")]
        public async Task GetAsync_AnyConfiguredZone_ReturnsTheInstantThatWasWritten(string timeZoneId)
        {
            var created = new DateTimeOffset(2026, 8, 1, 12, 34, 0, TimeSpan.Zero);
            var backlog = await TestBacklog.CreateAsync(created, timeZoneId: timeZoneId);

            var ticket = await backlog.AddAsync("Something");
            var reloaded = await backlog.Store.GetAsync(ticket.Id);

            // Seconds are below the display resolution, so compare to the minute.
            Assert.Equal(created.ToUnixTimeSeconds() / 60, reloaded!.Created.ToUnixTimeSeconds() / 60);
        }

        [Fact]
        public async Task DoctorAsync_ConfiguredZoneNoLongerMatchesTheSpreadsheet_ReportsTheMismatchWithoutMovingInstants()
        {
            // The whole reason instants stay canonical: reinterpreting history on a config edit
            // would silently shift every lead and cycle time already recorded.
            var created = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            var backlog = await TestBacklog.CreateAsync(created, timeZoneId: "America/New_York");

            var ticket = await backlog.AddAsync("Something");
            var before = (await backlog.Store.GetAsync(ticket.Id))!.Created;

            var serialBefore = backlog.RawCell(BacklogPhase.Backlog, ticket.Id, SheetSchema.Created);

            // A serial is wall-clock, so re-pointing the zone without rewriting cells WOULD move
            // it — which is exactly why doctor treats a mismatch as an error rather than shrugging.
            await backlog.SetConfigAsync(BacklogSettings.TimeZoneKey, "Australia/Sydney");
            var report = await backlog.FreshStore().DoctorAsync();

            Assert.Contains(report.Issues, issue => issue.Kind == "timezone-mismatch");
            Assert.Equal(serialBefore, backlog.RawCell(BacklogPhase.Backlog, ticket.Id, SheetSchema.Created));
            Assert.Equal(created, before);
        }

        [Fact]
        public async Task DoctorAsync_BothTimeZonesAgree_ReportsNoMismatch()
        {
            var backlog = await TestBacklog.CreateAsync(timeZoneId: "Europe/London");
            await backlog.AddAsync("Something");

            var report = await backlog.Store.DoctorAsync();

            Assert.DoesNotContain(report.Issues, issue => issue.Kind == "timezone-mismatch");
        }

        [Fact]
        public async Task AddAsync_TimeZoneConfigured_WritesTheActivityLogInThatZone()
        {
            var created = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            var backlog = await TestBacklog.CreateAsync(created, timeZoneId: "America/New_York");

            var ticket = await backlog.AddAsync("Something");
            var document = backlog.Drive.ContentOf(ticket.DocId!);

            // 12:00Z is 08:00 EDT.
            Assert.Contains("2026-08-01 08:00 -04:00 — created", document);
        }
    }

    public class ReindexTests
    {
        [Fact]
        public async Task ReindexAsync_AHumanEditedTheDocument_TakesContentFromItAndKeepsLifecycleFromTheSheet()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Original title");

            await backlog.Store.StartAsync(ticket.Id, "jason", false);
            var startedAt = (await backlog.Store.GetAsync(ticket.Id))!.StartedAt;

            // Simulate a human editing the document in Drive.
            var document = TicketDocument.Parse(backlog.Drive.ContentOf(ticket.DocId!));
            document.Ticket.Title = "Retitled by a human";
            document.Ticket.Score.BusinessValue = 13;
            await backlog.Drive.UpdateDocAsync(ticket.DocId!, document.Serialize());

            await backlog.Store.ReindexAsync();
            var repaired = await backlog.Store.GetAsync(ticket.Id);

            Assert.Equal("Retitled by a human", repaired!.Title);
            Assert.Equal(13, repaired.Score.BusinessValue);

            // The document carries no lifecycle fields, so these must have survived from the row
            // rather than been blanked by the merge.
            Assert.Equal(BacklogPhase.InProgress, repaired.Phase);
            Assert.Equal(startedAt, repaired.StartedAt);
        }

        [Fact]
        public async Task ReindexAsync_Always_TakesCreatedAndUpdatedFromDriveMetadata()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Something");

            var driveCreated = new DateTimeOffset(2025, 1, 2, 3, 4, 0, TimeSpan.Zero);
            var driveModified = new DateTimeOffset(2025, 6, 7, 8, 9, 0, TimeSpan.Zero);
            backlog.Drive.SetTimestamps(ticket.DocId!, driveCreated, driveModified);

            await backlog.Store.ReindexAsync();
            var repaired = await backlog.Store.GetAsync(ticket.Id);

            // Drive is authoritative for these two, and its modifiedTime also catches a human
            // editing the document directly — which a field we maintain would miss.
            Assert.Equal(driveCreated, repaired!.Created);
            Assert.Equal(driveModified, repaired.Updated);
        }

        [Fact]
        public async Task DoctorAsync_DocumentAndRowDisagree_ReportsTheDrift()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Original title");

            var document = TicketDocument.Parse(backlog.Drive.ContentOf(ticket.DocId!));
            document.Ticket.Title = "Changed underneath";
            await backlog.Drive.UpdateDocAsync(ticket.DocId!, document.Serialize());

            var report = await backlog.Store.DoctorAsync();

            Assert.Contains(report.Issues, issue => issue.Kind == "drift" && issue.Detail.Contains("title"));
        }
    }
}

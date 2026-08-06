namespace Noogen.Backlog.Tests
{
    public class TimeZoneIntegrationTests
    {
        [Fact]
        public async Task Init_stamps_the_timezone_onto_the_spreadsheet_and_the_config_tab()
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
        public async Task Timestamp_cells_are_numeric_serials_not_text()
        {
            var backlog = await TestBacklog.CreateAsync(timeZoneId: "America/New_York");
            var ticket = await backlog.AddAsync("Something");

            var cell = backlog.RawCell(BacklogPhase.Backlog, ticket.Id, SheetSchema.Created);

            Assert.IsType<double>(cell);
            Assert.True((double)cell > 40000);   // somewhere this century, not a stray 0
        }

        [Fact]
        public async Task Timestamp_columns_get_a_date_time_format_not_a_text_one()
        {
            var backlog = await TestBacklog.CreateAsync();

            Assert.NotEmpty(backlog.Sheets.DateTimeFormattedColumns);
            Assert.Contains(backlog.Sheets.DateTimeFormattedColumns, entry => entry.StartsWith("Backlog!"));
        }

        [Theory]
        [InlineData("UTC")]
        [InlineData("America/New_York")]
        [InlineData("Australia/Sydney")]
        public async Task Instants_survive_a_write_and_read_in_any_configured_zone(string timeZoneId)
        {
            var created = new DateTimeOffset(2026, 8, 1, 12, 34, 0, TimeSpan.Zero);
            var backlog = await TestBacklog.CreateAsync(created, timeZoneId: timeZoneId);

            var ticket = await backlog.AddAsync("Something");
            var reloaded = await backlog.Store.GetAsync(ticket.Id);

            // Seconds are below the display resolution, so compare to the minute.
            Assert.Equal(created.ToUnixTimeSeconds() / 60, reloaded!.Created.ToUnixTimeSeconds() / 60);
        }

        [Fact]
        public async Task Changing_the_timezone_does_not_move_existing_instants()
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
        public async Task Doctor_is_quiet_when_the_two_timezones_agree()
        {
            var backlog = await TestBacklog.CreateAsync(timeZoneId: "Europe/London");
            await backlog.AddAsync("Something");

            var report = await backlog.Store.DoctorAsync();

            Assert.DoesNotContain(report.Issues, issue => issue.Kind == "timezone-mismatch");
        }

        [Fact]
        public async Task Activity_log_is_written_in_the_configured_zone()
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
        public async Task Takes_content_from_the_document_and_keeps_lifecycle_from_the_sheet()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Original title");

            await backlog.Store.StartAsync(ticket.Id, "jason", false);
            var startedAt = (await backlog.Store.GetAsync(ticket.Id))!.StartedAt;

            // Simulate a human editing the document in Drive.
            var document = TicketDocument.Parse(backlog.Drive.ContentOf(ticket.DocId!));
            document.Ticket.Title = "Retitled by a human";
            document.Ticket.Score.BusinessValue = 13;
            await backlog.Drive.UpdateTextFileAsync(ticket.DocId!, document.Serialize(), "text/markdown");

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
        public async Task Takes_created_and_updated_from_drive_metadata()
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
        public async Task Reports_drift_between_the_sheet_and_the_document()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Original title");

            var document = TicketDocument.Parse(backlog.Drive.ContentOf(ticket.DocId!));
            document.Ticket.Title = "Changed underneath";
            await backlog.Drive.UpdateTextFileAsync(ticket.DocId!, document.Serialize(), "text/markdown");

            var report = await backlog.Store.DoctorAsync();

            Assert.Contains(report.Issues, issue => issue.Kind == "drift" && issue.Detail.Contains("title"));
        }
    }
}

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// What a human opens after `backlog init`: the tabs in the order work travels through them,
    /// and nothing left over from how Sheets happens to create a spreadsheet.
    /// </summary>
    public class BacklogInitializerTests
    {
        const string RootFolderId = "root";
        const string ExistingSpreadsheetId = "sheet-1";

        [Fact]
        public async Task RunAsync_SpreadsheetIsNew_LeavesNoEmptyDefaultTabBehind()
        {
            var backlog = NewSpreadsheet();

            var result = await backlog.Initializer.RunAsync(RootFolderId, timeZoneId: "UTC");

            Assert.True(result.CreatedSpreadsheet);
            Assert.DoesNotContain("Sheet1", await backlog.Sheets.ListTabsAsync(result.SpreadsheetId));
        }

        [Fact]
        public async Task RunAsync_SpreadsheetIsNew_OrdersTheTabsAlongTheKanbanFlow()
        {
            var backlog = NewSpreadsheet();

            var result = await backlog.Initializer.RunAsync(RootFolderId, timeZoneId: "UTC");

            Assert.Equal(
                ["Backlog", "In Progress", "Archive", "Config"],
                await backlog.Sheets.ListTabsAsync(result.SpreadsheetId));
        }

        [Fact]
        public async Task RunAsync_SpreadsheetIsNew_GivesTheAdoptedDefaultTabTheBacklogHeader()
        {
            // The default tab is renamed rather than deleted, so it still has to end up carrying
            // the schema every other Backlog tab does.
            var backlog = NewSpreadsheet();

            await backlog.Initializer.RunAsync(RootFolderId, timeZoneId: "UTC");

            var header = backlog.Sheets.Rows(BacklogPhase.Backlog.TabName())[0].Select(cell => cell?.ToString()).ToList();
            Assert.Equal(SheetSchema.Id, header[0]);
            Assert.Contains(SheetSchema.Wsjf, header);
        }

        [Fact]
        public async Task RunAsync_SpreadsheetIsNew_HeadsTheColumnsWithWordsRatherThanAbbreviations()
        {
            var backlog = NewSpreadsheet();

            await backlog.Initializer.RunAsync(RootFolderId, timeZoneId: "UTC");

            var header = backlog.Sheets.Rows(BacklogPhase.Backlog.TabName())[0].Select(cell => cell?.ToString()).ToList();

            Assert.Contains("Business Value", header);
            Assert.Contains("Risk & Opportunity", header);
            Assert.Contains("Cost of Delay", header);
            Assert.DoesNotContain("bv", header);
            Assert.DoesNotContain("rroe", header);
        }

        [Fact]
        public async Task RunAsync_TabAlreadyHasTheLegacyShortHeaders_LeavesThemExactlyAsTheyAre()
        {
            // A header row is a human's to edit. We understand the old names rather than
            // rewriting a row someone may have styled, filtered, or referenced from elsewhere.
            var legacy = new List<object> { "id", "title", "bv", "tc", "rroe", "size", "cod", "wsjf", "rank" };

            var sheets = new FakeSheetsGateway();
            sheets.SeedTab(BacklogPhase.Backlog.TabName());
            await sheets.UpdateValuesAsync(
                ExistingSpreadsheetId,
                Providers.GoogleWorkspace.A1.Anchor(BacklogPhase.Backlog.TabName(), 0, 0),
                [legacy]);

            var initializer = new BacklogInitializer(new FakeDriveGateway(), sheets);
            await initializer.RunAsync(RootFolderId, ExistingSpreadsheetId, "UTC");

            Assert.Equal(legacy, sheets.Rows(BacklogPhase.Backlog.TabName())[0]);
        }

        [Fact]
        public async Task RunAsync_TabsAreOutOfOrder_PutsThemBackInFlowOrder()
        {
            // Init doubles as a repair, and a tab added by an earlier version lands at the end.
            var sheets = new FakeSheetsGateway();
            sheets.SeedTab(SheetSchema.ConfigTabName);
            sheets.SeedTab(BacklogPhase.Archive.TabName());
            sheets.SeedTab(BacklogPhase.Backlog.TabName());

            var initializer = new BacklogInitializer(new FakeDriveGateway(), sheets);
            await initializer.RunAsync(RootFolderId, ExistingSpreadsheetId, "UTC");

            Assert.Equal(
                ["Backlog", "In Progress", "Archive", "Config"],
                await sheets.ListTabsAsync(ExistingSpreadsheetId));
        }

        [Fact]
        public async Task RunAsync_AHumanAddedTheirOwnTab_KeepsItBehindOursRatherThanTouchingIt()
        {
            var sheets = new FakeSheetsGateway();
            sheets.SeedTab("Notes");

            var initializer = new BacklogInitializer(new FakeDriveGateway(), sheets);
            await initializer.RunAsync(RootFolderId, ExistingSpreadsheetId, "UTC");

            Assert.Equal(
                ["Backlog", "In Progress", "Archive", "Config", "Notes"],
                await sheets.ListTabsAsync(ExistingSpreadsheetId));
        }

        [Fact]
        public async Task RunAsync_SpreadsheetAlreadyHadASingleBacklogTab_DoesNotAdoptItTwice()
        {
            // Only a *new* spreadsheet's lone unrecognised tab is adopted; an existing backlog
            // must come through a re-run unchanged.
            var sheets = new FakeSheetsGateway();
            sheets.SeedTab(BacklogPhase.Backlog.TabName());

            var initializer = new BacklogInitializer(new FakeDriveGateway(), sheets);
            await initializer.RunAsync(RootFolderId, ExistingSpreadsheetId, "UTC");

            var tabs = await sheets.ListTabsAsync(ExistingSpreadsheetId);
            Assert.Equal(BacklogInitializer.TabOrder, tabs);
        }

        class NewBacklog
        {
            public FakeSheetsGateway Sheets { get; set; } = null!;

            public BacklogInitializer Initializer { get; set; } = null!;
        }

        /// <summary>
        /// A spreadsheet as Sheets hands it over: one default tab, named for the creating
        /// account's locale.
        /// </summary>
        static NewBacklog NewSpreadsheet()
        {
            var sheets = new FakeSheetsGateway();
            sheets.SeedTab("Sheet1");

            return new NewBacklog
            {
                Sheets = sheets,
                Initializer = new BacklogInitializer(new FakeDriveGateway(), sheets)
            };
        }
    }
}

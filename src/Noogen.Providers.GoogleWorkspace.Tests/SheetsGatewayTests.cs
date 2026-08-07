using System.Text.Json;

namespace Noogen.Providers.GoogleWorkspace.Tests
{
    /// <summary>
    /// The gateway is a translator: our vocabulary in, Google's REST shapes out. What it puts on
    /// the wire is therefore the behaviour, so these tests run it against a stub transport and
    /// assert the request it actually built.
    /// </summary>
    public class SheetsGatewayTests
    {
        const string SpreadsheetId = "sheet-1";

        const string TabsResponse = """
            { "sheets": [ { "properties": { "sheetId": 0, "title": "Backlog" } },
                          { "properties": { "sheetId": 7, "title": "In Progress" } } ] }
            """;

        const string BatchUpdateResponse = """{ "spreadsheetId": "sheet-1", "replies": [ {} ] }""";

        [Fact]
        public async Task GetValuesAsync_Always_ReadsUnformattedValuesAndSerialDates()
        {
            // Invariant 6: a formatted read hands back locale-rendered text, so the same sheet
            // would parse differently on a European machine.
            var handler = StubHttpHandler.Returning("""{ "values": [ [ "id" ] ] }""");
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.GetValuesAsync(SpreadsheetId, "'Backlog'!A1:R2");

            Assert.Equal("UNFORMATTED_VALUE", handler.LastRequest.Parameter("valueRenderOption"));
            Assert.Equal("SERIAL_NUMBER", handler.LastRequest.Parameter("dateTimeRenderOption"));
        }

        [Fact]
        public async Task GetValuesAsync_RangeHasValues_ReturnsThemAsAGrid()
        {
            var handler = StubHttpHandler.Returning("""{ "values": [ [ "id", "title" ], [ "NG-1", "Do a thing" ] ] }""");
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            var values = await gateway.GetValuesAsync(SpreadsheetId, "'Backlog'!A1:B2");

            Assert.Equal(2, values.Count);
            Assert.Equal("NG-1", values[1][0]);
        }

        [Fact]
        public async Task GetValuesAsync_RangeIsEmpty_ReturnsAnEmptyGridRatherThanNull()
        {
            // Sheets omits "values" entirely for an empty range; every caller would otherwise
            // need a null check.
            var handler = StubHttpHandler.Returning("""{ "range": "'Backlog'!A1:R1" }""");
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            Assert.Empty(await gateway.GetValuesAsync(SpreadsheetId, "'Backlog'!A1:R1"));
        }

        [Fact]
        public async Task UpdateValuesAsync_Always_WritesUserEnteredSoFormulaStringsStayFormulas()
        {
            // Invariant 3: the Backlog tab's cod/wsjf/rank cells are live formulas. RAW would
            // store them as text.
            var handler = StubHttpHandler.Returning("{}");
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.UpdateValuesAsync(SpreadsheetId, "'Backlog'!A2", [["=IF(A2>0,1,0)"]]);

            Assert.Equal("USER_ENTERED", handler.LastRequest.Parameter("valueInputOption"));
            Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);
        }

        [Fact]
        public async Task AppendRowAsync_Always_InsertsRowsRatherThanOverwritingTrailingCells()
        {
            var handler = StubHttpHandler.Returning("""{ "updates": { "updatedRange": "'Backlog'!A12:R12" } }""");
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.AppendRowAsync(SpreadsheetId, "Backlog", ["NG-1"]);

            Assert.Equal("INSERT_ROWS", handler.LastRequest.Parameter("insertDataOption"));
            Assert.Equal("USER_ENTERED", handler.LastRequest.Parameter("valueInputOption"));
        }

        [Fact]
        public async Task AppendRowAsync_SheetsReportsTheUpdatedRange_ReturnsTheZeroBasedRowIndex()
        {
            var handler = StubHttpHandler.Returning("""{ "updates": { "updatedRange": "'Backlog'!A12:R12" } }""");
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            Assert.Equal(11, await gateway.AppendRowAsync(SpreadsheetId, "Backlog", ["NG-1"]));
        }

        [Theory]
        [InlineData("'Backlog'!A12:R12", 11)]
        [InlineData("Backlog!A2:R2", 1)]
        [InlineData("'In Progress'!B7", 6)]
        [InlineData("A1:R1", 0)]
        public void ParseFirstRowIndex_AnUpdatedRange_ReturnsTheZeroBasedFirstRow(string updatedRange, int expected) =>
            Assert.Equal(expected, SheetsGateway.ParseFirstRowIndex(updatedRange));

        [Fact]
        public void ParseFirstRowIndex_TabNameContainsDigits_TakesTheRowFromTheCellsNotTheName() =>
            // The tab name is dropped at the '!' first, so "2024 Backlog" cannot bleed into the row.
            Assert.Equal(4, SheetsGateway.ParseFirstRowIndex("'2024 Backlog'!A5:R5"));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ParseFirstRowIndex_NoUpdatedRangeReported_Throws(string? updatedRange) =>
            Assert.Throws<InvalidOperationException>(() => SheetsGateway.ParseFirstRowIndex(updatedRange));

        [Fact]
        public void ParseFirstRowIndex_RangeCarriesNoRowNumber_ThrowsNamingTheRange()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => SheetsGateway.ParseFirstRowIndex("'Backlog'!A:R"));

            Assert.Contains("'Backlog'!A:R", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task DeleteRowAsync_Always_DeletesExactlyOneRowFromTheNamedTab()
        {
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.DeleteRowAsync(SpreadsheetId, "In Progress", 5);

            var range = FirstBatchRequest(handler.LastRequest, "deleteDimension").GetProperty("range");
            Assert.Equal(7, range.GetProperty("sheetId").GetInt32());
            Assert.Equal("ROWS", range.GetProperty("dimension").GetString());
            Assert.Equal(5, range.GetProperty("startIndex").GetInt32());
            Assert.Equal(6, range.GetProperty("endIndex").GetInt32());
        }

        [Fact]
        public async Task DeleteRowAsync_TabIsNotInTheSpreadsheet_ThrowsNamingTheTab()
        {
            var handler = StubHttpHandler.Returning(TabsResponse);
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => gateway.DeleteRowAsync(SpreadsheetId, "Nowhere", 0));

            Assert.Contains("Nowhere", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task DeleteRowAsync_CalledTwiceForTheSameTab_ResolvesTheTabIdOnlyOnce()
        {
            // The tab id never changes, and a lookup is a whole extra round trip per row move.
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.DeleteRowAsync(SpreadsheetId, "Backlog", 1);
            await gateway.DeleteRowAsync(SpreadsheetId, "Backlog", 2);

            Assert.Single(handler.Requests, request => request.Method == HttpMethod.Get);
        }

        [Fact]
        public async Task SetCellLinkAsync_Always_StoresPlainTextCarryingARichTextLink()
        {
            // Deliberately not =HYPERLINK(): the stored value must stay the title so an ordinary
            // value read returns text rather than a formula.
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.SetCellLinkAsync(SpreadsheetId, "Backlog", 3, 1, "Do a thing", "https://drive/x");

            var cell = FirstBatchRequest(handler.LastRequest, "updateCells")
                .GetProperty("rows")[0].GetProperty("values")[0];

            Assert.Equal("Do a thing", cell.GetProperty("userEnteredValue").GetProperty("stringValue").GetString());
            Assert.Equal("https://drive/x", cell.GetProperty("textFormatRuns")[0].GetProperty("format").GetProperty("link").GetProperty("uri").GetString());
            Assert.DoesNotContain("HYPERLINK", handler.LastRequest.Body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SetCellLinkAsync_Always_TargetsTheCellByZeroBasedGridCoordinate()
        {
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.SetCellLinkAsync(SpreadsheetId, "Backlog", 3, 1, "Do a thing", "https://drive/x");

            var start = FirstBatchRequest(handler.LastRequest, "updateCells").GetProperty("start");
            Assert.Equal(3, start.GetProperty("rowIndex").GetInt32());
            Assert.Equal(1, start.GetProperty("columnIndex").GetInt32());
        }

        [Fact]
        public async Task ListTabsAsync_Always_ReturnsEveryTabTitle()
        {
            var handler = StubHttpHandler.Returning(TabsResponse);
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            Assert.Equal(["Backlog", "In Progress"], await gateway.ListTabsAsync(SpreadsheetId));
        }

        [Fact]
        public async Task EnsureTabAsync_TabAlreadyExists_AddsNothing()
        {
            var handler = StubHttpHandler.Returning(TabsResponse);
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.EnsureTabAsync(SpreadsheetId, "Backlog");

            Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        }

        [Fact]
        public async Task EnsureTabAsync_TabIsMissing_AddsItByName()
        {
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.EnsureTabAsync(SpreadsheetId, "Archive");

            Assert.Equal("Archive", FirstBatchRequest(handler.LastRequest, "addSheet").GetProperty("properties").GetProperty("title").GetString());
        }

        [Fact]
        public async Task RenameTabAsync_Always_UpdatesOnlyTheTitle()
        {
            // A wider field mask on updateSheetProperties would reset grid properties the tab
            // already carries — the frozen header among them.
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.RenameTabAsync(SpreadsheetId, "In Progress", "Doing");

            var request = FirstBatchRequest(handler.LastRequest, "updateSheetProperties");
            Assert.Equal(7, request.GetProperty("properties").GetProperty("sheetId").GetInt32());
            Assert.Equal("Doing", request.GetProperty("properties").GetProperty("title").GetString());
            Assert.Equal("title", request.GetProperty("fields").GetString());
        }

        [Fact]
        public async Task RenameTabAsync_TabIsUsedAfterwards_ResolvesItUnderTheNewNameWithoutAnotherLookup()
        {
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.RenameTabAsync(SpreadsheetId, "In Progress", "Doing");
            await gateway.DeleteRowAsync(SpreadsheetId, "Doing", 2);

            Assert.Single(handler.Requests, request => request.Method == HttpMethod.Get);
            Assert.Equal(7, FirstBatchRequest(handler.LastRequest, "deleteDimension").GetProperty("range").GetProperty("sheetId").GetInt32());
        }

        [Fact]
        public async Task SetTabOrderAsync_Always_MovesEachTabToItsPlaceInOneOrderedBatch()
        {
            // Requests in a batch apply in order, and Sheets reads each index against the order
            // before that request — placing tabs front to back is what keeps a plain index right.
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.SetTabOrderAsync(SpreadsheetId, ["In Progress", "Backlog"]);

            var requests = handler.LastRequest.Json().GetProperty("requests");
            Assert.Equal(2, requests.GetArrayLength());

            Assert.Equal(7, requests[0].GetProperty("updateSheetProperties").GetProperty("properties").GetProperty("sheetId").GetInt32());
            Assert.Equal(0, requests[0].GetProperty("updateSheetProperties").GetProperty("properties").GetProperty("index").GetInt32());
            Assert.Equal("index", requests[0].GetProperty("updateSheetProperties").GetProperty("fields").GetString());

            Assert.Equal(0, requests[1].GetProperty("updateSheetProperties").GetProperty("properties").GetProperty("sheetId").GetInt32());
            Assert.Equal(1, requests[1].GetProperty("updateSheetProperties").GetProperty("properties").GetProperty("index").GetInt32());
        }

        [Fact]
        public async Task SetTabOrderAsync_TabIsNotInTheSpreadsheet_SkipsItAndKeepsThePositionsContiguous()
        {
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.SetTabOrderAsync(SpreadsheetId, ["Backlog", "Nowhere", "In Progress"]);

            var requests = handler.LastRequest.Json().GetProperty("requests");
            Assert.Equal(2, requests.GetArrayLength());
            Assert.Equal(1, requests[1].GetProperty("updateSheetProperties").GetProperty("properties").GetProperty("index").GetInt32());
        }

        [Fact]
        public async Task SetTabOrderAsync_NoNamedTabIsPresent_SendsNothing()
        {
            var handler = StubHttpHandler.Returning(TabsResponse);
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.SetTabOrderAsync(SpreadsheetId, ["Nowhere"]);

            Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        }

        [Fact]
        public async Task FreezeHeaderAsync_Always_FreezesExactlyOneRow()
        {
            // Row 1 is the header the whole column resolution depends on; it has to stay visible.
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.FreezeHeaderAsync(SpreadsheetId, "Backlog");

            var properties = FirstBatchRequest(handler.LastRequest, "updateSheetProperties").GetProperty("properties");
            Assert.Equal(1, properties.GetProperty("gridProperties").GetProperty("frozenRowCount").GetInt32());
        }

        [Fact]
        public async Task HideColumnsAsync_Always_HidesTheHalfOpenColumnRange()
        {
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.HideColumnsAsync(SpreadsheetId, "Backlog", 12, 15);

            var request = FirstBatchRequest(handler.LastRequest, "updateDimensionProperties");
            Assert.Equal("COLUMNS", request.GetProperty("range").GetProperty("dimension").GetString());
            Assert.Equal(12, request.GetProperty("range").GetProperty("startIndex").GetInt32());
            Assert.Equal(15, request.GetProperty("range").GetProperty("endIndex").GetInt32());
            Assert.True(request.GetProperty("properties").GetProperty("hiddenByUser").GetBoolean());
        }

        [Fact]
        public async Task SetDateTimeFormatAsync_NoEndRow_LeavesTheRangeUnboundedBelowTheStart()
        {
            // Invariant 5: timestamps are real datetime serials, and a DATE_TIME format is what
            // makes them render as local times rather than as five-digit numbers. An omitted
            // endRowIndex is how the format reaches rows that do not exist yet.
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.SetDateTimeFormatAsync(SpreadsheetId, "Backlog", [4], 1, null, "yyyy-mm-dd hh:mm");

            var request = FirstBatchRequest(handler.LastRequest, "repeatCell");
            var range = request.GetProperty("range");

            Assert.Equal(4, range.GetProperty("startColumnIndex").GetInt32());
            Assert.Equal(5, range.GetProperty("endColumnIndex").GetInt32());
            Assert.Equal(1, range.GetProperty("startRowIndex").GetInt32());
            Assert.False(range.TryGetProperty("endRowIndex", out _));

            var format = request.GetProperty("cell").GetProperty("userEnteredFormat").GetProperty("numberFormat");
            Assert.Equal("DATE_TIME", format.GetProperty("type").GetString());
            Assert.Equal("yyyy-mm-dd hh:mm", format.GetProperty("pattern").GetString());
        }

        [Fact]
        public async Task SetDateTimeFormatAsync_EndRowGiven_FormatsThatHalfOpenRowRangeOnly()
        {
            // An appended row is inserted unformatted, so the row it landed in is reformatted on
            // its own; widening that to the column would rewrite formatting a human chose.
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.SetDateTimeFormatAsync(SpreadsheetId, "Backlog", [4], 11, 12, "yyyy-mm-dd hh:mm");

            var range = FirstBatchRequest(handler.LastRequest, "repeatCell").GetProperty("range");

            Assert.Equal(11, range.GetProperty("startRowIndex").GetInt32());
            Assert.Equal(12, range.GetProperty("endRowIndex").GetInt32());
        }

        [Fact]
        public async Task SetDateTimeFormatAsync_SeveralColumns_SendsOneRequestPerColumnInOneBatch()
        {
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.SetDateTimeFormatAsync(SpreadsheetId, "Backlog", [4, 12, 13], 1, null, "yyyy-mm-dd hh:mm");

            var requests = handler.LastRequest.Json().GetProperty("requests");

            Assert.Equal(3, requests.GetArrayLength());
            Assert.Equal(13, requests[2].GetProperty("repeatCell").GetProperty("range").GetProperty("startColumnIndex").GetInt32());
        }

        [Fact]
        public async Task SetDateTimeFormatAsync_NoColumns_SendsNothing()
        {
            // A tab a human stripped every timestamp column out of must not produce an empty
            // batchUpdate, which Sheets rejects.
            var handler = new StubHttpHandler(StubResponse.Json(TabsResponse), StubResponse.Json(BatchUpdateResponse));
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.SetDateTimeFormatAsync(SpreadsheetId, "Backlog", [], 1, null, "yyyy-mm-dd hh:mm");

            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task GetSpreadsheetTimeZoneAsync_SpreadsheetHasAZone_ReturnsIt()
        {
            var handler = StubHttpHandler.Returning("""{ "properties": { "timeZone": "America/New_York" } }""");
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            Assert.Equal("America/New_York", await gateway.GetSpreadsheetTimeZoneAsync(SpreadsheetId));
        }

        [Fact]
        public async Task GetSpreadsheetTimeZoneAsync_SpreadsheetReportsNoProperties_ReturnsNull()
        {
            var handler = StubHttpHandler.Returning("{}");
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            Assert.Null(await gateway.GetSpreadsheetTimeZoneAsync(SpreadsheetId));
        }

        [Fact]
        public async Task SetSpreadsheetTimeZoneAsync_Always_UpdatesOnlyTheTimeZoneProperty()
        {
            // Invariant 5: the spreadsheet's zone must be reconcilable with Config!timezone, and
            // a wider field mask would clobber unrelated spreadsheet properties.
            var handler = StubHttpHandler.Returning(BatchUpdateResponse);
            var gateway = new SheetsGateway(new StubSheetsClientFactory(handler));

            await gateway.SetSpreadsheetTimeZoneAsync(SpreadsheetId, "Europe/London");

            var request = FirstBatchRequest(handler.LastRequest, "updateSpreadsheetProperties");
            Assert.Equal("Europe/London", request.GetProperty("properties").GetProperty("timeZone").GetString());
            Assert.Equal("timeZone", request.GetProperty("fields").GetString());
        }

        static JsonElement FirstBatchRequest(RecordedRequest request, string requestName) =>
            request.Json().GetProperty("requests")[0].GetProperty(requestName);
    }
}

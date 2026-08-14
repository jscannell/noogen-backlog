using System.Net;

namespace Noogen.Providers.GoogleWorkspace.Tests
{
    /// <summary>
    /// Drive's query language is string-concatenated, and every call has to opt in to shared
    /// drives, so both are asserted on the wire rather than trusted.
    /// </summary>
    public class DriveGatewayTests
    {
        const string ParentId = "folder-1";

        [Fact]
        public async Task FindChildAsync_NameMatches_ReturnsTheFileId()
        {
            var handler = StubHttpHandler.Returning("""{ "files": [ { "id": "file-1", "name": "NG-1" } ] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            Assert.Equal("file-1", await gateway.FindChildAsync(ParentId, "NG-1", DriveGateway.DocumentMimeType));
        }

        [Fact]
        public async Task FindChildAsync_NothingMatches_ReturnsNull()
        {
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            Assert.Null(await gateway.FindChildAsync(ParentId, "NG-1", null));
        }

        [Fact]
        public async Task FindChildAsync_Always_ScopesTheQueryToTheParentAndExcludesTheTrash()
        {
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.FindChildAsync(ParentId, "NG-1", null);

            var query = handler.LastRequest.Parameter("q");
            Assert.Equal("'folder-1' in parents and name = 'NG-1' and trashed = false", query);
        }

        [Fact]
        public async Task FindChildAsync_MimeTypeGiven_ConstrainsTheQueryToIt()
        {
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.FindChildAsync(ParentId, "Backlog Index", DriveGateway.SpreadsheetMimeType);

            Assert.Contains($"mimeType = '{DriveGateway.SpreadsheetMimeType}'", handler.LastRequest.Parameter("q"), StringComparison.Ordinal);
        }

        [Fact]
        public async Task FindChildAsync_NameContainsAnApostrophe_EscapesItRatherThanClosingTheLiteral()
        {
            // A ticket title is untrusted input and reaches Drive's query language verbatim
            // otherwise. This is the same concern as escaping user text before a cell.
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.FindChildAsync(ParentId, "Jason's ticket", null);

            Assert.Contains(@"name = 'Jason\'s ticket'", handler.LastRequest.Parameter("q"), StringComparison.Ordinal);
        }

        [Fact]
        public async Task FindChildAsync_NameContainsABackslash_EscapesTheBackslashFirst()
        {
            // Escaping the quote first would leave the backslash free to escape our own escape.
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.FindChildAsync(ParentId, @"a\'b", null);

            Assert.Contains(@"name = 'a\\\'b'", handler.LastRequest.Parameter("q"), StringComparison.Ordinal);
        }

        [Fact]
        public async Task FindChildAsync_Always_AsksDriveToSearchSharedDrives()
        {
            // The backlog lives in a shared drive. Without both flags Drive silently searches only
            // My Drive and reports nothing found.
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.FindChildAsync(ParentId, "NG-1", null);

            Assert.Equal("true", handler.LastRequest.Parameter("supportsAllDrives"));
            Assert.Equal("true", handler.LastRequest.Parameter("includeItemsFromAllDrives"));
        }

        [Fact]
        public async Task ListChildrenAsync_ResponseIsPaged_FollowsEveryPage()
        {
            // doctor sweeps the whole ticket folder; stopping at the first page would report
            // healthy documents as missing.
            var handler = new StubHttpHandler(
                StubResponse.Json("""{ "nextPageToken": "page-2", "files": [ { "id": "a", "name": "NG-1" } ] }"""),
                StubResponse.Json("""{ "files": [ { "id": "b", "name": "NG-2" } ] }"""));

            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            var entries = await gateway.ListChildrenAsync(ParentId, null);

            Assert.Equal(["NG-1", "NG-2"], entries.Select(entry => entry.Name));
            Assert.Equal("page-2", handler.LastRequest.Parameter("pageToken"));
        }

        [Fact]
        public async Task ListChildrenAsync_NoMorePages_StopsRequesting()
        {
            var handler = StubHttpHandler.Returning("""{ "files": [ { "id": "a", "name": "NG-1" } ] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.ListChildrenAsync(ParentId, null);

            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task ListChildrenAsync_NoMimeType_ListsEveryUntrashedChild()
        {
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.ListChildrenAsync(ParentId, null);

            Assert.Equal("'folder-1' in parents and trashed = false", handler.LastRequest.Parameter("q"));
        }

        [Fact]
        public async Task SearchTextAsync_Always_QueriesTheFullTextIndexAndExcludesTheTrash()
        {
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.SearchTextAsync("rate limit", null, null);

            Assert.Equal("fullText contains 'rate limit' and trashed = false", handler.LastRequest.Parameter("q"));
        }

        [Fact]
        public async Task SearchTextAsync_MimeTypeGiven_ConstrainsTheQueryToIt()
        {
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.SearchTextAsync("rate limit", DriveGateway.DocumentMimeType, null);

            Assert.Contains($"mimeType = '{DriveGateway.DocumentMimeType}'", handler.LastRequest.Parameter("q"), StringComparison.Ordinal);
        }

        [Fact]
        public async Task SearchTextAsync_TextContainsAnApostrophe_EscapesItRatherThanClosingTheLiteral()
        {
            // The search string is the most directly user-supplied value this tool puts into a
            // Drive query — it is typed at the command line and goes straight through.
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.SearchTextAsync(@"Jason's a\b", null, null);

            Assert.Contains(@"fullText contains 'Jason\'s a\\b'", handler.LastRequest.Parameter("q"), StringComparison.Ordinal);
        }

        [Fact]
        public async Task SearchTextAsync_DriveIdGiven_ConfinesTheSweepToThatSharedDrive()
        {
            // The tool holds full Drive scope, so without this the query rummages through
            // everything the signed-in person can read.
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.SearchTextAsync("rate limit", null, "drive-9");

            Assert.Equal("drive", handler.LastRequest.Parameter("corpora"));
            Assert.Equal("drive-9", handler.LastRequest.Parameter("driveId"));
        }

        [Fact]
        public async Task SearchTextAsync_NoDriveId_LeavesTheCorpusUnrestricted()
        {
            // A backlog rooted in My Drive has no shared drive to name, and asking for corpora
            // without a driveId is an error rather than a wider search.
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.SearchTextAsync("rate limit", null, null);

            Assert.Null(handler.LastRequest.Parameter("corpora"));
            Assert.Null(handler.LastRequest.Parameter("driveId"));
        }

        [Fact]
        public async Task SearchTextAsync_Always_AsksDriveToSearchSharedDrives()
        {
            var handler = StubHttpHandler.Returning("""{ "files": [] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.SearchTextAsync("rate limit", null, null);

            Assert.Equal("true", handler.LastRequest.Parameter("supportsAllDrives"));
            Assert.Equal("true", handler.LastRequest.Parameter("includeItemsFromAllDrives"));
        }

        [Fact]
        public async Task SearchTextAsync_ResponseIsPaged_FollowsEveryPage()
        {
            // A broad term over a large backlog pages, and a hit dropped here reads as "no such
            // ticket" — the answer that makes somebody file a duplicate.
            var handler = new StubHttpHandler(
                StubResponse.Json("""{ "nextPageToken": "page-2", "files": [ { "id": "a", "name": "NG-1" } ] }"""),
                StubResponse.Json("""{ "files": [ { "id": "b", "name": "NG-2" } ] }"""));

            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            var entries = await gateway.SearchTextAsync("rate limit", null, null);

            Assert.Equal(["a", "b"], entries.Select(entry => entry.Id));
            Assert.Equal("page-2", handler.LastRequest.Parameter("pageToken"));
        }

        [Fact]
        public async Task GetDriveIdAsync_FileIsOnASharedDrive_ReturnsTheDriveId()
        {
            var handler = StubHttpHandler.Returning("""{ "driveId": "drive-9" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            Assert.Equal("drive-9", await gateway.GetDriveIdAsync(ParentId));
            Assert.Equal("driveId", handler.LastRequest.Parameter("fields"));
        }

        [Fact]
        public async Task GetDriveIdAsync_FileIsInMyDrive_ReturnsNull()
        {
            // Drive omits driveId entirely for a file that is not on a shared drive.
            var handler = StubHttpHandler.Returning("""{ "id": "folder-1" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            Assert.Null(await gateway.GetDriveIdAsync(ParentId));
        }

        [Fact]
        public async Task CreateFolderAsync_Always_CreatesAFolderUnderTheParent()
        {
            var handler = StubHttpHandler.Returning("""{ "id": "folder-2" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            var id = await gateway.CreateFolderAsync(ParentId, "Tickets");

            var body = handler.LastRequest.Json();
            Assert.Equal("folder-2", id);
            Assert.Equal("Tickets", body.GetProperty("name").GetString());
            Assert.Equal(DriveGateway.FolderMimeType, body.GetProperty("mimeType").GetString());
            Assert.Equal("folder-1", body.GetProperty("parents")[0].GetString());
        }

        [Fact]
        public async Task CreateSpreadsheetAsync_Always_CreatesANativeSheetNotAnUploadedFile()
        {
            var handler = StubHttpHandler.Returning("""{ "id": "sheet-1" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.CreateSpreadsheetAsync(ParentId, "Backlog Index");

            Assert.Equal(DriveGateway.SpreadsheetMimeType, handler.LastRequest.Json().GetProperty("mimeType").GetString());
        }

        [Fact]
        public async Task CreateSpreadsheetAsync_Always_SupportsSharedDrives()
        {
            var handler = StubHttpHandler.Returning("""{ "id": "sheet-1" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.CreateSpreadsheetAsync(ParentId, "Backlog Index");

            Assert.Equal("true", handler.LastRequest.Parameter("supportsAllDrives"));
        }

        static StubHttpHandler Upload(string responseJson) => new(
            StubResponse.Status(HttpStatusCode.OK).WithHeader("Location", "https://upload.example.invalid/session"),
            StubResponse.Json(responseJson));

        [Fact]
        public async Task CreateDocAsync_Always_UploadsTheMarkdownAndReturnsTheNewId()
        {
            var handler = Upload("""{ "id": "file-9" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            var id = await gateway.CreateDocAsync(ParentId, "NG-1", "# NG-1");

            Assert.Equal("file-9", id);
            Assert.Equal("# NG-1", handler.LastRequest.Body);
        }

        [Fact]
        public async Task CreateDocAsync_Always_AsksDriveToConvertTheUploadIntoANativeDoc()
        {
            // The whole point of the Doc format: a native Doc's webViewLink is a docs.google.com
            // URL that renders, where a text/markdown file's is a Drive preview of raw text. Send
            // the same type in both places and Drive stores markdown instead of converting it.
            var handler = Upload("""{ "id": "file-9" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.CreateDocAsync(ParentId, "NG-1", "# NG-1");

            var initiation = handler.Requests[0];
            Assert.Equal(DriveGateway.DocumentMimeType, initiation.Json().GetProperty("mimeType").GetString());
            Assert.Equal(DriveGateway.MarkdownMimeType, initiation.Header("X-Upload-Content-Type"));
        }

        [Fact]
        public async Task CreateDocAsync_Always_NamesTheFileAndParentsItInTheInitialMetadata()
        {
            var handler = Upload("""{ "id": "file-9" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.CreateDocAsync(ParentId, "NG-1", "# NG-1");

            var metadata = handler.Requests[0].Json();
            Assert.Equal("NG-1", metadata.GetProperty("name").GetString());
            Assert.Equal("folder-1", metadata.GetProperty("parents")[0].GetString());
        }

        [Fact]
        public async Task CreateDocAsync_Always_SupportsSharedDrives()
        {
            var handler = Upload("""{ "id": "file-9" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.CreateDocAsync(ParentId, "NG-1", "# NG-1");

            Assert.Equal("true", handler.Requests[0].Parameter("supportsAllDrives"));
        }

        [Fact]
        public async Task CreateDocAsync_UploadFails_Throws()
        {
            var handler = new StubHttpHandler(StubResponse.Status(HttpStatusCode.Forbidden));
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await Assert.ThrowsAnyAsync<Exception>(() => gateway.CreateDocAsync(ParentId, "NG-1", "# NG-1"));
        }

        [Fact]
        public async Task ReadDocAsync_Always_ReturnsTheContentAsUtf8()
        {
            var handler = new StubHttpHandler(StubResponse.Text("# NG-1\n\nEmoji survive: ✓"));
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            Assert.Equal("# NG-1\n\nEmoji survive: ✓", await gateway.ReadDocAsync("file-1"));
        }

        [Fact]
        public async Task ReadDocAsync_Always_ExportsAsMarkdownRatherThanDownloadingTheMedia()
        {
            // A Doc has no stored bytes: alt=media on one fails outright. The content only exists
            // in whichever format Drive is asked to render, so the export mimeType is the contract.
            var handler = new StubHttpHandler(StubResponse.Text("# NG-1"));
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.ReadDocAsync("file-1");

            Assert.EndsWith("/files/file-1/export", handler.LastRequest.Path, StringComparison.Ordinal);
            Assert.Equal(DriveGateway.MarkdownMimeType, handler.LastRequest.Parameter("mimeType"));
        }

        [Fact]
        public async Task UpdateDocAsync_Always_SendsTheNewMarkdownForTheSameFile()
        {
            var handler = Upload("""{ "id": "file-1" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.UpdateDocAsync("file-1", "# NG-1 edited");

            Assert.Equal("# NG-1 edited", handler.LastRequest.Body);
            Assert.Contains("file-1", handler.Requests[0].Path, StringComparison.Ordinal);
        }

        [Fact]
        public async Task UpdateDocAsync_Always_KeepsTheFileADocRatherThanReplacingItWithMarkdown()
        {
            // Without restating the target type, an update that uploads text/markdown turns the
            // Doc back into a markdown file — and the Sheet's link silently stops rendering.
            var handler = Upload("""{ "id": "file-1" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.UpdateDocAsync("file-1", "# NG-1 edited");

            var initiation = handler.Requests[0];
            Assert.Equal(DriveGateway.DocumentMimeType, initiation.Json().GetProperty("mimeType").GetString());
            Assert.Equal(DriveGateway.MarkdownMimeType, initiation.Header("X-Upload-Content-Type"));
        }

        [Fact]
        public async Task MoveAsync_Always_ReParentsTheFileWithoutTrashingIt()
        {
            // Invariant 11: archive, never delete. Moving is a parent swap, and nothing here may
            // ever issue a delete or set trashed.
            var handler = StubHttpHandler.Returning("""{ "id": "file-1", "parents": [ "folder-2" ] }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.MoveAsync("file-1", "folder-2", "folder-1");

            Assert.Equal("folder-2", handler.LastRequest.Parameter("addParents"));
            Assert.Equal("folder-1", handler.LastRequest.Parameter("removeParents"));
            Assert.NotEqual(HttpMethod.Delete, handler.LastRequest.Method);
            Assert.DoesNotContain("trashed", handler.LastRequest.Body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task MoveAsync_Always_SupportsSharedDrives()
        {
            var handler = StubHttpHandler.Returning("""{ "id": "file-1" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            await gateway.MoveAsync("file-1", "folder-2", "folder-1");

            Assert.Equal("true", handler.LastRequest.Parameter("supportsAllDrives"));
        }

        [Fact]
        public async Task GetWebViewLinkAsync_Always_ReturnsTheLinkDriveReports()
        {
            var handler = StubHttpHandler.Returning("""{ "webViewLink": "https://drive.google.com/file/d/file-1/view" }""");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            Assert.Equal("https://drive.google.com/file/d/file-1/view", await gateway.GetWebViewLinkAsync("file-1"));
        }

        [Fact]
        public async Task GetTimestampsAsync_Always_ReadsCreatedAndModifiedFromDrive()
        {
            // Invariant 8: created and updated are Drive's, not a document field a human maintains.
            var handler = StubHttpHandler.Returning(
                """{ "createdTime": "2026-01-02T03:04:05Z", "modifiedTime": "2026-02-03T04:05:06Z" }""");

            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            var times = await gateway.GetTimestampsAsync("file-1");

            Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), times.CreatedTime);
            Assert.Equal(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), times.ModifiedTime);
        }

        [Fact]
        public async Task GetTimestampsAsync_DriveReportsNoTimes_ReturnsNulls()
        {
            var handler = StubHttpHandler.Returning("{}");
            var gateway = new DriveGateway(new StubDriveClientFactory(handler));

            var times = await gateway.GetTimestampsAsync("file-1");

            Assert.Null(times.CreatedTime);
            Assert.Null(times.ModifiedTime);
        }
    }
}

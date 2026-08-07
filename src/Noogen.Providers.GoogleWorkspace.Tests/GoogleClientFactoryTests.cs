using System.Net;
using Google;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;

namespace Noogen.Providers.GoogleWorkspace.Tests
{
    public class GoogleClientFactoryTests
    {
        [Fact]
        public void Constructor_Always_DefersCreatingTheServiceUntilItIsAskedFor()
        {
            var factory = new CountingDriveClientFactory(new StubCredential());

            Assert.Equal(0, factory.CreateCount);
        }

        [Fact]
        public void GetService_CalledTwice_CreatesTheServiceOnlyOnce()
        {
            // Each service owns an HTTP client; rebuilding one per call would discard connection
            // reuse and re-run credential initialisation on every request.
            var factory = new CountingDriveClientFactory(new StubCredential());

            var first = factory.GetService();
            var second = factory.GetService();

            Assert.Same(first, second);
            Assert.Equal(1, factory.CreateCount);
        }

        [Fact]
        public void GetService_Always_InitializesTheHttpClientWithTheResolvedCredential()
        {
            // The factory takes an already-resolved credential rather than resolving one itself:
            // resolution can need I/O and, for a new user, a browser, so it happens once at
            // startup instead of lazily from inside a request.
            var credential = new StubCredential();
            var factory = new CountingDriveClientFactory(credential);

            factory.GetService();

            Assert.Equal(1, credential.InitializeCount);
        }

        [Fact]
        public void Create_Always_IdentifiesTheToolToDriveByApplicationName()
        {
            IDriveClientFactory factory = new DriveClientFactory(new StubCredential());

            Assert.Equal("Noogen.Backlog", factory.Create().ApplicationName);
        }

        [Fact]
        public void Create_Always_IdentifiesTheToolToSheetsByApplicationName()
        {
            ISheetsClientFactory factory = new SheetsClientFactory(new StubCredential());

            Assert.Equal("Noogen.Backlog", factory.Create().ApplicationName);
        }

        [Fact]
        public void Create_ApplicationNameGiven_UsesItInsteadOfTheDefault()
        {
            IDriveClientFactory factory = new DriveClientFactory(new StubCredential(), "Noogen.Agent");

            Assert.Equal("Noogen.Agent", factory.Create().ApplicationName);
        }

        [Fact]
        public void Create_CalledTwice_ReturnsTheSameSheetsService()
        {
            ISheetsClientFactory factory = new SheetsClientFactory(new StubCredential());

            Assert.Same(factory.Create(), factory.Create());
        }

        [Fact]
        public async Task Create_RateLimitedRequest_IsRetriedRatherThanFailing()
        {
            // The whole point of the handler is that nothing above the gateways has to know: a
            // 429 costs a wait, not an error.
            var transport = new StubHttpHandler(
                StubResponse.Status(HttpStatusCode.TooManyRequests),
                StubResponse.Json("""{"values":[["Title"]]}"""));

            var scheduler = new RecordingRetryScheduler();
            var factory = new StubbedSheetsClientFactory(transport, new RateLimitRetryHandler(scheduler: scheduler));

            var response = await factory.GetService().Spreadsheets.Values.Get("sheet-1", "Backlog!A1:A1").ExecuteAsync();

            Assert.Equal("Title", response.Values[0][0]);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Single(scheduler.Waits);
        }

        [Fact]
        public async Task Create_RateLimitedEveryTime_StopsAtTheAttemptLimit()
        {
            // The retry loop lives in the message handler and stops at NumTries, which defaults
            // to 3 — attaching the handler has to raise it or the backoff is unreachable.
            var transport = new StubHttpHandler(StubResponse.Status(HttpStatusCode.TooManyRequests));

            var scheduler = new RecordingRetryScheduler();
            var factory = new StubbedSheetsClientFactory(transport, new RateLimitRetryHandler(scheduler: scheduler, maxAttempts: 4));

            await Assert.ThrowsAsync<GoogleApiException>(
                () => factory.GetService().Spreadsheets.Values.Get("sheet-1", "Backlog!A1:A1").ExecuteAsync());

            Assert.Equal(4, transport.Requests.Count);
            Assert.Equal(3, scheduler.Waits.Count);
        }

        /// <summary>
        /// Builds its service over the stub transport, so the retry the base class attaches is
        /// exercised against recorded requests rather than against Google.
        /// </summary>
        class StubbedSheetsClientFactory : GoogleClientFactory<SheetsService>
        {
            readonly HttpMessageHandler _transport;

            public StubbedSheetsClientFactory(HttpMessageHandler transport, RateLimitRetryHandler retry)
                : base(new StubCredential(), "Noogen.Backlog.Tests", retry)
            {
                _transport = transport;
            }

            protected override SheetsService Create(BaseClientService.Initializer initializer) =>
                StubGoogle.SheetsService(_transport);
        }

        class CountingDriveClientFactory : GoogleClientFactory<DriveService>
        {
            public CountingDriveClientFactory(IConfigurableHttpClientInitializer credential)
                : base(credential, "Noogen.Backlog.Tests")
            {
            }

            public int CreateCount { get; private set; }

            protected override DriveService Create(BaseClientService.Initializer initializer)
            {
                CreateCount++;
                return new DriveService(initializer);
            }
        }
    }

    public class GoogleWorkspaceScopesTests
    {
        [Fact]
        public void Drive_Always_IsTheFullDriveScopeNotTheAppCreatedFilesOne()
        {
            // drive.file only reaches files this install created, so a second person could not
            // read the index or anyone else's tickets. A shared backlog needs shared visibility.
            Assert.Equal("https://www.googleapis.com/auth/drive", GoogleWorkspaceScopes.Drive);
            Assert.DoesNotContain("drive.file", GoogleWorkspaceScopes.Drive, StringComparison.Ordinal);
        }

        [Fact]
        public void All_Always_CoversDriveSheetsAndEnoughIdentityToNameTheUser() =>
            Assert.Equal(
                [GoogleWorkspaceScopes.Drive, GoogleWorkspaceScopes.Spreadsheets, GoogleWorkspaceScopes.OpenId, GoogleWorkspaceScopes.Email],
                GoogleWorkspaceScopes.All);
    }
}

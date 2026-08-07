namespace Noogen.Providers.GoogleWorkspace.Tests
{
    /// <summary>
    /// Resolution order is environment, then a local file, then whatever the build embedded — an
    /// override must beat a default. The embedded half of that lives with the CLI, which is the
    /// assembly the build bakes the client into.
    /// </summary>
    public class OAuthClientSettingsTests : IDisposable
    {
        readonly TemporaryDirectory _directory = new("noogen-oauth");
        readonly string? _originalClientId;
        readonly string? _originalClientSecret;

        public OAuthClientSettingsTests()
        {
            // A developer running the suite may have the real client in their environment, and it
            // would otherwise win over every file these tests write.
            _originalClientId = Environment.GetEnvironmentVariable(OAuthClientSettings.ClientIdEnvironmentVariable);
            _originalClientSecret = Environment.GetEnvironmentVariable(OAuthClientSettings.ClientSecretEnvironmentVariable);

            SetEnvironment(null, null);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            SetEnvironment(_originalClientId, _originalClientSecret);
            _directory.Dispose();
        }

        [Fact]
        public void Resolve_FileIsTheClientSecretJsonFromTheConsole_ReadsItUnedited()
        {
            // Saves everyone a transcription step, and transcription is where secrets get mangled.
            var path = WriteOAuthFile("""
                {
                  "installed": {
                    "client_id": "1234.apps.googleusercontent.com",
                    "client_secret": "GOCSPX-example",
                    "redirect_uris": ["http://localhost"]
                  }
                }
                """);

            var settings = OAuthClientSettings.Resolve(path);

            Assert.True(settings.IsConfigured);
            Assert.Equal("1234.apps.googleusercontent.com", settings.ClientId);
            Assert.Equal("GOCSPX-example", settings.ClientSecret);
        }

        [Fact]
        public void Resolve_FileUsesOurFlatShape_ReadsIt()
        {
            var path = WriteOAuthFile("""{ "clientId": "abc", "clientSecret": "def" }""");

            var settings = OAuthClientSettings.Resolve(path);

            Assert.Equal("abc", settings.ClientId);
            Assert.Equal("def", settings.ClientSecret);
        }

        [Fact]
        public void Resolve_FlatFileUsesSnakeCaseKeys_ReadsThemToo()
        {
            var path = WriteOAuthFile("""{ "client_id": "abc", "client_secret": "def" }""");

            Assert.Equal("abc", OAuthClientSettings.Resolve(path).ClientId);
        }

        [Fact]
        public void Resolve_FileWasRead_ReportsThePathAsTheSource()
        {
            // `backlog whoami` shows this, so nobody has to guess which client is in play.
            var path = WriteOAuthFile("""{ "clientId": "abc", "clientSecret": "def" }""");

            Assert.Equal(path, OAuthClientSettings.Resolve(path).Source);
        }

        [Fact]
        public void Resolve_NothingConfiguredAnywhere_IsNotConfigured()
        {
            var settings = OAuthClientSettings.Resolve(_directory.File("absent.json"));

            Assert.False(settings.IsConfigured);
            Assert.Equal("none", settings.Source);
        }

        [Fact]
        public void Resolve_NoFilePathGiven_IsNotConfigured() => Assert.False(OAuthClientSettings.Resolve().IsConfigured);

        [Fact]
        public void Resolve_AssemblyCarriesNoEmbeddedClient_IsNotConfigured() =>
            // A build without the gitignored oauth.json must still work — that is what a
            // contributor without the secret gets.
            Assert.False(OAuthClientSettings.Resolve(_directory.File("absent.json"), typeof(OAuthClientSettingsTests).Assembly).IsConfigured);

        [Fact]
        public void Resolve_EnvironmentAndFileBothSet_PrefersTheEnvironment()
        {
            // Lets a login script or CI supply the client without touching anyone's disk.
            var path = WriteOAuthFile("""{ "clientId": "from-file", "clientSecret": "from-file" }""");
            SetEnvironment("from-env", "from-env-secret");

            var settings = OAuthClientSettings.Resolve(path);

            Assert.Equal("from-env", settings.ClientId);
            Assert.Contains(OAuthClientSettings.ClientIdEnvironmentVariable, settings.Source, StringComparison.Ordinal);
        }

        [Fact]
        public void Resolve_EnvironmentHasOnlyTheClientId_FallsBackToTheFile()
        {
            // Half a client is not a client; falling back beats failing to authenticate later.
            var path = WriteOAuthFile("""{ "clientId": "from-file", "clientSecret": "from-file-secret" }""");
            SetEnvironment("from-env", null);

            Assert.Equal("from-file", OAuthClientSettings.Resolve(path).ClientId);
        }

        [Fact]
        public void Resolve_FileIsAWebApplicationClient_ThrowsNamingTheMistake()
        {
            // The single most likely wrong turn in the console, and it would otherwise surface
            // much later as an opaque consent-screen failure.
            var path = WriteOAuthFile("""
                { "web": { "client_id": "1234.apps.googleusercontent.com", "client_secret": "GOCSPX-example" } }
                """);

            var exception = Assert.Throws<OAuthClientInvalidException>(() => OAuthClientSettings.Resolve(path));

            Assert.Contains("Web application client", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Desktop app", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Resolve_FileIsNotJson_ThrowsSayingSo()
        {
            var path = WriteOAuthFile("{ not json");

            Assert.Contains("not valid JSON", Assert.Throws<OAuthClientInvalidException>(() => OAuthClientSettings.Resolve(path)).Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Resolve_FileShapeIsUnrecognised_ThrowsDescribingBothAcceptedShapes()
        {
            var path = WriteOAuthFile("""{ "somethingElse": true }""");

            var message = Assert.Throws<OAuthClientInvalidException>(() => OAuthClientSettings.Resolve(path)).Message;

            Assert.Contains("installed", message, StringComparison.Ordinal);
            Assert.Contains("clientId", message, StringComparison.Ordinal);
        }

        [Fact]
        public void Resolve_InstalledSectionIsIncomplete_ThrowsNamingTheMissingField()
        {
            var path = WriteOAuthFile("""{ "installed": { "client_id": "1234.apps.googleusercontent.com" } }""");

            Assert.Contains("client_secret", Assert.Throws<OAuthClientInvalidException>(() => OAuthClientSettings.Resolve(path)).Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Resolve_InvalidFile_NamesTheFileInTheError()
        {
            var path = WriteOAuthFile("{ not json");

            Assert.Contains(path, Assert.Throws<OAuthClientInvalidException>(() => OAuthClientSettings.Resolve(path)).Message, StringComparison.Ordinal);
        }

        [Fact]
        public void IsConfigured_OnlyOneHalfOfThePairIsSet_IsFalse() =>
            Assert.False(new OAuthClientSettings { ClientId = "abc" }.IsConfigured);

        [Fact]
        public void IsConfigured_BothHalvesAreWhitespace_IsFalse() =>
            Assert.False(new OAuthClientSettings { ClientId = " ", ClientSecret = " " }.IsConfigured);

        [Fact]
        public void ToClientSecrets_Always_CarriesBothHalvesToGoogle()
        {
            var secrets = new OAuthClientSettings { ClientId = "abc", ClientSecret = "def" }.ToClientSecrets();

            Assert.Equal("abc", secrets.ClientId);
            Assert.Equal("def", secrets.ClientSecret);
        }

        [Fact]
        public void OAuthClientNotConfiguredException_Always_ExplainsTheWholeSetup()
        {
            var message = new OAuthClientNotConfiguredException("C:\\x\\oauth.json").Message;

            Assert.Contains("Desktop app", message, StringComparison.Ordinal);
            Assert.Contains("Internal", message, StringComparison.Ordinal);
            Assert.Contains("C:\\x\\oauth.json", message, StringComparison.Ordinal);
            Assert.Contains(OAuthClientSettings.ClientIdEnvironmentVariable, message, StringComparison.Ordinal);
        }

        string WriteOAuthFile(string content)
        {
            var path = _directory.File("oauth.json");
            File.WriteAllText(path, content);

            return path;
        }

        static void SetEnvironment(string? clientId, string? clientSecret)
        {
            Environment.SetEnvironmentVariable(OAuthClientSettings.ClientIdEnvironmentVariable, clientId);
            Environment.SetEnvironmentVariable(OAuthClientSettings.ClientSecretEnvironmentVariable, clientSecret);
        }
    }
}

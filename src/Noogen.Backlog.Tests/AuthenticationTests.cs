using System.Runtime.InteropServices;
using Noogen.Backlog.Cli;
using Noogen.Providers.GoogleWorkspace;
using Noogen.Providers.GoogleWorkspace.Security;

namespace Noogen.Backlog.Tests
{
    public class CredentialPrecedenceTests
    {
        [Fact]
        public void An_explicit_service_account_key_wins()
        {
            // CI and automation set this deliberately; deliberate beats ambient.
            Assert.Equal(
                CredentialSource.ServiceAccountKey,
                GoogleCredentialResolver.Choose(hasServiceAccountKey: true, hasUserToken: true, allowApplicationDefault: true));
        }

        [Fact]
        public void A_signed_in_user_beats_application_default()
        {
            // The person at the keyboard, not whatever gcloud happens to be pointed at.
            Assert.Equal(
                CredentialSource.UserOAuth,
                GoogleCredentialResolver.Choose(hasServiceAccountKey: false, hasUserToken: true, allowApplicationDefault: true));
        }

        [Fact]
        public void Application_default_is_the_last_resort()
        {
            // Present for Workload Identity in GKE, where it is the only correct answer.
            Assert.Equal(
                CredentialSource.ApplicationDefault,
                GoogleCredentialResolver.Choose(hasServiceAccountKey: false, hasUserToken: false, allowApplicationDefault: true));
        }

        [Fact]
        public void Nothing_configured_reports_none_rather_than_guessing()
        {
            Assert.Equal(
                CredentialSource.None,
                GoogleCredentialResolver.Choose(hasServiceAccountKey: false, hasUserToken: false, allowApplicationDefault: false));
        }

        [Fact]
        public void Not_signed_in_says_what_to_run() =>
            Assert.Contains("backlog login", new NotSignedInException().Message);
    }

    public class OAuthClientSettingsTests : IDisposable
    {
        readonly string _directory = Path.Combine(Path.GetTempPath(), "noogen-oauth-" + Guid.NewGuid().ToString("N"));

        public OAuthClientSettingsTests() => Directory.CreateDirectory(_directory);

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        [Fact]
        public void Reads_the_client_secret_json_google_hands_you_unedited()
        {
            // Saves everyone a transcription step, and transcription is where secrets get mangled.
            var path = Path.Combine(_directory, "oauth.json");
            File.WriteAllText(path, """
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
        public void Reads_a_flat_shape_too()
        {
            var path = Path.Combine(_directory, "oauth.json");
            File.WriteAllText(path, """{ "clientId": "abc", "clientSecret": "def" }""");

            var settings = OAuthClientSettings.Resolve(path);

            Assert.Equal("abc", settings.ClientId);
            Assert.Equal("def", settings.ClientSecret);
        }

        [Fact]
        public void Is_not_configured_when_nothing_is_present() =>
            Assert.False(OAuthClientSettings.Resolve(Path.Combine(_directory, "absent.json")).IsConfigured);

        [Fact]
        public void Names_the_mistake_when_a_web_client_was_created_instead()
        {
            // The single most likely wrong turn in the console, and it would otherwise surface
            // much later as an opaque consent-screen failure.
            var path = Path.Combine(_directory, "oauth.json");
            File.WriteAllText(path, """
                { "web": { "client_id": "1234.apps.googleusercontent.com", "client_secret": "GOCSPX-example" } }
                """);

            var exception = Assert.Throws<OAuthClientInvalidException>(() => OAuthClientSettings.Resolve(path));

            Assert.Contains("Web application client", exception.Message);
            Assert.Contains("Desktop app", exception.Message);
        }

        [Fact]
        public void Names_the_mistake_when_the_json_is_malformed()
        {
            var path = Path.Combine(_directory, "oauth.json");
            File.WriteAllText(path, "{ not json");

            Assert.Contains("not valid JSON", Assert.Throws<OAuthClientInvalidException>(() => OAuthClientSettings.Resolve(path)).Message);
        }

        [Fact]
        public void Names_the_mistake_when_the_shape_is_unrecognised()
        {
            var path = Path.Combine(_directory, "oauth.json");
            File.WriteAllText(path, """{ "somethingElse": true }""");

            Assert.Contains("installed", Assert.Throws<OAuthClientInvalidException>(() => OAuthClientSettings.Resolve(path)).Message);
        }

        [Fact]
        public void Names_the_mistake_when_the_installed_section_is_incomplete()
        {
            var path = Path.Combine(_directory, "oauth.json");
            File.WriteAllText(path, """{ "installed": { "client_id": "1234.apps.googleusercontent.com" } }""");

            Assert.Contains("client_secret", Assert.Throws<OAuthClientInvalidException>(() => OAuthClientSettings.Resolve(path)).Message);
        }

        [Fact]
        public void An_embedded_client_is_found_without_any_file()
        {
            // Whether the CLI assembly carries one depends on whether the build had the gitignored
            // oauth.json, so assert the behaviour for whichever case this build is — both are
            // legitimate, and a contributor without the secret must still get a working build.
            var cli = typeof(Program).Assembly;
            var hasEmbedded = cli.GetManifestResourceNames()
                .Any(name => name.EndsWith(OAuthClientSettings.EmbeddedResourceName, StringComparison.OrdinalIgnoreCase));

            var settings = OAuthClientSettings.Resolve(Path.Combine(_directory, "absent.json"), cli);

            if (hasEmbedded)
            {
                Assert.True(settings.IsConfigured);
                Assert.Equal("built into this tool", settings.Source);
            }
            else
            {
                Assert.False(settings.IsConfigured);
            }
        }

        [Fact]
        public void A_local_file_overrides_the_embedded_default()
        {
            // An override should beat a default, so someone can point at a different client
            // without rebuilding the tool.
            var path = Path.Combine(_directory, "oauth.json");
            File.WriteAllText(path, """{ "clientId": "override", "clientSecret": "override-secret" }""");

            var settings = OAuthClientSettings.Resolve(path, typeof(Program).Assembly);

            Assert.Equal("override", settings.ClientId);
            Assert.Equal(path, settings.Source);
        }

        [Fact]
        public void An_assembly_with_no_embedded_client_resolves_to_nothing()
        {
            // The test assembly carries no oauth.json.
            var settings = OAuthClientSettings.Resolve(Path.Combine(_directory, "absent.json"), typeof(OAuthClientSettingsTests).Assembly);

            Assert.False(settings.IsConfigured);
            Assert.Equal("none", settings.Source);
        }

        [Fact]
        public void Environment_variables_win_over_the_file()
        {
            // Lets a login script or CI supply the client without touching anyone's disk.
            var path = Path.Combine(_directory, "oauth.json");
            File.WriteAllText(path, """{ "clientId": "from-file", "clientSecret": "from-file" }""");

            Environment.SetEnvironmentVariable(OAuthClientSettings.ClientIdEnvironmentVariable, "from-env");
            Environment.SetEnvironmentVariable(OAuthClientSettings.ClientSecretEnvironmentVariable, "from-env-secret");

            try
            {
                Assert.Equal("from-env", OAuthClientSettings.Resolve(path).ClientId);
            }
            finally
            {
                Environment.SetEnvironmentVariable(OAuthClientSettings.ClientIdEnvironmentVariable, null);
                Environment.SetEnvironmentVariable(OAuthClientSettings.ClientSecretEnvironmentVariable, null);
            }
        }

        [Fact]
        public void The_missing_client_error_explains_the_whole_setup()
        {
            var message = new OAuthClientNotConfiguredException("C:\\x\\oauth.json").Message;

            Assert.Contains("Desktop app", message);
            Assert.Contains("Internal", message);
            Assert.Contains("C:\\x\\oauth.json", message);
        }
    }

    public class TokenProtectionTests : IDisposable
    {
        readonly string _directory = Path.Combine(Path.GetTempPath(), "noogen-tokens-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        [Fact]
        public void The_platform_protector_is_os_backed_on_windows()
        {
            var protector = TokenProtector.ForCurrentPlatform();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.True(protector.IsOsBacked);
                Assert.Contains("DPAPI", protector.Description);
            }
            else
            {
                // Linux CI usually has no Secret Service, and falling back must be visible.
                Assert.NotNull(protector.Description);
            }
        }

        [Fact]
        public void A_protector_round_trips_its_own_ciphertext()
        {
            var protector = TokenProtector.ForCurrentPlatform();

            var ciphertext = protector.Protect("TokenResponse-someone@noogen.ai", "the-refresh-token");
            Assert.Equal("the-refresh-token", protector.Unprotect("TokenResponse-someone@noogen.ai", ciphertext));

            protector.Remove("TokenResponse-someone@noogen.ai");
        }

        [Fact]
        public void The_stored_form_does_not_contain_the_token_in_the_clear()
        {
            var protector = TokenProtector.ForCurrentPlatform();
            if (!protector.IsOsBacked)
                return;   // the plaintext fallback is honest about being plaintext

            var stored = protector.Protect("TokenResponse-x", "super-secret-refresh-token");

            // The whole point: a harvested file yields nothing readable.
            Assert.DoesNotContain("super-secret-refresh-token", stored);

            protector.Remove("TokenResponse-x");
        }

        [Fact]
        public void The_plaintext_fallback_declares_itself()
        {
            var protector = new PlaintextTokenProtector();

            Assert.False(protector.IsOsBacked);
            Assert.Contains("UNENCRYPTED", protector.Description);
        }

        [Fact]
        public async Task The_data_store_writes_nothing_readable_to_disk()
        {
            var protector = TokenProtector.ForCurrentPlatform();
            if (!protector.IsOsBacked)
                return;

            var store = new ProtectedDataStore(_directory, protector);
            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//super-secret-refresh" });

            var onDisk = Directory.GetFiles(_directory).Select(File.ReadAllText);
            Assert.All(onDisk, contents => Assert.DoesNotContain("1//super-secret-refresh", contents));
        }

        [Fact]
        public async Task The_data_store_round_trips_through_encryption()
        {
            var store = new ProtectedDataStore(_directory, TokenProtector.ForCurrentPlatform());
            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//abc" });

            var loaded = await store.GetAsync<FakeToken>("someone@noogen.ai");

            Assert.Equal("1//abc", loaded.RefreshToken);
            await store.DeleteAsync<FakeToken>("someone@noogen.ai");
        }

        [Fact]
        public async Task A_missing_entry_reads_as_default_not_an_exception()
        {
            var store = new ProtectedDataStore(_directory, TokenProtector.ForCurrentPlatform());
            Assert.Null(await store.GetAsync<FakeToken>("nobody@noogen.ai"));
        }

        [Fact]
        public async Task Ciphertext_from_another_machine_reads_as_no_credential()
        {
            // A blob copied from elsewhere cannot be unwrapped. The user must be told to sign in
            // again, not shown a cryptographic stack trace.
            var protector = TokenProtector.ForCurrentPlatform();
            if (!protector.IsOsBacked || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            var store = new ProtectedDataStore(_directory, protector);
            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//abc" });

            var file = Directory.GetFiles(_directory).Single();
            File.WriteAllText(file, Convert.ToBase64String("not a real dpapi blob"u8.ToArray()));

            Assert.Null(await store.GetAsync<FakeToken>("someone@noogen.ai"));
        }

        [Fact]
        public async Task Delete_removes_both_the_file_and_the_keystore_entry()
        {
            var store = new ProtectedDataStore(_directory, TokenProtector.ForCurrentPlatform());

            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//abc" });
            await store.DeleteAsync<FakeToken>("someone@noogen.ai");

            Assert.Empty(Directory.GetFiles(_directory));
            Assert.Null(await store.GetAsync<FakeToken>("someone@noogen.ai"));
        }

        [Fact]
        public void Token_files_are_owner_only_on_unix()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            _ = new ProtectedDataStore(_directory, TokenProtector.ForCurrentPlatform());
            var mode = File.GetUnixFileMode(_directory);

            Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        }

        public class FakeToken
        {
            public string RefreshToken { get; set; } = string.Empty;
        }
    }
}

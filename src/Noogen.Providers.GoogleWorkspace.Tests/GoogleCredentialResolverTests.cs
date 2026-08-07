using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Noogen.Providers.GoogleWorkspace.Security;

namespace Noogen.Providers.GoogleWorkspace.Tests
{
    public class GoogleCredentialResolverTests : IDisposable
    {
        readonly TemporaryDirectory _directory = new("noogen-resolver");
        readonly ReversingTokenProtector _protector = new();

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            _directory.Dispose();
        }

        [Fact]
        public void Choose_ServiceAccountKeyAndUserBothPresent_PrefersTheServiceAccountKey() =>
            // CI and automation set this deliberately; deliberate beats ambient.
            Assert.Equal(
                CredentialSource.ServiceAccountKey,
                GoogleCredentialResolver.Choose(hasServiceAccountKey: true, hasUserToken: true, allowApplicationDefault: true));

        [Fact]
        public void Choose_UserSignedInAndApplicationDefaultAllowed_PrefersTheSignedInUser() =>
            // The person at the keyboard, not whatever gcloud happens to be pointed at.
            Assert.Equal(
                CredentialSource.UserOAuth,
                GoogleCredentialResolver.Choose(hasServiceAccountKey: false, hasUserToken: true, allowApplicationDefault: true));

        [Fact]
        public void Choose_NothingButApplicationDefaultAllowed_FallsBackToApplicationDefault() =>
            // Present for Workload Identity in GKE, where it is the only correct answer.
            Assert.Equal(
                CredentialSource.ApplicationDefault,
                GoogleCredentialResolver.Choose(hasServiceAccountKey: false, hasUserToken: false, allowApplicationDefault: true));

        [Fact]
        public void Choose_NothingConfiguredAndApplicationDefaultDisallowed_ReportsNone() =>
            // On a workstation ADC is machine-global and usually belongs to something else, so it
            // is never volunteered.
            Assert.Equal(
                CredentialSource.None,
                GoogleCredentialResolver.Choose(hasServiceAccountKey: false, hasUserToken: false, allowApplicationDefault: false));

        [Fact]
        public async Task ResolveAsync_ServiceAccountKeyConfigured_UsesItEvenThoughAUserIsSignedIn()
        {
            var keyPath = WriteServiceAccountKey();
            await StoreTokenAsync("someone@noogen.ai");

            var resolved = await Resolver(keyPath).ResolveAsync("someone@noogen.ai", GoogleWorkspaceScopes.All);

            Assert.Equal(CredentialSource.ServiceAccountKey, resolved.Source);
            Assert.Contains(keyPath, resolved.Description, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ResolveAsync_KeyFileIsNotAServiceAccountKey_RefusesToLoadIt()
        {
            // Pinned to ServiceAccountCredential deliberately: an external-account config can name
            // an arbitrary executable to run for token exchange, and the path comes from an
            // environment variable an attacker may control.
            var keyPath = _directory.File("external.json");
            await File.WriteAllTextAsync(keyPath, """
                {
                  "type": "external_account",
                  "audience": "//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/x",
                  "subject_token_type": "urn:ietf:params:oauth:token-type:jwt",
                  "token_url": "https://sts.googleapis.com/v1/token",
                  "credential_source": { "executable": { "command": "/bin/sh -c whoami" } }
                }
                """);

            await Assert.ThrowsAnyAsync<Exception>(
                () => Resolver(keyPath).ResolveAsync("someone@noogen.ai", GoogleWorkspaceScopes.All));
        }

        [Fact]
        public async Task ResolveAsync_KeyPathPointsAtNothing_FallsThroughToTheSignedInUser()
        {
            // A stale environment variable must not strand a user who is perfectly well signed in.
            await StoreTokenAsync("someone@noogen.ai");

            var resolved = await Resolver(_directory.File("absent.json")).ResolveAsync("someone@noogen.ai", GoogleWorkspaceScopes.All);

            Assert.Equal(CredentialSource.UserOAuth, resolved.Source);
        }

        [Fact]
        public async Task ResolveAsync_UserSignedIn_NamesTheAccountAndItsKeystore()
        {
            // `backlog whoami` prints this, so it has to say who we are and how the token is held.
            await StoreTokenAsync("someone@noogen.ai");

            var resolved = await Resolver(null).ResolveAsync("someone@noogen.ai", GoogleWorkspaceScopes.All);

            Assert.Contains("someone@noogen.ai", resolved.Description, StringComparison.Ordinal);
            Assert.Contains("test protector", resolved.Description, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ResolveAsync_NothingSignedInAndApplicationDefaultDisallowed_ThrowsNotSignedIn() =>
            await Assert.ThrowsAsync<NotSignedInException>(
                () => Resolver(null, allowApplicationDefault: false).ResolveAsync("someone@noogen.ai", GoogleWorkspaceScopes.All));

        [Fact]
        public void NotSignedInException_Always_SaysWhichCommandToRun()
        {
            var message = new NotSignedInException().Message;

            Assert.Contains("backlog login", message, StringComparison.Ordinal);
            Assert.Contains("NOOGEN_BACKLOG_CREDENTIALS", message, StringComparison.Ordinal);
        }

        GoogleCredentialResolver Resolver(string? serviceAccountKeyPath, bool allowApplicationDefault = true) =>
            new(
                new UserCredentialStore(new OAuthClientSettings { ClientId = "client-id", ClientSecret = "client-secret" }, _directory.Path, _protector),
                serviceAccountKeyPath,
                allowApplicationDefault);

        Task StoreTokenAsync(string account) =>
            new ProtectedDataStore(_directory.Path, _protector).StoreAsync(account, new TokenResponse
            {
                AccessToken = "access",
                RefreshToken = "1//refresh",
                ExpiresInSeconds = 3600,
                IssuedUtc = DateTime.UtcNow
            });

        string WriteServiceAccountKey()
        {
            using var rsa = RSA.Create(2048);

            var path = _directory.File("service-account.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["type"] = "service_account",
                ["project_id"] = "noogen",
                ["private_key_id"] = "key-1",
                ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
                ["client_email"] = "backlog@noogen.iam.gserviceaccount.com",
                ["client_id"] = "1234",
                ["token_uri"] = "https://oauth2.googleapis.com/token"
            }));

            return path;
        }
    }
}

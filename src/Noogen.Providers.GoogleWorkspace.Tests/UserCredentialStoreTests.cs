using Google.Apis.Auth.OAuth2.Responses;
using Noogen.Providers.GoogleWorkspace.Security;

namespace Noogen.Providers.GoogleWorkspace.Tests
{
    /// <summary>
    /// Everything here stays local: reading, listing and re-keying a cached token must never
    /// reach Google, and must never open a browser.
    /// </summary>
    public class UserCredentialStoreTests : IDisposable
    {
        readonly TemporaryDirectory _directory = new("noogen-credentials");
        readonly ReversingTokenProtector _protector = new();

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            _directory.Dispose();
        }

        [Fact]
        public void Constructor_Always_CreatesTheTokenDirectory() =>
            Assert.True(Directory.Exists(Create().TokenDirectory));

        [Fact]
        public void Protector_Always_ReportsTheKeystoreProtectingTheToken() =>
            // `backlog whoami` prints this, and it is the only way a user learns their token is
            // sitting in plaintext on a machine with no keystore.
            Assert.Equal("test protector", Create().Protector.Description);

        [Fact]
        public async Task TryLoadAsync_NoOAuthClientConfigured_ReturnsNull()
        {
            var store = new UserCredentialStore(new OAuthClientSettings(), _directory.Path, _protector);

            Assert.Null(await store.TryLoadAsync("someone@noogen.ai", GoogleWorkspaceScopes.All));
        }

        [Fact]
        public async Task TryLoadAsync_NothingCached_ReturnsNullRatherThanOpeningABrowser()
        {
            // An ordinary command must not surprise anyone with a consent prompt mid-run; the
            // caller reports "run backlog login" instead.
            Assert.Null(await Create().TryLoadAsync("someone@noogen.ai", GoogleWorkspaceScopes.All));
        }

        [Fact]
        public async Task TryLoadAsync_TokenCached_ReturnsACredentialCarryingTheRefreshToken()
        {
            await StoreTokenAsync("someone@noogen.ai", "1//refresh");

            var credential = await Create().TryLoadAsync("someone@noogen.ai", GoogleWorkspaceScopes.All);

            Assert.NotNull(credential);
            Assert.Equal("1//refresh", credential.Token.RefreshToken);
        }

        [Fact]
        public async Task TryLoadAsync_TokenCachedUnderAnotherAccount_ReturnsNull()
        {
            await StoreTokenAsync("someone@noogen.ai", "1//refresh");

            Assert.Null(await Create().TryLoadAsync("someone-else@noogen.ai", GoogleWorkspaceScopes.All));
        }

        [Fact]
        public async Task AuthorizeAsync_NoOAuthClientConfigured_ThrowsNamingTheFileToCreate()
        {
            var store = new UserCredentialStore(new OAuthClientSettings(), _directory.Path, _protector);

            var exception = await Assert.ThrowsAsync<OAuthClientNotConfiguredException>(
                () => store.AuthorizeAsync("someone@noogen.ai", GoogleWorkspaceScopes.All));

            Assert.Contains("oauth.json", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Desktop app", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ListAccounts_NothingStored_ReturnsEmpty() => Assert.Empty(Create().ListAccounts());

        [Fact]
        public async Task ListAccounts_TokensStored_ReturnsEachAccountSortedCaseInsensitively()
        {
            await StoreTokenAsync("zoe@noogen.ai", "1//z");
            await StoreTokenAsync("Adam@noogen.ai", "1//a");

            Assert.Equal(["Adam@noogen.ai", "zoe@noogen.ai"], Create().ListAccounts());
        }

        [Fact]
        public async Task ListAccounts_AccountWasEscapedInTheFileName_ReturnsTheOriginalAddress()
        {
            // The file name percent-escapes the address; a raw read would report "someone%40...".
            await StoreTokenAsync("someone@noogen.ai", "1//refresh");

            Assert.Equal(["someone@noogen.ai"], Create().ListAccounts());
        }

        [Fact]
        public async Task ListAccounts_DirectoryHoldsUnrelatedFiles_IgnoresThem()
        {
            await StoreTokenAsync("someone@noogen.ai", "1//refresh");
            await File.WriteAllTextAsync(_directory.File("notes.txt"), "not a token");

            Assert.Equal(["someone@noogen.ai"], Create().ListAccounts());
        }

        [Fact]
        public async Task RenameAsync_FromAndToDifferOnlyByCase_ChangesNothing()
        {
            await StoreTokenAsync("someone@noogen.ai", "1//refresh");

            Assert.False(await Create().RenameAsync("someone@noogen.ai", "Someone@Noogen.ai", GoogleWorkspaceScopes.All));
            Assert.Equal(["someone@noogen.ai"], Create().ListAccounts());
        }

        [Fact]
        public async Task RenameAsync_NothingStoredUnderTheOldKey_ReturnsFalse() =>
            Assert.False(await Create().RenameAsync(UserCredentialStore.DefaultAccountKey, "someone@noogen.ai", GoogleWorkspaceScopes.All));

        [Fact]
        public async Task RenameAsync_TokenStoredUnderThePlaceholder_MovesItToTheRealAddress()
        {
            // Login writes under a placeholder, then re-keys once Google says who signed in —
            // without a second browser trip.
            await StoreTokenAsync(UserCredentialStore.DefaultAccountKey, "1//refresh");

            Assert.True(await Create().RenameAsync(UserCredentialStore.DefaultAccountKey, "someone@noogen.ai", GoogleWorkspaceScopes.All));
            Assert.Equal(["someone@noogen.ai"], Create().ListAccounts());
        }

        [Fact]
        public async Task RenameAsync_TokenStoredUnderThePlaceholder_KeepsTheRefreshTokenReadable()
        {
            await StoreTokenAsync(UserCredentialStore.DefaultAccountKey, "1//refresh");

            var store = Create();
            await store.RenameAsync(UserCredentialStore.DefaultAccountKey, "someone@noogen.ai", GoogleWorkspaceScopes.All);

            var credential = await store.TryLoadAsync("someone@noogen.ai", GoogleWorkspaceScopes.All);

            Assert.NotNull(credential);
            Assert.Equal("1//refresh", credential.Token.RefreshToken);
        }

        [Fact]
        public async Task RevokeAsync_NothingCached_ReturnsFalseWithoutCallingGoogle() =>
            Assert.False(await Create().RevokeAsync("nobody@noogen.ai", GoogleWorkspaceScopes.All));

        UserCredentialStore Create() =>
            new(new OAuthClientSettings { ClientId = "client-id", ClientSecret = "client-secret" }, _directory.Path, _protector);

        Task StoreTokenAsync(string account, string refreshToken) =>
            new ProtectedDataStore(_directory.Path, _protector).StoreAsync(account, new TokenResponse
            {
                AccessToken = "access",
                RefreshToken = refreshToken,
                ExpiresInSeconds = 3600,
                IssuedUtc = DateTime.UtcNow
            });
    }
}

using System.Runtime.InteropServices;
using Noogen.Providers.GoogleWorkspace.Security;

namespace Noogen.Providers.GoogleWorkspace.Tests
{
    /// <summary>
    /// The replacement for Google's FileDataStore, which writes the refresh token as plaintext
    /// JSON in a predictable location — precisely the shape credential-harvesting malware expects.
    /// </summary>
    public class ProtectedDataStoreTests : IDisposable
    {
        readonly TemporaryDirectory _directory = new("noogen-tokens");
        readonly ReversingTokenProtector _protector = new();

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            _directory.Dispose();
        }

        [Fact]
        public void Constructor_DirectoryDoesNotExist_CreatesIt()
        {
            var path = _directory.File("nested");

            _ = new ProtectedDataStore(path, _protector);

            Assert.True(Directory.Exists(path));
        }

        [Fact]
        public async Task StoreAsync_Always_WritesCiphertextRatherThanTheToken()
        {
            var store = new ProtectedDataStore(_directory.Path, _protector);

            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//super-secret-refresh" });

            var onDisk = Directory.GetFiles(_directory.Path).Select(File.ReadAllText);
            Assert.All(onDisk, contents => Assert.DoesNotContain("1//super-secret-refresh", contents, StringComparison.Ordinal));
        }

        [Fact]
        public async Task StoreAsync_ThenGetAsync_RoundTripsThroughTheProtector()
        {
            var store = new ProtectedDataStore(_directory.Path, _protector);

            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//abc" });

            Assert.Equal("1//abc", (await store.GetAsync<FakeToken>("someone@noogen.ai")).RefreshToken);
        }

        [Fact]
        public async Task StoreAsync_Always_KeysTheFileByTypeAndAccount()
        {
            var store = new ProtectedDataStore(_directory.Path, _protector);

            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//abc" });

            // UserCredentialStore.ListAccounts reads the account back out of this name.
            Assert.Equal("FakeToken-someone%40noogen.ai", Path.GetFileName(Directory.GetFiles(_directory.Path).Single()));
        }

        [Fact]
        public async Task StoreAsync_SameKeyTwice_OverwritesRatherThanAccumulating()
        {
            var store = new ProtectedDataStore(_directory.Path, _protector);

            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//first" });
            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//second" });

            Assert.Single(Directory.GetFiles(_directory.Path));
            Assert.Equal("1//second", (await store.GetAsync<FakeToken>("someone@noogen.ai")).RefreshToken);
        }

        [Fact]
        public async Task GetAsync_NothingStoredUnderTheKey_ReturnsDefaultRatherThanThrowing() =>
            Assert.Null(await new ProtectedDataStore(_directory.Path, _protector).GetAsync<FakeToken>("nobody@noogen.ai"));

        [Fact]
        public async Task GetAsync_ProtectorCannotUnwrapTheStoredForm_ReturnsDefault()
        {
            // A blob copied from another machine or account. Treat it as "no credential" so the
            // user is told to sign in again.
            var store = new ProtectedDataStore(_directory.Path, _protector);
            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//abc" });

            await File.WriteAllTextAsync(Directory.GetFiles(_directory.Path).Single(), "!!! not something this protector wrote !!!");

            Assert.Null(await store.GetAsync<FakeToken>("someone@noogen.ai"));
        }

        [Fact]
        public async Task GetAsync_PlaintextIsNotTheExpectedJson_ReturnsDefault()
        {
            var store = new ProtectedDataStore(_directory.Path, _protector);
            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//abc" });

            await File.WriteAllTextAsync(Directory.GetFiles(_directory.Path).Single(), _protector.Protect("x", "{ not json"));

            Assert.Null(await store.GetAsync<FakeToken>("someone@noogen.ai"));
        }

        [Fact]
        public async Task GetAsync_RealPlatformProtector_RoundTripsOnThisMachine()
        {
            // The path a signed-in user actually takes, with whatever keystore this box has.
            var store = new ProtectedDataStore(_directory.Path, TokenProtector.ForCurrentPlatform());
            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//abc" });

            Assert.Equal("1//abc", (await store.GetAsync<FakeToken>("someone@noogen.ai")).RefreshToken);
            await store.DeleteAsync<FakeToken>("someone@noogen.ai");
        }

        [Fact]
        public async Task DeleteAsync_Always_RemovesBothTheFileAndTheKeystoreEntry()
        {
            // On macOS and Linux the token itself lives in the keystore, so deleting only the file
            // would leave the secret behind.
            var store = new ProtectedDataStore(_directory.Path, _protector);
            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//abc" });

            await store.DeleteAsync<FakeToken>("someone@noogen.ai");

            Assert.Empty(Directory.GetFiles(_directory.Path));
            Assert.Equal(["FakeToken-someone@noogen.ai"], _protector.Removed);
        }

        [Fact]
        public async Task DeleteAsync_NothingStored_StillClearsTheKeystoreEntry()
        {
            var store = new ProtectedDataStore(_directory.Path, _protector);

            await store.DeleteAsync<FakeToken>("nobody@noogen.ai");

            Assert.Equal(["FakeToken-nobody@noogen.ai"], _protector.Removed);
        }

        [Fact]
        public async Task ClearAsync_Always_RemovesTheWholeTokenDirectory()
        {
            var store = new ProtectedDataStore(_directory.Path, _protector);
            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//abc" });

            await store.ClearAsync();

            Assert.False(Directory.Exists(_directory.Path));
        }

        [Fact]
        public void Constructor_OnUnix_MakesTheTokenDirectoryOwnerOnly()
        {
            // Keeps the token away from other accounts on a shared machine, and off the radar of
            // backup and sync tools that skip unreadable files.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            _ = new ProtectedDataStore(_directory.Path, _protector);

            var mode = File.GetUnixFileMode(_directory.Path);
            Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        }

        [Fact]
        public async Task StoreAsync_OnUnix_MakesTheTokenFileOwnerOnly()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            var store = new ProtectedDataStore(_directory.Path, _protector);
            await store.StoreAsync("someone@noogen.ai", new FakeToken { RefreshToken = "1//abc" });

            var mode = File.GetUnixFileMode(Directory.GetFiles(_directory.Path).Single());
            Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        }

        [Fact]
        public void RestrictPermissions_PathDoesNotExist_DoesNotThrow() =>
            // Best effort: an exotic filesystem refusing chmod must not stop the CLI working.
            ProtectedDataStore.RestrictPermissions(_directory.File("absent"));

        public class FakeToken
        {
            public string RefreshToken { get; set; } = string.Empty;
        }
    }
}

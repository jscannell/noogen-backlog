using System.Runtime.InteropServices;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace Noogen.Providers.GoogleWorkspace.Security
{
    /// <summary>
    /// Google's token store, with the token encrypted at rest by the OS keystore.
    ///
    /// Replaces <c>FileDataStore</c>, which writes the refresh token as plaintext JSON in a
    /// predictable location — precisely the shape credential-harvesting malware looks for.
    /// </summary>
    public class ProtectedDataStore : IDataStore
    {
        readonly string _directory;
        readonly ITokenProtector _protector;

        public ProtectedDataStore(string directory, ITokenProtector protector)
        {
            _directory = directory;
            _protector = protector;

            Directory.CreateDirectory(_directory);
            RestrictPermissions(_directory);
        }

        public ITokenProtector Protector => _protector;

        public Task StoreAsync<T>(string key, T value)
        {
            var path = PathFor<T>(key);
            var serialized = NewtonsoftJsonSerializer.Instance.Serialize(value);

            File.WriteAllText(path, _protector.Protect(StorageKey<T>(key), serialized));
            RestrictPermissions(path);

            return Task.CompletedTask;
        }

        public Task<T> GetAsync<T>(string key)
        {
            var path = PathFor<T>(key);
            if (!File.Exists(path))
                return Task.FromResult(default(T)!);

            var stored = File.ReadAllText(path);
            var plaintext = _protector.Unprotect(StorageKey<T>(key), stored);

            if (string.IsNullOrEmpty(plaintext))
                return Task.FromResult(default(T)!);

            try
            {
                return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(plaintext));
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // Must be Newtonsoft's exception, not System.Text.Json's: the serializer above is
                // Google's, and catching the wrong one would let a truncated token file crash the
                // CLI rather than degrade to "sign in again".
                return Task.FromResult(default(T)!);
            }
        }

        public Task DeleteAsync<T>(string key)
        {
            var path = PathFor<T>(key);
            if (File.Exists(path))
                File.Delete(path);

            _protector.Remove(StorageKey<T>(key));
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);

            return Task.CompletedTask;
        }

        string PathFor<T>(string key) => Path.Combine(_directory, $"{typeof(T).Name}-{Uri.EscapeDataString(key)}");

        static string StorageKey<T>(string key) => $"{typeof(T).Name}-{key}";

        /// <summary>
        /// Owner-only access. This does not stop malware running as the user — nothing in user
        /// space does — but it keeps the token out of reach of other accounts on a shared machine
        /// and off the radar of backup and sync tools that skip unreadable files.
        /// </summary>
        internal static void RestrictPermissions(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            try
            {
                var mode = Directory.Exists(path)
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite;

                File.SetUnixFileMode(path, mode);
            }
            catch (Exception)
            {
                // Best effort: an exotic filesystem refusing chmod must not stop the CLI working.
            }
        }
    }
}

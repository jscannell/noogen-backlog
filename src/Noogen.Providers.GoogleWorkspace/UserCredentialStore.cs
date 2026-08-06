using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Noogen.Providers.GoogleWorkspace.Security;

namespace Noogen.Providers.GoogleWorkspace
{
    /// <summary>
    /// Per-user OAuth tokens on this machine.
    ///
    /// Each person signs in as themselves, so Drive revision history attributes edits to the
    /// actual human rather than to one shared robot, and access is governed by their existing
    /// membership of the shared drive. Nothing is granted centrally and nothing is shared.
    ///
    /// Deliberately separate from Application Default Credentials: ADC is machine-global, and
    /// re-running `gcloud auth application-default login` with Drive scopes would clobber the
    /// credentials a developer is already using for other work.
    /// </summary>
    public class UserCredentialStore
    {
        public const string DefaultAccountKey = "default";

        readonly OAuthClientSettings _client;
        readonly string _tokenDirectory;
        readonly ProtectedDataStore _store;

        public UserCredentialStore(OAuthClientSettings client, string tokenDirectory, ITokenProtector? protector = null)
        {
            _client = client;
            _tokenDirectory = tokenDirectory;
            _store = new ProtectedDataStore(tokenDirectory, protector ?? TokenProtector.ForCurrentPlatform());
        }

        public string TokenDirectory => _tokenDirectory;

        /// <summary>Which keystore is protecting the refresh token, so `whoami` can report it.</summary>
        public ITokenProtector Protector => _store.Protector;

        GoogleAuthorizationCodeFlow CreateFlow(IEnumerable<string> scopes) =>
            new(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = _client.ToClientSecrets(),
                Scopes = scopes,
                DataStore = _store
            });

        /// <summary>
        /// Reads a cached token, refreshing it silently if it has expired. Never opens a browser —
        /// ordinary commands must not surprise anyone with a consent prompt mid-run. Returns null
        /// when there is nothing cached, and the caller reports "run backlog login".
        /// </summary>
        public async Task<UserCredential?> TryLoadAsync(string account, IEnumerable<string> scopes, CancellationToken cancellationToken = default)
        {
            if (!_client.IsConfigured)
                return null;

            var flow = CreateFlow(scopes);
            var token = await flow.LoadTokenAsync(account, cancellationToken);

            return token is null ? null : new UserCredential(flow, account, token);
        }

        /// <summary>Opens the browser for consent and caches the refresh token. Only `backlog login` calls this.</summary>
        public async Task<UserCredential> AuthorizeAsync(string account, IEnumerable<string> scopes, CancellationToken cancellationToken = default)
        {
            if (!_client.IsConfigured)
                throw new OAuthClientNotConfiguredException(Path.Combine(Directory.GetParent(_tokenDirectory)?.FullName ?? _tokenDirectory, "oauth.json"));

            // A loopback receiver on 127.0.0.1 — the flow Google mandates for installed apps.
            return await GoogleWebAuthorizationBroker.AuthorizeAsync(
                _client.ToClientSecrets(),
                scopes,
                account,
                cancellationToken,
                _store,
                new LocalServerCodeReceiver());
        }

        /// <summary>
        /// Moves a cached token to a different key. Login uses this to re-key from a placeholder
        /// to the real address once Google tells us who signed in — without a second browser trip.
        /// </summary>
        public async Task<bool> RenameAsync(string from, string to, IEnumerable<string> scopes, CancellationToken cancellationToken = default)
        {
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                return false;

            var token = await CreateFlow(scopes).LoadTokenAsync(from, cancellationToken);
            if (token is null)
                return false;

            await _store.StoreAsync(to, token);
            await _store.DeleteAsync<TokenResponse>(from);

            return true;
        }

        public async Task<bool> RevokeAsync(string account, IEnumerable<string> scopes, CancellationToken cancellationToken = default)
        {
            var credential = await TryLoadAsync(account, scopes, cancellationToken);
            if (credential is null)
                return false;

            try
            {
                await credential.RevokeTokenAsync(cancellationToken);
            }
            catch (Exception)
            {
                // The token may already be dead or the network down. Deleting the local copy is
                // the part that must happen regardless, so a failed revoke is not fatal.
            }

            await _store.DeleteAsync<TokenResponse>(account);
            return true;
        }

        public IReadOnlyList<string> ListAccounts()
        {
            if (!Directory.Exists(_tokenDirectory))
                return [];

            // Files are named "<Type>-<key>"; the key is the account we stored under.
            const string Prefix = "TokenResponse-";

            return Directory.GetFiles(_tokenDirectory, $"*{Prefix}*")
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .Where(name => name.Contains(Prefix, StringComparison.Ordinal))
                .Select(name => name[(name.IndexOf(Prefix, StringComparison.Ordinal) + Prefix.Length)..])
                .Select(Uri.UnescapeDataString)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Asks Google who the token belongs to. Used only to show a friendly identity, so a
        /// failure here degrades to "unknown" rather than breaking the command.
        /// </summary>
        public static async Task<string?> GetEmailAsync(UserCredential credential, CancellationToken cancellationToken = default)
        {
            try
            {
                var accessToken = await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);

                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var json = await http.GetStringAsync("https://www.googleapis.com/oauth2/v3/userinfo", cancellationToken);
                using var document = JsonDocument.Parse(json);

                return document.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;

namespace Noogen.Providers.GoogleWorkspace
{
    /// <summary>
    /// The OAuth client identifying this CLI to Google. One Desktop-type client per organisation,
    /// created once.
    ///
    /// The "secret" is not confidential here, and Google says so: for installed applications the
    /// client secret cannot be kept secret, and security rests on the user's own consent plus the
    /// loopback redirect. The real access boundary is the person's Google account and their
    /// membership of the shared drive — not this string.
    ///
    /// Resolution order lets an organisation choose how to distribute it: environment variables,
    /// a file next to the local config, or values compiled in for an internal build.
    /// </summary>
    public class OAuthClientSettings
    {
        public const string ClientIdEnvironmentVariable = "NOOGEN_BACKLOG_OAUTH_CLIENT_ID";
        public const string ClientSecretEnvironmentVariable = "NOOGEN_BACKLOG_OAUTH_CLIENT_SECRET";

        /// <summary>Set at build time for an internal distribution; empty in source.</summary>
        public const string CompiledInClientId = "";

        public const string CompiledInClientSecret = "";

        [JsonPropertyName("clientId")]
        public string? ClientId { get; set; }

        [JsonPropertyName("clientSecret")]
        public string? ClientSecret { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

        public ClientSecrets ToClientSecrets() => new()
        {
            ClientId = ClientId,
            ClientSecret = ClientSecret
        };

        public static OAuthClientSettings Resolve(string? filePath = null)
        {
            var fromEnvironment = new OAuthClientSettings
            {
                ClientId = Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable),
                ClientSecret = Environment.GetEnvironmentVariable(ClientSecretEnvironmentVariable)
            };

            if (fromEnvironment.IsConfigured)
                return fromEnvironment;

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                var fromFile = ReadFile(filePath);
                if (fromFile is not null && fromFile.IsConfigured)
                    return fromFile;
            }

            return new OAuthClientSettings
            {
                ClientId = CompiledInClientId,
                ClientSecret = CompiledInClientSecret
            };
        }

        static OAuthClientSettings? ReadFile(string filePath)
        {
            var json = File.ReadAllText(filePath);

            // Accepts either our flat shape or the client_secret_*.json Google hands you, so the
            // downloaded file can be dropped in unedited.
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("installed", out var installed))
            {
                return new OAuthClientSettings
                {
                    ClientId = installed.TryGetProperty("client_id", out var id) ? id.GetString() : null,
                    ClientSecret = installed.TryGetProperty("client_secret", out var secret) ? secret.GetString() : null
                };
            }

            return JsonSerializer.Deserialize<OAuthClientSettings>(json);
        }
    }

    public class OAuthClientNotConfiguredException : InvalidOperationException
    {
        public OAuthClientNotConfiguredException(string oauthFilePath)
            : base(
                "No OAuth client is configured, so 'backlog login' cannot start.\n\n" +
                "Someone needs to create one, once, for the whole organisation:\n" +
                "  1. Google Cloud console -> APIs & Services -> Credentials\n" +
                "  2. Create Credentials -> OAuth client ID -> Application type: Desktop app\n" +
                "  3. On the OAuth consent screen choose User Type: Internal\n" +
                "     (internal apps skip Google's verification review)\n" +
                $"  4. Download the JSON and save it as: {oauthFilePath}\n\n" +
                $"Or set {OAuthClientSettings.ClientIdEnvironmentVariable} and " +
                $"{OAuthClientSettings.ClientSecretEnvironmentVariable}.")
        {
        }
    }
}

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
            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(File.ReadAllText(filePath));
            }
            catch (JsonException exception)
            {
                throw new OAuthClientInvalidException(filePath, $"the file is not valid JSON ({exception.Message})");
            }

            using (document)
            {
                // A "web" root means an OAuth client of the wrong type. It cannot drive the
                // loopback flow an installed app uses, and the resulting failure would otherwise
                // surface as a confusing consent-screen error much later.
                if (document.RootElement.TryGetProperty("web", out _))
                {
                    throw new OAuthClientInvalidException(
                        filePath,
                        "this is a Web application client. Create a new OAuth client ID with " +
                        "Application type: Desktop app, and download that one instead");
                }

                // The client_secret_*.json Google hands you, unedited.
                if (document.RootElement.TryGetProperty("installed", out var installed))
                {
                    var settings = new OAuthClientSettings
                    {
                        ClientId = ReadString(installed, "client_id"),
                        ClientSecret = ReadString(installed, "client_secret")
                    };

                    if (!settings.IsConfigured)
                        throw new OAuthClientInvalidException(filePath, "the 'installed' section has no client_id or client_secret");

                    return settings;
                }

                // Or our flat shape, for anyone who would rather write two lines than keep the
                // whole download.
                var flat = new OAuthClientSettings
                {
                    ClientId = ReadString(document.RootElement, "clientId") ?? ReadString(document.RootElement, "client_id"),
                    ClientSecret = ReadString(document.RootElement, "clientSecret") ?? ReadString(document.RootElement, "client_secret")
                };

                if (!flat.IsConfigured)
                {
                    throw new OAuthClientInvalidException(
                        filePath,
                        "expected either the client_secret JSON downloaded from the Cloud console " +
                        "(with an 'installed' section) or {\"clientId\": \"...\", \"clientSecret\": \"...\"}");
                }

                return flat;
            }
        }

        static string? ReadString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// The file exists but cannot be used. Distinct from "not configured" so the message can name
    /// the actual problem instead of repeating the whole setup guide.
    /// </summary>
    public class OAuthClientInvalidException : InvalidOperationException
    {
        public OAuthClientInvalidException(string filePath, string problem)
            : base($"The OAuth client file at {filePath} cannot be used: {problem}.")
        {
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

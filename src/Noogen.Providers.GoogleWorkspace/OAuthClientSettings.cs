using System.Reflection;
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

        /// <summary>
        /// Name of the resource the build bakes into the tool, so a distributed install works
        /// with nothing on disk. See the CLI csproj: it embeds a gitignored oauth.json when one
        /// is present, which keeps the value out of the repository and off every user's machine.
        /// </summary>
        public const string EmbeddedResourceName = "oauth.json";

        [JsonPropertyName("clientId")]
        public string? ClientId { get; set; }

        [JsonPropertyName("clientSecret")]
        public string? ClientSecret { get; set; }

        /// <summary>Where this came from, so `backlog whoami` can show it without guesswork.</summary>
        [JsonIgnore]
        public string Source { get; set; } = "none";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

        public ClientSecrets ToClientSecrets() => new()
        {
            ClientId = ClientId,
            ClientSecret = ClientSecret
        };

        /// <summary>
        /// Environment, then an on-disk file, then whatever the build embedded.
        ///
        /// The embedded copy is the org default and is why an ordinary install needs no setup at
        /// all. It comes last so that anyone testing against a different client can override it
        /// without rebuilding — an override should beat a default, not the other way round.
        /// </summary>
        public static OAuthClientSettings Resolve(string? filePath = null, Assembly? embeddedIn = null)
        {
            var fromEnvironment = new OAuthClientSettings
            {
                ClientId = Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable),
                ClientSecret = Environment.GetEnvironmentVariable(ClientSecretEnvironmentVariable),
                Source = $"{ClientIdEnvironmentVariable} environment variable"
            };

            if (fromEnvironment.IsConfigured)
                return fromEnvironment;

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                var fromFile = ReadFile(filePath);
                if (fromFile is not null && fromFile.IsConfigured)
                {
                    fromFile.Source = filePath;
                    return fromFile;
                }
            }

            var fromEmbedded = ReadEmbedded(embeddedIn);
            if (fromEmbedded is not null && fromEmbedded.IsConfigured)
                return fromEmbedded;

            return new OAuthClientSettings();
        }

        static OAuthClientSettings? ReadEmbedded(Assembly? assembly)
        {
            if (assembly is null)
                return null;

            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(candidate => candidate.EndsWith(EmbeddedResourceName, StringComparison.OrdinalIgnoreCase));

            if (name is null)
                return null;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
                return null;

            using var reader = new StreamReader(stream);
            var settings = Parse(reader.ReadToEnd(), $"embedded resource '{name}'");

            if (settings is not null)
                settings.Source = "built into this tool";

            return settings;
        }

        static OAuthClientSettings? ReadFile(string filePath) => Parse(File.ReadAllText(filePath), filePath);

        static OAuthClientSettings? Parse(string json, string origin)
        {
            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                throw new OAuthClientInvalidException(origin, $"the content is not valid JSON ({exception.Message})");
            }

            using (document)
            {
                // A "web" root means an OAuth client of the wrong type. It cannot drive the
                // loopback flow an installed app uses, and the resulting failure would otherwise
                // surface as a confusing consent-screen error much later.
                if (document.RootElement.TryGetProperty("web", out _))
                {
                    throw new OAuthClientInvalidException(
                        origin,
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
                        throw new OAuthClientInvalidException(origin, "the 'installed' section has no client_id or client_secret");

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
                        origin,
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

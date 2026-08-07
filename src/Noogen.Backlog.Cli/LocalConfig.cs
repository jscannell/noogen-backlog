using System.Text.Json;
using System.Text.Json.Serialization;
using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Cli
{
    /// <summary>
    /// The only machine-local state: which Sheet to talk to. Everything else — WIP limit, id
    /// prefix, folder ids — lives on the Sheet's Config tab so the whole company shares one
    /// answer. Kept outside any repo because the backlog spans repos.
    /// </summary>
    public class LocalConfig
    {
        public const string DriveIdEnvironmentVariable = "NOOGEN_BACKLOG_DRIVE_ID";
        public const string SpreadsheetIdEnvironmentVariable = "NOOGEN_BACKLOG_SPREADSHEET_ID";
        public const string OwnerEnvironmentVariable = "NOOGEN_BACKLOG_OWNER";

        [JsonPropertyName("sharedDriveId")]
        public string? SharedDriveId { get; set; }

        [JsonPropertyName("spreadsheetId")]
        public string? SpreadsheetId { get; set; }

        [JsonPropertyName("defaultOwner")]
        public string? DefaultOwner { get; set; }

        /// <summary>Which signed-in account to use. Set by `backlog login`.</summary>
        [JsonPropertyName("account")]
        public string? Account { get; set; }

        static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public const string ServiceAccountKeyEnvironmentVariable = "NOOGEN_BACKLOG_CREDENTIALS";

        public static string Directory
        {
            get
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return System.IO.Path.Combine(appData, "Noogen");
            }
        }

        public static string Path => System.IO.Path.Combine(Directory, "backlog.json");

        /// <summary>Where the OAuth client JSON downloaded from the Cloud console can be dropped.</summary>
        public static string OAuthClientPath => System.IO.Path.Combine(Directory, "oauth.json");

        /// <summary>Encrypted refresh tokens, one file per signed-in account.</summary>
        public static string TokenDirectory => System.IO.Path.Combine(Directory, "credentials");

        public const string ClaudeConfigEnvironmentVariable = "CLAUDE_CONFIG_DIR";

        /// <summary>
        /// Where Claude Code keeps a person's own skills. Not under <see cref="Directory"/>: this
        /// is Claude's directory, not ours, and we only ever add one folder to it.
        ///
        /// CLAUDE_CONFIG_DIR relocates ~/.claude, so honour it — installing beside a moved config
        /// would write a copy nothing ever loads, which looks like success and is not.
        /// </summary>
        public static string SkillsDirectory
        {
            get
            {
                var configured = Environment.GetEnvironmentVariable(ClaudeConfigEnvironmentVariable);

                var root = string.IsNullOrWhiteSpace(configured)
                    ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                    : configured.Trim();

                return System.IO.Path.Combine(root, "skills");
            }
        }

        /// <summary>
        /// An explicit service-account key, for CI and automation. Never auto-discovered from
        /// GOOGLE_APPLICATION_CREDENTIALS: that variable usually belongs to something else on a
        /// developer's machine, and quietly acting as a different identity would be a surprise.
        /// </summary>
        public static string? ServiceAccountKeyPath =>
            Environment.GetEnvironmentVariable(ServiceAccountKeyEnvironmentVariable);

        public string ResolveAccount(string? requested) =>
            !string.IsNullOrWhiteSpace(requested) ? requested.Trim()
            : !string.IsNullOrWhiteSpace(Account) ? Account
            : UserCredentialStore.DefaultAccountKey;

        public static LocalConfig Load()
        {
            var config = File.Exists(Path)
                ? JsonSerializer.Deserialize<LocalConfig>(File.ReadAllText(Path)) ?? new LocalConfig()
                : new LocalConfig();

            config.SharedDriveId = FirstNonEmpty(Environment.GetEnvironmentVariable(DriveIdEnvironmentVariable), config.SharedDriveId);
            config.SpreadsheetId = FirstNonEmpty(Environment.GetEnvironmentVariable(SpreadsheetIdEnvironmentVariable), config.SpreadsheetId);
            config.DefaultOwner = FirstNonEmpty(Environment.GetEnvironmentVariable(OwnerEnvironmentVariable), config.DefaultOwner);

            return config;
        }

        public void Save()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, SerializerOptions));
        }

        public string RequireSpreadsheetId() =>
            string.IsNullOrWhiteSpace(SpreadsheetId)
                ? throw new UsageException(
                    $"No backlog configured. Run 'backlog init --drive <sharedDriveId>' first, " +
                    $"or set {SpreadsheetIdEnvironmentVariable}. Config lives at {Path}.")
                : SpreadsheetId;

        public string ResolveOwner(string? requested)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return DefaultOwner ?? Environment.UserName;

            return string.Equals(requested, "me", StringComparison.OrdinalIgnoreCase)
                ? DefaultOwner ?? Environment.UserName
                : requested.Trim();
        }

        static string? FirstNonEmpty(string? preferred, string? fallback) =>
            string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
    }
}

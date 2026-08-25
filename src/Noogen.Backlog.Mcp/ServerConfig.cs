namespace Noogen.Backlog.Mcp
{
    /// <summary>
    /// Which backlog this server serves, and as whom. All of it from the environment.
    ///
    /// Not from the CLI's config file: that lives under a person's <c>%APPDATA%</c> and is state
    /// belonging to whoever is signed in at that keyboard. A server has no keyboard, may not have
    /// a home directory, and is configured by whoever deploys it — so the environment is the whole
    /// story, and <c>init</c> and <c>login</c> are refused here for the same reason.
    ///
    /// The names are the CLI's names on purpose. A machine already configured to run the tool can
    /// serve the same backlog without being told twice, and anyone who has read the README once
    /// knows them.
    /// </summary>
    public class ServerConfig
    {
        public const string SpreadsheetIdEnvironmentVariable = "NOOGEN_BACKLOG_SPREADSHEET_ID";
        public const string OwnerEnvironmentVariable = "NOOGEN_BACKLOG_OWNER";
        public const string ServiceAccountKeyEnvironmentVariable = "NOOGEN_BACKLOG_CREDENTIALS";
        public const string AccountEnvironmentVariable = "NOOGEN_BACKLOG_ACCOUNT";

        /// <summary>
        /// Where <c>backlog login</c> wrote its encrypted refresh tokens, for a server run on a
        /// workstation that has already signed in. Absent on a deployed server, where the identity
        /// is a service account or the platform's own.
        /// </summary>
        public const string TokenDirectoryEnvironmentVariable = "NOOGEN_BACKLOG_TOKENS";

        /// <summary>
        /// An OAuth client on disk, needed only to refresh a token from
        /// <see cref="TokenDirectoryEnvironmentVariable"/>. Nothing is embedded in this assembly —
        /// the client is baked into the CLI, which is the artifact people install.
        /// </summary>
        public const string OAuthClientFileEnvironmentVariable = "NOOGEN_BACKLOG_OAUTH_FILE";

        public string SpreadsheetId { get; set; } = string.Empty;

        /// <summary>
        /// Who a write is attributed to when the caller names nobody. One value for the whole
        /// server: it acts as a single identity, and a caller who wants their own name on a ticket
        /// passes <c>owner</c>.
        /// </summary>
        public string? Owner { get; set; }

        public string? ServiceAccountKeyPath { get; set; }

        public string? TokenDirectory { get; set; }

        public string Account { get; set; } = "default";

        public string? OAuthClientPath { get; set; }

        public static ServerConfig FromEnvironment() => new()
        {
            SpreadsheetId = Read(SpreadsheetIdEnvironmentVariable) ?? string.Empty,
            Owner = Read(OwnerEnvironmentVariable),
            ServiceAccountKeyPath = Read(ServiceAccountKeyEnvironmentVariable),
            TokenDirectory = Read(TokenDirectoryEnvironmentVariable),
            Account = Read(AccountEnvironmentVariable) ?? "default",
            OAuthClientPath = Read(OAuthClientFileEnvironmentVariable)
        };

        public string RequireSpreadsheetId() =>
            string.IsNullOrWhiteSpace(SpreadsheetId)
                ? throw new UsageException(
                    $"No backlog configured. Set {SpreadsheetIdEnvironmentVariable} to the id of the "
                    + "Backlog Index spreadsheet. Run 'backlog init' from the CLI to create one.")
                : SpreadsheetId;

        static string? Read(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}

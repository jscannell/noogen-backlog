using System.Text.Json;
using System.Text.Json.Serialization;

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

        static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string Path
        {
            get
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return System.IO.Path.Combine(appData, "Noogen", "backlog.json");
            }
        }

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
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

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

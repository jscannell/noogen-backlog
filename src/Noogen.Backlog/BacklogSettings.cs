using System.Globalization;
using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog
{
    /// <summary>
    /// Process policy lives on the Config tab, not in each person's local file. Kanban asks for
    /// explicit policies; putting the WIP limit somewhere everyone can see and change is the
    /// cheapest way to honour that. The local config only needs the spreadsheet id — everything
    /// else is discovered from the Sheet.
    /// </summary>
    public class BacklogSettings
    {
        public const string IdPrefixKey = "id_prefix";
        public const string IdWidthKey = "id_width";
        public const string WipLimitKey = "wip_limit";
        public const string TicketsFolderKey = "tickets_folder_id";
        public const string ArchiveFolderKey = "archive_folder_id";

        public string IdPrefix { get; set; } = "NG";

        public int IdWidth { get; set; } = 4;

        public int WipLimit { get; set; } = 5;

        public string TicketsFolderId { get; set; } = string.Empty;

        public string ArchiveFolderId { get; set; } = string.Empty;

        public string FormatId(int number) =>
            $"{IdPrefix}-{number.ToString(CultureInfo.InvariantCulture).PadLeft(IdWidth, '0')}";

        /// <summary>Pulls the numeric part out of an id, ignoring the prefix so a rename is survivable.</summary>
        public static int? ParseIdNumber(string id)
        {
            var dash = id.LastIndexOf('-');
            var digits = dash >= 0 ? id[(dash + 1)..] : id;

            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        public static async Task<BacklogSettings> LoadAsync(ISheetsGateway sheets, string spreadsheetId, CancellationToken cancellationToken = default)
        {
            var values = await sheets.GetValuesAsync(spreadsheetId, A1.WholeTab(SheetSchema.ConfigTabName), cancellationToken);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in values)
            {
                if (row.Count < 2)
                    continue;

                var key = row[0]?.ToString()?.Trim();
                var value = row[1]?.ToString()?.Trim();

                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    map[key] = value;
            }

            var settings = new BacklogSettings();

            if (map.TryGetValue(IdPrefixKey, out var prefix))
                settings.IdPrefix = prefix;

            if (map.TryGetValue(IdWidthKey, out var width) && int.TryParse(width, out var parsedWidth))
                settings.IdWidth = parsedWidth;

            if (map.TryGetValue(WipLimitKey, out var wip) && int.TryParse(wip, out var parsedWip))
                settings.WipLimit = parsedWip;

            if (map.TryGetValue(TicketsFolderKey, out var tickets))
                settings.TicketsFolderId = tickets;

            if (map.TryGetValue(ArchiveFolderKey, out var archive))
                settings.ArchiveFolderId = archive;

            return settings;
        }

        public IList<IList<object>> ToRows() =>
        [
            ["key", "value", "notes"],
            [IdPrefixKey, IdPrefix, "Ticket id prefix."],
            [IdWidthKey, IdWidth, "Zero-padding width for the numeric part."],
            [WipLimitKey, WipLimit, "Kanban WIP limit. 'backlog start' refuses to exceed it without --force."],
            [TicketsFolderKey, TicketsFolderId, "Drive folder holding active ticket documents."],
            [ArchiveFolderKey, ArchiveFolderId, "Drive folder root for archived ticket documents (year/quarter beneath)."],
            [string.Empty, string.Empty, string.Empty],
            ["vocabulary", "values", string.Empty],
            ["type", string.Join(", ", Vocabulary.WireValues<TicketType>()), "Ticket types."],
            ["state", string.Join(", ", Vocabulary.WireValues<WorkState>()), "Sub-states of the In Progress column."],
            ["outcome", string.Join(", ", Vocabulary.WireValues<Outcome>()), "Terminal outcomes recorded on Archive."],
            ["wsjf scale", string.Join(", ", WsjfScore.AllowedValues), "Modified Fibonacci. Score relatively; the smallest item in each column is a 1."]
        ];
    }
}

using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog
{
    public class InitResult
    {
        public string SpreadsheetId { get; set; } = string.Empty;

        public string SpreadsheetUrl { get; set; } = string.Empty;

        public string TicketsFolderId { get; set; } = string.Empty;

        public string ArchiveFolderId { get; set; } = string.Empty;

        public string TimeZoneId { get; set; } = string.Empty;

        public bool CreatedSpreadsheet { get; set; }
    }

    /// <summary>
    /// One-time setup of the Drive layout and the index. Idempotent: re-running finds what
    /// already exists rather than duplicating it, so it doubles as a repair for a half-finished
    /// first run.
    /// </summary>
    public class BacklogInitializer
    {
        public const string IndexName = "Backlog Index";
        public const string TicketsFolderName = "tickets";
        public const string ArchiveFolderName = "archive";

        readonly IDriveGateway _drive;
        readonly ISheetsGateway _sheets;

        public BacklogInitializer(IDriveGateway drive, ISheetsGateway sheets)
        {
            _drive = drive;
            _sheets = sheets;
        }

        public async Task<InitResult> RunAsync(string rootFolderId, string? existingSpreadsheetId = null, string? timeZoneId = null, CancellationToken cancellationToken = default)
        {
            var result = new InitResult();

            var ticketsFolderId = await _drive.FindChildAsync(rootFolderId, TicketsFolderName, DriveGateway.FolderMimeType, cancellationToken)
                ?? await _drive.CreateFolderAsync(rootFolderId, TicketsFolderName, cancellationToken);

            var archiveFolderId = await _drive.FindChildAsync(rootFolderId, ArchiveFolderName, DriveGateway.FolderMimeType, cancellationToken)
                ?? await _drive.CreateFolderAsync(rootFolderId, ArchiveFolderName, cancellationToken);

            var spreadsheetId = existingSpreadsheetId
                ?? await _drive.FindChildAsync(rootFolderId, IndexName, DriveGateway.SpreadsheetMimeType, cancellationToken);

            if (spreadsheetId is null)
            {
                spreadsheetId = await _drive.CreateSpreadsheetAsync(rootFolderId, IndexName, cancellationToken);
                result.CreatedSpreadsheet = true;
            }

            result.SpreadsheetId = spreadsheetId;
            result.TicketsFolderId = ticketsFolderId;
            result.ArchiveFolderId = archiveFolderId;
            result.SpreadsheetUrl = await _drive.GetWebViewLinkAsync(spreadsheetId, cancellationToken);

            var settings = await EnsureConfigTabAsync(spreadsheetId, ticketsFolderId, archiveFolderId, timeZoneId, cancellationToken);
            result.TimeZoneId = settings.TimeZoneId;

            // Datetime cells are wall-clock values interpreted against the spreadsheet's own
            // timezone, so it has to agree with the config or every instant we store shifts.
            await _sheets.SetSpreadsheetTimeZoneAsync(spreadsheetId, settings.TimeZoneId, cancellationToken);

            foreach (var phase in BacklogPhaseExtensions.All)
                await EnsureLifecycleTabAsync(spreadsheetId, phase, cancellationToken);

            return result;
        }

        async Task EnsureLifecycleTabAsync(string spreadsheetId, BacklogPhase phase, CancellationToken cancellationToken)
        {
            var tabName = phase.TabName();
            await _sheets.EnsureTabAsync(spreadsheetId, tabName, cancellationToken);

            var columns = SheetSchema.Columns(phase);
            var existing = await _sheets.GetValuesAsync(spreadsheetId, A1.WholeTab(tabName), cancellationToken);

            if (existing.Count == 0 || existing[0].Count == 0)
            {
                var header = columns.Cast<object>().ToList();
                await _sheets.UpdateValuesAsync(spreadsheetId, A1.Row(tabName, 0, columns.Count), [header], cancellationToken);
            }

            await _sheets.FreezeHeaderAsync(spreadsheetId, tabName, cancellationToken);

            var table = new SheetTable(phase, await _sheets.GetValuesAsync(spreadsheetId, A1.WholeTab(tabName), cancellationToken));

            // Timestamps are real datetime cells, so Sheets renders them in the spreadsheet's
            // timezone, sorts them numerically, and can filter on them. The pattern keeps the
            // display unambiguous rather than locale-dependent.
            foreach (var column in SheetSchema.TimestampColumns)
            {
                if (table.Has(column))
                    await _sheets.SetColumnDateTimeFormatAsync(spreadsheetId, tabName, table.IndexOf(column), SheetTime.DisplayPattern, cancellationToken);
            }

            foreach (var column in SheetSchema.HiddenColumns)
            {
                if (table.Has(column))
                {
                    var index = table.IndexOf(column);
                    await _sheets.HideColumnsAsync(spreadsheetId, tabName, index, index + 1, cancellationToken);
                }
            }
        }

        async Task<BacklogSettings> EnsureConfigTabAsync(string spreadsheetId, string ticketsFolderId, string archiveFolderId, string? timeZoneId, CancellationToken cancellationToken)
        {
            await _sheets.EnsureTabAsync(spreadsheetId, SheetSchema.ConfigTabName, cancellationToken);

            var existing = await BacklogSettings.LoadAsync(_sheets, spreadsheetId, cancellationToken);
            existing.TicketsFolderId = ticketsFolderId;
            existing.ArchiveFolderId = archiveFolderId;

            if (!string.IsNullOrWhiteSpace(timeZoneId))
                existing.TimeZoneId = timeZoneId.Trim();

            // Validates the id and fails with a useful message before anything is written.
            _ = existing.Zone;

            // Anchored at A1 so the write expands to fit however many rows ToRows() produces.
            await _sheets.UpdateValuesAsync(
                spreadsheetId,
                A1.Anchor(SheetSchema.ConfigTabName, 0, 0),
                existing.ToRows(),
                cancellationToken);

            return existing;
        }
    }
}

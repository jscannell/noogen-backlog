using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog
{
    public class InitResult
    {
        public string SpreadsheetId { get; set; } = string.Empty;

        public string SpreadsheetUrl { get; set; } = string.Empty;

        public string TicketsFolderId { get; set; } = string.Empty;

        public string ArchiveFolderId { get; set; } = string.Empty;

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

        public async Task<InitResult> RunAsync(string rootFolderId, string? existingSpreadsheetId = null, CancellationToken cancellationToken = default)
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

            foreach (var phase in BacklogPhaseExtensions.All)
                await EnsureLifecycleTabAsync(spreadsheetId, phase, cancellationToken);

            await EnsureConfigTabAsync(spreadsheetId, ticketsFolderId, archiveFolderId, cancellationToken);

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

            // Timestamps are written as ISO-8601 strings. Without an explicit TEXT format Sheets
            // coerces them into locale-formatted date serials and the round-trip read comes back
            // as something we never wrote.
            foreach (var column in SheetSchema.TimestampColumns)
            {
                if (table.Has(column))
                    await _sheets.SetColumnTextFormatAsync(spreadsheetId, tabName, table.IndexOf(column), cancellationToken);
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

        async Task EnsureConfigTabAsync(string spreadsheetId, string ticketsFolderId, string archiveFolderId, CancellationToken cancellationToken)
        {
            await _sheets.EnsureTabAsync(spreadsheetId, SheetSchema.ConfigTabName, cancellationToken);

            var existing = await BacklogSettings.LoadAsync(_sheets, spreadsheetId, cancellationToken);
            existing.TicketsFolderId = ticketsFolderId;
            existing.ArchiveFolderId = archiveFolderId;

            // Anchored at A1 so the write expands to fit however many rows ToRows() produces.
            await _sheets.UpdateValuesAsync(
                spreadsheetId,
                A1.Anchor(SheetSchema.ConfigTabName, 0, 0),
                existing.ToRows(),
                cancellationToken);
        }
    }
}

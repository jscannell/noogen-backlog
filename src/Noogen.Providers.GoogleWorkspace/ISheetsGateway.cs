namespace Noogen.Providers.GoogleWorkspace
{
    /// <summary>
    /// The narrow slice of Sheets the backlog needs. Row indexes are zero-based and include the
    /// header row, matching what <see cref="A1"/> produces.
    /// </summary>
    public interface ISheetsGateway
    {
        /// <summary>
        /// Reads unformatted values: numbers arrive as numbers and date cells as serials, rather
        /// than as locale-rendered text. Anything that needs to survive a round-trip depends on
        /// this — a formatted read would hand back whatever the viewer's locale happens to show.
        /// </summary>
        Task<IList<IList<object>>> GetValuesAsync(string spreadsheetId, string range, CancellationToken cancellationToken = default);

        /// <summary>Writes with USER_ENTERED so formula strings are stored as formulas.</summary>
        Task UpdateValuesAsync(string spreadsheetId, string range, IList<IList<object>> values, CancellationToken cancellationToken = default);

        /// <summary>Appends a row and returns its zero-based index.</summary>
        Task<int> AppendRowAsync(string spreadsheetId, string tabName, IList<object> values, CancellationToken cancellationToken = default);

        Task DeleteRowAsync(string spreadsheetId, string tabName, int rowIndex, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets a cell to plain <paramref name="text"/> carrying a rich-text link to
        /// <paramref name="uri"/>. Deliberately not a HYPERLINK() formula: the stored value stays
        /// the plain string, so ordinary value reads return the title rather than a formula.
        /// </summary>
        Task SetCellLinkAsync(string spreadsheetId, string tabName, int rowIndex, int columnIndex, string text, string uri, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<string>> ListTabsAsync(string spreadsheetId, CancellationToken cancellationToken = default);

        Task EnsureTabAsync(string spreadsheetId, string tabName, CancellationToken cancellationToken = default);

        Task RenameTabAsync(string spreadsheetId, string tabName, string newTabName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Moves the named tabs to the front, left to right, in the order given. Names that the
        /// spreadsheet does not carry are skipped, and tabs a human added keep their relative
        /// order behind ours.
        /// </summary>
        Task SetTabOrderAsync(string spreadsheetId, IReadOnlyList<string> tabNames, CancellationToken cancellationToken = default);

        Task FreezeHeaderAsync(string spreadsheetId, string tabName, CancellationToken cancellationToken = default);

        Task HideColumnsAsync(string spreadsheetId, string tabName, int startColumnIndex, int endColumnIndex, CancellationToken cancellationToken = default);

        /// <summary>
        /// Applies a date-time number format to the given columns over a half-open row range, so
        /// real datetime cells render as readable local times in the spreadsheet's own timezone.
        /// A null <paramref name="endRowIndex"/> means every row from the start downwards.
        ///
        /// Takes a set of columns because both callers want several at once, and one batch keeps
        /// the format arriving atomically rather than column by column.
        /// </summary>
        Task SetDateTimeFormatAsync(string spreadsheetId, string tabName, IReadOnlyList<int> columnIndexes, int startRowIndex, int? endRowIndex, string pattern, CancellationToken cancellationToken = default);

        /// <summary>
        /// The spreadsheet's timezone (CLDR/IANA, e.g. America/New_York). Datetime cells are
        /// wall-clock values interpreted against this, so it must match the backlog's configured
        /// timezone or every stored instant shifts.
        /// </summary>
        Task<string?> GetSpreadsheetTimeZoneAsync(string spreadsheetId, CancellationToken cancellationToken = default);

        Task SetSpreadsheetTimeZoneAsync(string spreadsheetId, string timeZoneId, CancellationToken cancellationToken = default);
    }
}

namespace Noogen.Providers.GoogleWorkspace
{
    /// <summary>
    /// The narrow slice of Sheets the backlog needs. Row indexes are zero-based and include the
    /// header row, matching what <see cref="A1"/> produces.
    /// </summary>
    public interface ISheetsGateway
    {
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

        Task FreezeHeaderAsync(string spreadsheetId, string tabName, CancellationToken cancellationToken = default);

        Task HideColumnsAsync(string spreadsheetId, string tabName, int startColumnIndex, int endColumnIndex, CancellationToken cancellationToken = default);

        /// <summary>
        /// Forces a column to the TEXT number format. Without it Sheets coerces our ISO-8601
        /// timestamp strings into locale-formatted date serials, and reads come back as
        /// something we never wrote.
        /// </summary>
        Task SetColumnTextFormatAsync(string spreadsheetId, string tabName, int columnIndex, CancellationToken cancellationToken = default);
    }
}

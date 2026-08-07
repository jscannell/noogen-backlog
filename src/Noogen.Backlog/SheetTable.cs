namespace Noogen.Backlog
{
    /// <summary>
    /// A lifecycle tab loaded into memory, with columns resolved by header name rather than by
    /// position. Sheets truncates trailing empty cells, so rows are routinely shorter than the
    /// header — every accessor here tolerates that.
    ///
    /// Names resolve through <see cref="SheetSchema.Canonical"/>, so a tab still headed with the
    /// old short names answers to the spelled-out ones and needs no migration.
    /// </summary>
    public class SheetTable
    {
        readonly Dictionary<string, int> _columns = new(StringComparer.OrdinalIgnoreCase);

        public SheetTable(BacklogPhase phase, IList<IList<object>> values)
        {
            Phase = phase;

            if (values.Count == 0)
            {
                Headers = [];
                CanonicalHeaders = [];
                Rows = [];
                return;
            }

            var headers = values[0].Select(cell => cell?.ToString()?.Trim() ?? string.Empty).ToList();
            var canonical = headers.Select(header => SheetSchema.Canonical(header) ?? header).ToList();

            Headers = headers;
            CanonicalHeaders = canonical;

            for (var i = 0; i < headers.Count; i++)
            {
                if (canonical[i].Length > 0 && !_columns.ContainsKey(canonical[i]))
                    _columns[canonical[i]] = i;

                // Also under whatever the sheet actually says, so a column a human added resolves
                // by the name they gave it.
                if (headers[i].Length > 0 && !_columns.ContainsKey(headers[i]))
                    _columns[headers[i]] = i;
            }

            Rows = values.Skip(1).ToList();
        }

        public BacklogPhase Phase { get; }

        /// <summary>The header row as the sheet actually spells it.</summary>
        public IReadOnlyList<string> Headers { get; }

        /// <summary>
        /// The same headers in the same order, each resolved to the name this schema knows it by.
        /// A column we do not own keeps its own text. Anything deciding what to write into a cell
        /// must read this rather than <see cref="Headers"/>, or a legacy tab falls through every
        /// case it has.
        /// </summary>
        public IReadOnlyList<string> CanonicalHeaders { get; }

        public IReadOnlyList<IList<object>> Rows { get; }

        public bool Has(string column) => _columns.ContainsKey(column);

        public int IndexOf(string column)
        {
            if (_columns.TryGetValue(column, out var index))
                return index;

            throw new InvalidOperationException(
                $"Tab '{Phase.TabName()}' has no '{column}' column. Found: {string.Join(", ", Headers.Where(header => header.Length > 0))}. " +
                "Run 'backlog doctor' to check the index against the expected schema.");
        }

        public string ColumnLetter(string column) => Providers.GoogleWorkspace.A1.Column(IndexOf(column));

        /// <summary>
        /// Where this tab's timestamp columns sit, in header order. Each phase carries a different
        /// subset of them, and a human may have removed one, so this reports only what is there.
        /// </summary>
        public IReadOnlyList<int> TimestampColumnIndexes() =>
            SheetSchema.TimestampColumns.Where(Has).Select(IndexOf).ToList();

        /// <summary>
        /// The cell as it came back from the API — a double for a number or date serial, a string
        /// otherwise. Callers that care about numbers must use this rather than
        /// <see cref="Value"/>, because ToString() on a double is culture-sensitive and would
        /// produce a comma decimal separator on a European machine.
        /// </summary>
        public object? Raw(int dataRowIndex, string column)
        {
            if (!Has(column))
                return null;

            var row = Rows[dataRowIndex];
            var index = IndexOf(column);

            return index >= row.Count ? null : row[index];
        }

        public string? Value(int dataRowIndex, string column)
        {
            if (!Has(column))
                return null;

            var row = Rows[dataRowIndex];
            var index = IndexOf(column);
            if (index >= row.Count)
                return null;

            var text = row[index]?.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        /// <summary>Zero-based index into the sheet including the header row — what the gateway wants.</summary>
        public static int SheetRowIndex(int dataRowIndex) => dataRowIndex + 1;

        /// <summary>One-based row number as it appears in A1 notation — what formulas want.</summary>
        public static int SheetRowNumber(int dataRowIndex) => dataRowIndex + 2;

        public int? FindByIdOrDefault(string id)
        {
            for (var i = 0; i < Rows.Count; i++)
            {
                if (string.Equals(Value(i, SheetSchema.Id), id, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return null;
        }

        public IEnumerable<string> Ids()
        {
            for (var i = 0; i < Rows.Count; i++)
            {
                var id = Value(i, SheetSchema.Id);
                if (id is not null)
                    yield return id;
            }
        }
    }
}

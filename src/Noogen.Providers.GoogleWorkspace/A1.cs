using System.Text;

namespace Noogen.Providers.GoogleWorkspace
{
    /// <summary>
    /// A1-notation helpers. Kept in one place so the rest of the codebase can think in
    /// zero-based row/column indexes and never hand-assemble a range string.
    /// </summary>
    public static class A1
    {
        /// <summary>Zero-based column index to letters: 0 =&gt; A, 25 =&gt; Z, 26 =&gt; AA.</summary>
        public static string Column(int zeroBasedIndex)
        {
            if (zeroBasedIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));

            var builder = new StringBuilder();
            var remaining = zeroBasedIndex;

            while (true)
            {
                builder.Insert(0, (char)('A' + (remaining % 26)));
                remaining = (remaining / 26) - 1;
                if (remaining < 0)
                    break;
            }

            return builder.ToString();
        }

        /// <summary>Quotes a tab name for use in a range. Sheets escapes a single quote by doubling it.</summary>
        public static string Tab(string tabName) => $"'{tabName.Replace("'", "''")}'";

        public static string Cell(string tabName, int rowIndex, int columnIndex) =>
            $"{Tab(tabName)}!{Column(columnIndex)}{rowIndex + 1}";

        public static string Row(string tabName, int rowIndex, int columnCount) =>
            $"{Tab(tabName)}!{Column(0)}{rowIndex + 1}:{Column(columnCount - 1)}{rowIndex + 1}";

        public static string WholeTab(string tabName) => Tab(tabName);

        /// <summary>
        /// A single-cell range used as a write anchor. Sheets expands the write to fit the
        /// values supplied, which is what lets one call write a whole block.
        /// </summary>
        public static string Anchor(string tabName, int rowIndex, int columnIndex) => Cell(tabName, rowIndex, columnIndex);
    }
}

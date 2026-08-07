namespace Noogen.Providers.GoogleWorkspace.Tests
{
    /// <summary>
    /// A1 is the only place that builds a range string, so every off-by-one and quoting rule in
    /// the codebase lives or dies here.
    /// </summary>
    public class A1Tests
    {
        [Theory]
        [InlineData(0, "A")]
        [InlineData(1, "B")]
        [InlineData(25, "Z")]
        [InlineData(26, "AA")]
        [InlineData(27, "AB")]
        [InlineData(51, "AZ")]
        [InlineData(52, "BA")]
        [InlineData(701, "ZZ")]
        [InlineData(702, "AAA")]
        public void Column_ZeroBasedIndex_MapsToSpreadsheetLetters(int index, string expected) =>
            Assert.Equal(expected, A1.Column(index));

        [Fact]
        public void Column_NegativeIndex_Throws() =>
            Assert.Throws<ArgumentOutOfRangeException>(() => A1.Column(-1));

        [Fact]
        public void Tab_OrdinaryName_IsQuoted() => Assert.Equal("'Backlog'", A1.Tab("Backlog"));

        [Fact]
        public void Tab_NameContainsAnApostrophe_DoublesIt() =>
            // Sheets escapes a quote by doubling it; anything else silently truncates the range.
            Assert.Equal("'Jason''s work'", A1.Tab("Jason's work"));

        [Fact]
        public void Tab_NameContainsASpace_StaysWithinTheQuotes() =>
            Assert.Equal("'In Progress'", A1.Tab("In Progress"));

        [Fact]
        public void Cell_ZeroBasedRow_IsOneBasedInTheRange() =>
            // Row 0 is the header row, which the sheet calls row 1.
            Assert.Equal("'Backlog'!A1", A1.Cell("Backlog", 0, 0));

        [Fact]
        public void Cell_RowAndColumnBeyondTheFirst_CombinesBoth() =>
            Assert.Equal("'Backlog'!AA12", A1.Cell("Backlog", 11, 26));

        [Fact]
        public void Row_ColumnCount_SpansColumnAToTheLastColumn() =>
            Assert.Equal("'Backlog'!A3:R3", A1.Row("Backlog", 2, 18));

        [Fact]
        public void Row_SingleColumn_StartsAndEndsAtA() =>
            Assert.Equal("'Config'!A1:A1", A1.Row("Config", 0, 1));

        [Fact]
        public void WholeTab_Always_IsJustTheQuotedTabName() =>
            Assert.Equal("'Archive'", A1.WholeTab("Archive"));

        [Fact]
        public void Anchor_Always_IsTheSameSingleCellAsCell() =>
            // A write anchor is a single cell that Sheets expands to fit the values supplied.
            Assert.Equal(A1.Cell("Backlog", 4, 2), A1.Anchor("Backlog", 4, 2));
    }
}

using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Tests
{
    public class SheetTableTests
    {
        static SheetTable Build(params string[] headers)
        {
            IList<IList<object>> values = [headers.Cast<object>().ToList()];
            return new SheetTable(BacklogPhase.Backlog, values);
        }

        [Fact]
        public void Resolves_columns_by_name_not_position()
        {
            var reordered = Build(SheetSchema.Title, SheetSchema.Id, SheetSchema.Wsjf);

            Assert.Equal(1, reordered.IndexOf(SheetSchema.Id));
            Assert.Equal(0, reordered.IndexOf(SheetSchema.Title));
        }

        [Fact]
        public void Tolerates_extra_columns_a_human_added()
        {
            var table = Build(SheetSchema.Id, "epic", SheetSchema.Title);

            Assert.Equal(2, table.IndexOf(SheetSchema.Title));
            Assert.True(table.Has("epic"));
        }

        [Fact]
        public void Explains_itself_when_a_column_is_missing()
        {
            var table = Build(SheetSchema.Id, SheetSchema.Title);
            var exception = Assert.Throws<InvalidOperationException>(() => table.IndexOf(SheetSchema.Wsjf));

            Assert.Contains("wsjf", exception.Message);
            Assert.Contains("backlog doctor", exception.Message);
        }

        [Fact]
        public void Tolerates_rows_shorter_than_the_header()
        {
            // Sheets omits trailing empty cells, so a short row is normal, not corrupt.
            IList<IList<object>> values =
            [
                new List<object> { SheetSchema.Id, SheetSchema.Title, SheetSchema.Owner },
                new List<object> { "NG-0001" }
            ];

            var table = new SheetTable(BacklogPhase.Backlog, values);

            Assert.Equal("NG-0001", table.Value(0, SheetSchema.Id));
            Assert.Null(table.Value(0, SheetSchema.Owner));
        }

        [Fact]
        public void Empty_tab_yields_no_headers_and_no_rows()
        {
            var table = new SheetTable(BacklogPhase.Backlog, []);

            Assert.Empty(table.Headers);
            Assert.Empty(table.Rows);
        }

        [Fact]
        public void Row_index_maths_accounts_for_the_header()
        {
            Assert.Equal(1, SheetTable.SheetRowIndex(0));
            Assert.Equal(2, SheetTable.SheetRowNumber(0));
        }
    }

    public class A1Tests
    {
        [Theory]
        [InlineData(0, "A")]
        [InlineData(25, "Z")]
        [InlineData(26, "AA")]
        [InlineData(27, "AB")]
        [InlineData(51, "AZ")]
        [InlineData(52, "BA")]
        public void Converts_column_indexes_to_letters(int index, string expected) =>
            Assert.Equal(expected, A1.Column(index));

        [Fact]
        public void Quotes_tab_names_containing_an_apostrophe() =>
            Assert.Equal("'Jason''s tab'", A1.Tab("Jason's tab"));

        [Fact]
        public void Builds_a_row_range() =>
            Assert.Equal("'In Progress'!A3:C3", A1.Row("In Progress", 2, 3));
    }

    public class SheetIndexFormulaTests
    {
        static SheetTable BacklogTable()
        {
            IList<IList<object>> values = [SheetSchema.Columns(BacklogPhase.Backlog).Cast<object>().ToList()];
            return new SheetTable(BacklogPhase.Backlog, values);
        }

        [Fact]
        public void Wsjf_formula_blanks_rather_than_dividing_by_zero()
        {
            var formula = SheetIndex.WsjfFormula(BacklogTable(), 2);

            Assert.StartsWith("=IF(OR(", formula);
            Assert.Contains("=0", formula);
            Assert.Contains("ROUND(", formula);
        }

        [Fact]
        public void Rank_formula_blanks_for_unscored_rows()
        {
            var formula = SheetIndex.RankFormula(BacklogTable(), 2);

            Assert.Contains("RANK(", formula);
            Assert.Contains("=\"\",\"\"", formula);
        }

        [Fact]
        public void Formulas_follow_the_resolved_column_letters_not_hardcoded_ones()
        {
            // Put wsjf somewhere unusual; the formula must point at the real column.
            IList<IList<object>> values = [new List<object> { "spacer", SheetSchema.Wsjf, SheetSchema.Size, SheetSchema.Cod }];
            var table = new SheetTable(BacklogPhase.Backlog, values);

            Assert.Contains("B2", SheetIndex.RankFormula(table, 2));
            Assert.Contains("D2", SheetIndex.WsjfFormula(table, 2));
        }

        [Theory]
        [InlineData("=cmd|'/c calc'!A1", "'=cmd|'/c calc'!A1")]
        [InlineData("+1", "'+1")]
        [InlineData("-lead", "'-lead")]
        [InlineData("@here", "'@here")]
        [InlineData("Normal title", "Normal title")]
        public void Neutralises_formula_injection_from_user_text(string input, string expected) =>
            Assert.Equal(expected, SheetIndex.EscapeUserText(input));
    }
}

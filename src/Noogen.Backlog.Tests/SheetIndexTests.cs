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
        public void IndexOf_ColumnsWereReordered_ResolvesByNameNotPosition()
        {
            var reordered = Build(SheetSchema.Title, SheetSchema.Id, SheetSchema.Wsjf);

            Assert.Equal(1, reordered.IndexOf(SheetSchema.Id));
            Assert.Equal(0, reordered.IndexOf(SheetSchema.Title));
        }

        [Fact]
        public void IndexOf_AHumanAddedAColumn_StillResolvesTheKnownOnes()
        {
            var table = Build(SheetSchema.Id, "epic", SheetSchema.Title);

            Assert.Equal(2, table.IndexOf(SheetSchema.Title));
            Assert.True(table.Has("epic"));
        }

        [Fact]
        public void IndexOf_ColumnIsMissing_ThrowsNamingTheColumnAndTheFix()
        {
            var table = Build(SheetSchema.Id, SheetSchema.Title);
            var exception = Assert.Throws<InvalidOperationException>(() => table.IndexOf(SheetSchema.Wsjf));

            Assert.Contains(SheetSchema.Wsjf, exception.Message);
            Assert.Contains("backlog doctor", exception.Message);
        }

        [Fact]
        public void Value_RowIsShorterThanTheHeader_ReturnsNullForTheMissingCells()
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
        public void Constructor_TabIsEmpty_HasNoHeadersAndNoRows()
        {
            var table = new SheetTable(BacklogPhase.Backlog, []);

            Assert.Empty(table.Headers);
            Assert.Empty(table.Rows);
        }

        [Fact]
        public void IndexOf_TabUsesTheLegacyShortHeaders_StillResolvesTheColumn()
        {
            // A backlog created before the columns were spelled out. Its header row is a human's
            // to change, so we read it as it is rather than relabelling it.
            var table = Build("id", "bv", "tc", "rroe", "size", "cod", "doc_id");

            Assert.Equal(1, table.IndexOf(SheetSchema.BusinessValue));
            Assert.Equal(3, table.IndexOf(SheetSchema.RiskOpportunity));
            Assert.Equal(4, table.IndexOf(SheetSchema.JobSize));
            Assert.Equal(6, table.IndexOf(SheetSchema.DriveFileId));
        }

        [Fact]
        public void IndexOf_HeaderIsPunctuatedDifferently_StillResolvesTheColumn()
        {
            var table = Build("blocked_reason", "Business value", "DRIVE FILE ID");

            Assert.Equal(0, table.IndexOf(SheetSchema.BlockedReason));
            Assert.Equal(1, table.IndexOf(SheetSchema.BusinessValue));
            Assert.Equal(2, table.IndexOf(SheetSchema.DriveFileId));
        }

        [Fact]
        public void IndexOf_TabHasBothIdAndDocId_KeepsTheTwoApart()
        {
            // The ticket id and Drive's file id are unrelated; nothing may collapse them.
            var table = Build("id", "doc_id");

            Assert.Equal(0, table.IndexOf(SheetSchema.Id));
            Assert.Equal(1, table.IndexOf(SheetSchema.DriveFileId));
        }

        [Fact]
        public void CanonicalHeaders_TabMixesLegacyAndSpelledOutHeaders_ReportsBothUnderTheSchemaName()
        {
            var table = Build("bv", SheetSchema.TimeCriticality, "epic");

            Assert.Equal([SheetSchema.BusinessValue, SheetSchema.TimeCriticality, "epic"], table.CanonicalHeaders);

            // A column we do not own keeps its own text, and still resolves by it.
            Assert.True(table.Has("epic"));
        }

        [Fact]
        public void Value_TabUsesTheLegacyShortHeaders_ReadsTheCell()
        {
            IList<IList<object>> values =
            [
                new List<object> { "id", "bv" },
                new List<object> { "NG-0001", 8d }
            ];

            var table = new SheetTable(BacklogPhase.Backlog, values);

            Assert.Equal("NG-0001", table.Value(0, SheetSchema.Id));
            Assert.Equal(8d, table.Raw(0, SheetSchema.BusinessValue));
        }

        [Fact]
        public void SheetRowIndex_FirstDataRow_SkipsTheHeaderRow() => Assert.Equal(1, SheetTable.SheetRowIndex(0));

        [Fact]
        public void SheetRowNumber_FirstDataRow_IsTheOneBasedRowAHumanSees() => Assert.Equal(2, SheetTable.SheetRowNumber(0));
    }

    public class SheetSchemaTests
    {
        [Theory]
        [InlineData("bv", SheetSchema.BusinessValue)]
        [InlineData("tc", SheetSchema.TimeCriticality)]
        [InlineData("rroe", SheetSchema.RiskOpportunity)]
        [InlineData("size", SheetSchema.JobSize)]
        [InlineData("cod", SheetSchema.CostOfDelay)]
        [InlineData("lead_days", SheetSchema.LeadTime)]
        [InlineData("cycle_days", SheetSchema.CycleTime)]
        [InlineData("doc_id", SheetSchema.DriveFileId)]
        [InlineData("doc_url", SheetSchema.DriveFileLink)]
        public void Canonical_HeaderIsALegacyShortName_ResolvesToTheSpelledOutName(string legacy, string expected) =>
            Assert.Equal(expected, SheetSchema.Canonical(legacy));

        [Theory]
        [InlineData("blocked_reason")]
        [InlineData("BLOCKED REASON")]
        [InlineData("  Blocked-Reason  ")]
        public void Canonical_HeaderIsPunctuatedDifferently_ResolvesToTheSpelledOutName(string written) =>
            Assert.Equal(SheetSchema.BlockedReason, SheetSchema.Canonical(written));

        [Fact]
        public void Canonical_HeaderIsIdOrDocId_KeepsTheTwoApart()
        {
            Assert.Equal(SheetSchema.Id, SheetSchema.Canonical("id"));
            Assert.Equal(SheetSchema.DriveFileId, SheetSchema.Canonical("doc_id"));
        }

        [Theory]
        [InlineData("epic")]
        [InlineData("")]
        [InlineData(null)]
        public void Canonical_ColumnIsNotOneOfOurs_ReturnsNullSoItIsLeftAlone(string? header) =>
            Assert.Null(SheetSchema.Canonical(header));

        [Fact]
        public void Canonical_EverySpelledOutName_RoundTripsToItself()
        {
            foreach (var phase in BacklogPhaseExtensions.All)
            {
                foreach (var column in SheetSchema.Columns(phase))
                    Assert.Equal(column, SheetSchema.Canonical(column));
            }
        }
    }

    public class SheetIndexFormulaTests
    {
        static SheetTable BacklogTable()
        {
            IList<IList<object>> values = [SheetSchema.Columns(BacklogPhase.Backlog).Cast<object>().ToList()];
            return new SheetTable(BacklogPhase.Backlog, values);
        }

        static SheetTable ReorderedTable()
        {
            // Put wsjf somewhere unusual; the formula must point at the real column.
            IList<IList<object>> values = [new List<object> { "spacer", SheetSchema.Wsjf, SheetSchema.JobSize, SheetSchema.CostOfDelay }];
            return new SheetTable(BacklogPhase.Backlog, values);
        }

        [Fact]
        public void WsjfFormula_Always_BlanksRatherThanDividingByZero()
        {
            var formula = SheetIndex.WsjfFormula(BacklogTable(), 2);

            Assert.StartsWith("=IF(OR(", formula);
            Assert.Contains("=0", formula);
            Assert.Contains("ROUND(", formula);
        }

        [Fact]
        public void RankFormula_Always_BlanksForUnscoredRows()
        {
            var formula = SheetIndex.RankFormula(BacklogTable(), 2);

            Assert.Contains("RANK(", formula);
            Assert.Contains("=\"\",\"\"", formula);
        }

        [Fact]
        public void WsjfFormula_ColumnsWereReordered_PointsAtTheResolvedColumnLetters() =>
            Assert.Contains("D2", SheetIndex.WsjfFormula(ReorderedTable(), 2));

        [Fact]
        public void RankFormula_ColumnsWereReordered_PointsAtTheResolvedColumnLetters() =>
            Assert.Contains("B2", SheetIndex.RankFormula(ReorderedTable(), 2));

        [Fact]
        public void BuildRow_TabUsesTheLegacyShortHeaders_FillsEveryCellRatherThanBlankingThem()
        {
            // The trap: BuildCell matches on the schema's names, so walking the literal header row
            // of a legacy tab would match nothing and write the ticket back as a row of blanks.
            var index = new SheetIndex(new FakeSheetsGateway(), "sheet");
            var table = LegacyBacklogTable();

            var ticket = new Ticket
            {
                Id = "NG-0001",
                Title = "Something",
                Area = "platform",
                DocId = "file-1",
                Score = new WsjfScore { BusinessValue = 8, TimeCriticality = 3, RiskReductionOpportunityEnablement = 2, JobSize = 5 }
            };

            var row = index.BuildRow(table, ticket, 0);

            Assert.Equal("NG-0001", row[table.IndexOf(SheetSchema.Id)]);
            Assert.Equal("Something", row[table.IndexOf(SheetSchema.Title)]);
            Assert.Equal(8, row[table.IndexOf(SheetSchema.BusinessValue)]);
            Assert.Equal(5, row[table.IndexOf(SheetSchema.JobSize)]);
            Assert.Equal("file-1", row[table.IndexOf(SheetSchema.DriveFileId)]);

            // The Sheet still owns these, and the formulas point at the legacy columns' letters.
            Assert.StartsWith("=", (string)row[table.IndexOf(SheetSchema.CostOfDelay)]);
            Assert.StartsWith("=", (string)row[table.IndexOf(SheetSchema.Wsjf)]);
            Assert.StartsWith("=", (string)row[table.IndexOf(SheetSchema.Rank)]);
        }

        [Fact]
        public void BuildRow_TabMixesLegacyAndSpelledOutHeaders_FillsBothAndLeavesHumanColumnsAlone()
        {
            var index = new SheetIndex(new FakeSheetsGateway(), "sheet");

            IList<IList<object>> values = [new List<object> { "id", SheetSchema.Title, "bv", "epic" }];
            var table = new SheetTable(BacklogPhase.Backlog, values);

            var ticket = new Ticket
            {
                Id = "NG-0001",
                Title = "Something",
                Score = new WsjfScore { BusinessValue = 8 }
            };

            var row = index.BuildRow(table, ticket, 0);

            Assert.Equal("NG-0001", row[0]);
            Assert.Equal("Something", row[1]);
            Assert.Equal(8, row[2]);
            Assert.Equal(string.Empty, row[3]);
        }

        static SheetTable LegacyBacklogTable()
        {
            IList<IList<object>> values =
            [
                new List<object>
                {
                    "id", "title", "type", "area", "owner", "bv", "tc", "rroe", "size", "cod",
                    "wsjf", "rank", "created", "updated", "doc_id", "doc_url"
                }
            ];

            return new SheetTable(BacklogPhase.Backlog, values);
        }

        [Theory]
        [InlineData("=cmd|'/c calc'!A1", "'=cmd|'/c calc'!A1")]
        [InlineData("+1", "'+1")]
        [InlineData("-lead", "'-lead")]
        [InlineData("@here", "'@here")]
        public void EscapeUserText_TextStartsWithAFormulaCharacter_PrefixesAnApostrophe(string input, string expected) =>
            Assert.Equal(expected, SheetIndex.EscapeUserText(input));

        [Fact]
        public void EscapeUserText_OrdinaryTitle_IsLeftAlone() =>
            Assert.Equal("Normal title", SheetIndex.EscapeUserText("Normal title"));
    }
}

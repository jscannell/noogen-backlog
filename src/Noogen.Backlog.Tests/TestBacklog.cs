namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// Spins up an initialised backlog over the in-memory fakes so each test starts from the
    /// same shape a real `backlog init` produces.
    /// </summary>
    public class TestBacklog
    {
        public const string SpreadsheetId = "sheet-1";
        public const string RootFolderId = "root";

        TestBacklog()
        {
        }

        public FakeSheetsGateway Sheets { get; private set; } = null!;

        public FakeDriveGateway Drive { get; private set; } = null!;

        public BacklogStore Store { get; private set; } = null!;

        public TestClock Clock { get; private set; } = null!;

        public InitResult Init { get; private set; } = null!;

        public static async Task<TestBacklog> CreateAsync(DateTimeOffset? now = null, int wipLimit = 5)
        {
            var backlog = new TestBacklog
            {
                Sheets = new FakeSheetsGateway(),
                Drive = new FakeDriveGateway(),
                Clock = new TestClock(now ?? new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero))
            };

            var initializer = new BacklogInitializer(backlog.Drive, backlog.Sheets);
            backlog.Init = await initializer.RunAsync(RootFolderId, SpreadsheetId);

            backlog.Store = new BacklogStore(backlog.Sheets, backlog.Drive, SpreadsheetId, backlog.Clock);

            if (wipLimit != 5)
                await backlog.SetWipLimitAsync(wipLimit);

            return backlog;
        }

        /// <summary>Writes the policy to the Config tab rather than poking the store, so the
        /// test exercises the same path a human editing the Sheet would.</summary>
        public async Task SetWipLimitAsync(int limit)
        {
            var rows = Sheets.Rows(SheetSchema.ConfigTabName);

            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Count > 0 && string.Equals(rows[i][0]?.ToString(), BacklogSettings.WipLimitKey, StringComparison.OrdinalIgnoreCase))
                {
                    await Sheets.UpdateValuesAsync(
                        SpreadsheetId,
                        Providers.GoogleWorkspace.A1.Cell(SheetSchema.ConfigTabName, i, 1),
                        [[limit]]);
                    return;
                }
            }

            throw new InvalidOperationException($"Config tab has no '{BacklogSettings.WipLimitKey}' row.");
        }

        public Task<Ticket> AddAsync(string title, int? bv = 8, int? tc = 3, int? rroe = 2, int? size = 5, string area = "agent", string? owner = null) =>
            Store.CreateAsync(new NewTicket
            {
                Title = title,
                Area = area,
                Owner = owner,
                Score = new WsjfScore
                {
                    BusinessValue = bv,
                    TimeCriticality = tc,
                    RiskReductionOpportunityEnablement = rroe,
                    JobSize = size
                }
            });

        /// <summary>Raw cell text for a ticket, so tests can assert formula vs frozen value.</summary>
        public string CellText(BacklogPhase phase, string ticketId, string column)
        {
            var rows = Sheets.Rows(phase.TabName());
            var headers = rows[0].Select(cell => cell?.ToString() ?? string.Empty).ToList();

            var idIndex = headers.IndexOf(SheetSchema.Id);
            var columnIndex = headers.IndexOf(column);

            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (idIndex < row.Count && string.Equals(row[idIndex]?.ToString(), ticketId, StringComparison.OrdinalIgnoreCase))
                    return columnIndex < row.Count ? row[columnIndex]?.ToString() ?? string.Empty : string.Empty;
            }

            return string.Empty;
        }

        public int RowCount(BacklogPhase phase) => Math.Max(0, Sheets.Rows(phase.TabName()).Count - 1);
    }
}

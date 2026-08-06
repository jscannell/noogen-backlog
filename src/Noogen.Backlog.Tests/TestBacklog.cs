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

        public static async Task<TestBacklog> CreateAsync(DateTimeOffset? now = null, int wipLimit = 5, string timeZoneId = "UTC")
        {
            var clock = new TestClock(now ?? new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));

            var backlog = new TestBacklog
            {
                Sheets = new FakeSheetsGateway(),
                Drive = new FakeDriveGateway { Clock = clock },
                Clock = clock
            };

            var initializer = new BacklogInitializer(backlog.Drive, backlog.Sheets);
            backlog.Init = await initializer.RunAsync(RootFolderId, SpreadsheetId, timeZoneId);

            backlog.Store = new BacklogStore(backlog.Sheets, backlog.Drive, SpreadsheetId, backlog.Clock);

            if (wipLimit != 5)
                await backlog.SetWipLimitAsync(wipLimit);

            return backlog;
        }

        public TimeZoneInfo Zone => SheetTime.ResolveZone(Sheets.TimeZoneId);

        /// <summary>Writes the policy to the Config tab rather than poking the store, so the
        /// test exercises the same path a human editing the Sheet would.</summary>
        public Task SetWipLimitAsync(int limit) => SetConfigAsync(BacklogSettings.WipLimitKey, limit);

        public async Task SetConfigAsync(string key, object value)
        {
            var rows = Sheets.Rows(SheetSchema.ConfigTabName);

            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Count > 0 && string.Equals(rows[i][0]?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                {
                    await Sheets.UpdateValuesAsync(
                        SpreadsheetId,
                        Providers.GoogleWorkspace.A1.Cell(SheetSchema.ConfigTabName, i, 1),
                        [[value]]);
                    return;
                }
            }

            throw new InvalidOperationException($"Config tab has no '{key}' row.");
        }

        /// <summary>A store with no cached settings, for asserting on a changed Config tab.</summary>
        public BacklogStore FreshStore() => new(Sheets, Drive, SpreadsheetId, Clock);

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

        /// <summary>The cell as stored — a double for a serial, a string for text or a formula.</summary>
        public object? RawCell(BacklogPhase phase, string ticketId, string column)
        {
            var rows = Sheets.Rows(phase.TabName());
            var headers = rows[0].Select(cell => cell?.ToString() ?? string.Empty).ToList();

            var idIndex = headers.IndexOf(SheetSchema.Id);
            var columnIndex = headers.IndexOf(column);

            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (idIndex < row.Count && string.Equals(row[idIndex]?.ToString(), ticketId, StringComparison.OrdinalIgnoreCase))
                    return columnIndex < row.Count ? row[columnIndex] : null;
            }

            return null;
        }

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

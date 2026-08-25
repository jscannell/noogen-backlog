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

        /// <summary>
        /// The seam both front ends sit on, over the same fakes and the same clock. Tests that
        /// care what an *answer* is use this; tests that care what reaches the Sheet use
        /// <see cref="Store"/>.
        /// </summary>
        public BacklogApi Api => new(Store, () => Clock.Now);

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

        /// <summary>
        /// Rewrites every header row the way a backlog created before the columns were spelled out
        /// still has it. Nothing in the product does this — it is how the tests get their hands on
        /// an old spreadsheet, which we read as-is rather than relabelling.
        /// </summary>
        public async Task UseLegacyHeadersAsync()
        {
            var legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [SheetSchema.BusinessValue] = "bv",
                [SheetSchema.TimeCriticality] = "tc",
                [SheetSchema.RiskOpportunity] = "rroe",
                [SheetSchema.JobSize] = "size",
                [SheetSchema.CostOfDelay] = "cod",
                [SheetSchema.LeadTime] = "lead_days",
                [SheetSchema.CycleTime] = "cycle_days",
                [SheetSchema.DriveFileId] = "doc_id",
                [SheetSchema.DriveFileLink] = "doc_url"
            };

            foreach (var phase in BacklogPhaseExtensions.All)
            {
                var headers = Sheets.Rows(phase.TabName())[0]
                    .Select(cell => cell?.ToString() ?? string.Empty)
                    .Select(header => legacy.TryGetValue(header, out var old) ? old : header.ToLowerInvariant().Replace(' ', '_'))
                    .Cast<object>()
                    .ToList();

                await Sheets.UpdateValuesAsync(
                    SpreadsheetId,
                    Providers.GoogleWorkspace.A1.Anchor(phase.TabName(), 0, 0),
                    [headers]);
            }
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

        /// <summary>The header row resolved to the schema's names, so a cell lookup works whether
        /// the tab is headed the current way or the legacy one.</summary>
        List<string> HeadersOf(BacklogPhase phase) =>
            Sheets.Rows(phase.TabName())[0]
                .Select(cell => cell?.ToString() ?? string.Empty)
                .Select(header => SheetSchema.Canonical(header) ?? header)
                .ToList();

        /// <summary>The cell as stored — a double for a serial, a string for text or a formula.</summary>
        public object? RawCell(BacklogPhase phase, string ticketId, string column)
        {
            var rows = Sheets.Rows(phase.TabName());
            var headers = HeadersOf(phase);

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
            var headers = HeadersOf(phase);

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

        /// <summary>
        /// Writes one cell of a ticket's row directly, which is how a test gets its hands on a row
        /// damaged the way a human editing the Sheet could damage it. Nothing in the product does
        /// this — the store always writes a whole row.
        /// </summary>
        public async Task SetCellAsync(BacklogPhase phase, string ticketId, string column, object value)
        {
            var rows = Sheets.Rows(phase.TabName());
            var headers = HeadersOf(phase);

            var idIndex = headers.IndexOf(SheetSchema.Id);
            var columnIndex = headers.IndexOf(column);

            for (var i = 1; i < rows.Count; i++)
            {
                if (idIndex < rows[i].Count && string.Equals(rows[i][idIndex]?.ToString(), ticketId, StringComparison.OrdinalIgnoreCase))
                {
                    await Sheets.UpdateValuesAsync(
                        SpreadsheetId,
                        Providers.GoogleWorkspace.A1.Cell(phase.TabName(), i, columnIndex),
                        [[value]]);
                    return;
                }
            }

            throw new InvalidOperationException($"No row for '{ticketId}' on the {phase.TabName()} tab.");
        }

        /// <summary>
        /// The DATE_TIME formats that reach a ticket's cell. A number format is not cell content,
        /// so this asks the recorded calls rather than the grid — and it returns them all, because
        /// what matters is <em>which</em> call covers the cell: a column-wide one does not survive
        /// a row being inserted underneath it, and a row-scoped one is the whole point.
        /// </summary>
        public IReadOnlyList<DateTimeFormatCall> DateTimeFormatsCovering(BacklogPhase phase, string ticketId, string column)
        {
            var rows = Sheets.Rows(phase.TabName());
            var headers = HeadersOf(phase);

            var idIndex = headers.IndexOf(SheetSchema.Id);
            var columnIndex = headers.IndexOf(column);

            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (idIndex >= row.Count || !string.Equals(row[idIndex]?.ToString(), ticketId, StringComparison.OrdinalIgnoreCase))
                    continue;

                return Sheets.DateTimeFormats
                    .Where(call => string.Equals(call.TabName, phase.TabName(), StringComparison.OrdinalIgnoreCase) && call.Covers(i, columnIndex))
                    .ToList();
            }

            return [];
        }
    }
}

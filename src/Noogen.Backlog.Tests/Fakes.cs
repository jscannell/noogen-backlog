using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Tests
{
    public class TestClock : TimeProvider
    {
        public TestClock(DateTimeOffset now)
        {
            Now = now;
        }

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan amount) => Now += amount;
    }

    class RangeRef
    {
        public string Tab { get; set; } = string.Empty;

        public int StartRow { get; set; }

        public int StartColumn { get; set; }

        public bool WholeTab { get; set; }
    }

    /// <summary>One recorded <c>SetDateTimeFormatAsync</c> call.</summary>
    public class DateTimeFormatCall
    {
        public string TabName { get; set; } = string.Empty;

        public IReadOnlyList<int> ColumnIndexes { get; set; } = [];

        public int StartRowIndex { get; set; }

        /// <summary>Null for an unbounded range — every row from the start downwards.</summary>
        public int? EndRowIndex { get; set; }

        public string Pattern { get; set; } = string.Empty;

        public bool Covers(int sheetRowIndex, int columnIndex) =>
            ColumnIndexes.Contains(columnIndex)
            && sheetRowIndex >= StartRowIndex
            && (EndRowIndex is null || sheetRowIndex < EndRowIndex);
    }

    /// <summary>
    /// In-memory Sheets. Stores raw cell content, so a formula written by the index is readable
    /// back as its formula text — which is exactly what lets the tests assert that the Backlog
    /// tab gets formulas and the other tabs get frozen values.
    /// </summary>
    public class FakeSheetsGateway : ISheetsGateway
    {
        readonly Dictionary<string, List<List<object>>> _tabs = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tab order is part of the shape init produces, so the fake tracks it.</summary>
        readonly List<string> _order = [];

        public Dictionary<string, string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Number formats are not cell content, so the fake records the calls rather than the
        /// grid. The row range matters as much as the column: an appended row is inserted
        /// unformatted, so a format that only ever covered the column would miss it.
        /// </summary>
        public List<DateTimeFormatCall> DateTimeFormats { get; } = [];

        public List<string> HiddenColumns { get; } = [];

        /// <summary>Mirrors the spreadsheet's own timeZone property, which init sets.</summary>
        public string? TimeZoneId { get; set; }

        /// <summary>Set to make the next DeleteRow throw, simulating an interrupted cross-tab move.</summary>
        public bool FailNextDeleteRow { get; set; }

        public int DeleteRowCallCount { get; private set; }

        public int AppendRowCallCount { get; private set; }

        public IReadOnlyList<IReadOnlyList<object>> Rows(string tabName) => Tab(tabName);

        /// <summary>Stands in for the default tab a freshly created spreadsheet arrives with.</summary>
        public void SeedTab(string tabName) => Tab(tabName);

        List<List<object>> Tab(string tabName)
        {
            if (!_tabs.TryGetValue(tabName, out var rows))
            {
                rows = [];
                _tabs[tabName] = rows;
                _order.Add(tabName);
            }

            return rows;
        }

        int OrderIndexOf(string tabName) =>
            _order.FindIndex(name => string.Equals(name, tabName, StringComparison.OrdinalIgnoreCase));

        public Task<IList<IList<object>>> GetValuesAsync(string spreadsheetId, string range, CancellationToken cancellationToken = default)
        {
            var reference = Parse(range);
            var rows = Tab(reference.Tab);

            var result = new List<IList<object>>();
            var from = reference.WholeTab ? 0 : reference.StartRow;

            for (var i = from; i < rows.Count; i++)
                result.Add(TrimTrailingEmpty(rows[i]));

            return Task.FromResult<IList<IList<object>>>(result);
        }

        public Task UpdateValuesAsync(string spreadsheetId, string range, IList<IList<object>> values, CancellationToken cancellationToken = default)
        {
            var reference = Parse(range);
            var rows = Tab(reference.Tab);

            for (var offset = 0; offset < values.Count; offset++)
            {
                var targetIndex = reference.StartRow + offset;
                while (rows.Count <= targetIndex)
                    rows.Add([]);

                var target = rows[targetIndex];
                var source = values[offset];

                for (var column = 0; column < source.Count; column++)
                {
                    var absolute = reference.StartColumn + column;
                    while (target.Count <= absolute)
                        target.Add(string.Empty);

                    target[absolute] = source[column];
                }
            }

            return Task.CompletedTask;
        }

        public Task<int> AppendRowAsync(string spreadsheetId, string tabName, IList<object> values, CancellationToken cancellationToken = default)
        {
            AppendRowCallCount++;

            var rows = Tab(tabName);
            rows.Add([.. values]);

            return Task.FromResult(rows.Count - 1);
        }

        public Task DeleteRowAsync(string spreadsheetId, string tabName, int rowIndex, CancellationToken cancellationToken = default)
        {
            DeleteRowCallCount++;

            if (FailNextDeleteRow)
            {
                FailNextDeleteRow = false;
                throw new IOException("simulated transport failure between append and delete");
            }

            var rows = Tab(tabName);
            if (rowIndex >= 0 && rowIndex < rows.Count)
                rows.RemoveAt(rowIndex);

            return Task.CompletedTask;
        }

        public Task SetCellLinkAsync(string spreadsheetId, string tabName, int rowIndex, int columnIndex, string text, string uri, CancellationToken cancellationToken = default)
        {
            var rows = Tab(tabName);
            while (rows.Count <= rowIndex)
                rows.Add([]);

            var row = rows[rowIndex];
            while (row.Count <= columnIndex)
                row.Add(string.Empty);

            // The cell keeps the plain string; the link rides alongside it, exactly as a
            // textFormatRun does in the real API.
            row[columnIndex] = text;
            Links[$"{tabName}!{rowIndex},{columnIndex}"] = uri;

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListTabsAsync(string spreadsheetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_order.ToList());

        public Task EnsureTabAsync(string spreadsheetId, string tabName, CancellationToken cancellationToken = default)
        {
            Tab(tabName);
            return Task.CompletedTask;
        }

        public Task RenameTabAsync(string spreadsheetId, string tabName, string newTabName, CancellationToken cancellationToken = default)
        {
            var rows = Tab(tabName);

            _order[OrderIndexOf(tabName)] = newTabName;
            _tabs.Remove(tabName);
            _tabs[newTabName] = rows;

            return Task.CompletedTask;
        }

        public Task SetTabOrderAsync(string spreadsheetId, IReadOnlyList<string> tabNames, CancellationToken cancellationToken = default)
        {
            var position = 0;

            foreach (var tabName in tabNames)
            {
                var current = OrderIndexOf(tabName);
                if (current < 0)
                    continue;

                _order.RemoveAt(current);
                _order.Insert(position, tabName);
                position++;
            }

            return Task.CompletedTask;
        }

        public Task FreezeHeaderAsync(string spreadsheetId, string tabName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task HideColumnsAsync(string spreadsheetId, string tabName, int startColumnIndex, int endColumnIndex, CancellationToken cancellationToken = default)
        {
            HiddenColumns.Add($"{tabName}!{startColumnIndex}");
            return Task.CompletedTask;
        }

        public Task SetDateTimeFormatAsync(string spreadsheetId, string tabName, IReadOnlyList<int> columnIndexes, int startRowIndex, int? endRowIndex, string pattern, CancellationToken cancellationToken = default)
        {
            if (columnIndexes.Count == 0)
                return Task.CompletedTask;

            var call = new DateTimeFormatCall();
            call.TabName = tabName;
            call.ColumnIndexes = [.. columnIndexes];
            call.StartRowIndex = startRowIndex;
            call.EndRowIndex = endRowIndex;
            call.Pattern = pattern;

            DateTimeFormats.Add(call);
            return Task.CompletedTask;
        }

        public Task<string?> GetSpreadsheetTimeZoneAsync(string spreadsheetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(TimeZoneId);

        public Task SetSpreadsheetTimeZoneAsync(string spreadsheetId, string timeZoneId, CancellationToken cancellationToken = default)
        {
            TimeZoneId = timeZoneId;
            return Task.CompletedTask;
        }

        static List<object> TrimTrailingEmpty(List<object> row)
        {
            // Sheets omits trailing empty cells; reproducing that keeps SheetTable honest.
            var last = row.Count - 1;
            while (last >= 0 && string.IsNullOrEmpty(row[last]?.ToString()))
                last--;

            return row.Take(last + 1).ToList();
        }

        static RangeRef Parse(string range)
        {
            var bang = range.LastIndexOf('!');

            var tabPart = bang >= 0 ? range[..bang] : range;
            var tab = tabPart.Trim('\'').Replace("''", "'");

            if (bang < 0)
                return new RangeRef { Tab = tab, WholeTab = true };

            var cells = range[(bang + 1)..];
            var colon = cells.IndexOf(':');
            var start = colon >= 0 ? cells[..colon] : cells;

            var letters = new string(start.TakeWhile(char.IsLetter).ToArray());
            var digits = new string(start.SkipWhile(char.IsLetter).ToArray());

            return new RangeRef
            {
                Tab = tab,
                StartColumn = ColumnIndex(letters),
                StartRow = int.TryParse(digits, out var parsed) ? parsed - 1 : 0
            };
        }

        static int ColumnIndex(string letters)
        {
            var index = 0;
            foreach (var letter in letters.ToUpperInvariant())
                index = (index * 26) + (letter - 'A' + 1);

            return index - 1;
        }
    }

    public class FakeDriveGateway : IDriveGateway
    {
        class Node
        {
            public string Id { get; set; } = string.Empty;

            public string Name { get; set; } = string.Empty;

            public string ParentId { get; set; } = string.Empty;

            public string MimeType { get; set; } = string.Empty;

            public string Content { get; set; } = string.Empty;

            public DateTimeOffset CreatedTime { get; set; }

            public DateTimeOffset ModifiedTime { get; set; }
        }

        readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
        int _nextId = 1;

        public string NewId() => $"file-{_nextId++}";

        public string ParentOf(string fileId) => _nodes[fileId].ParentId;

        public string ContentOf(string fileId) => _nodes[fileId].Content;

        public bool Exists(string fileId) => _nodes.ContainsKey(fileId);

        public int FileCount => _nodes.Count;

        public Task<string?> FindChildAsync(string parentId, string name, string? mimeType, CancellationToken cancellationToken = default)
        {
            var match = _nodes.Values.FirstOrDefault(node =>
                string.Equals(node.ParentId, parentId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)
                && (mimeType is null || node.MimeType == mimeType));

            return Task.FromResult(match?.Id);
        }

        public Task<IReadOnlyList<DriveEntry>> ListChildrenAsync(string parentId, string? mimeType, CancellationToken cancellationToken = default)
        {
            var children = _nodes.Values
                .Where(node => string.Equals(node.ParentId, parentId, StringComparison.OrdinalIgnoreCase))
                .Where(node => mimeType is null || node.MimeType == mimeType)
                .Select(node => new DriveEntry { Id = node.Id, Name = node.Name })
                .ToList();

            return Task.FromResult<IReadOnlyList<DriveEntry>>(children);
        }

        /// <summary>
        /// Every <c>SearchTextAsync</c> call, so a test can assert the drive it was confined to.
        /// </summary>
        public List<string?> SearchDriveIds { get; } = [];

        /// <summary>Set to make the fake behave like Drive's index before it has caught up.</summary>
        public bool SearchFindsNothing { get; set; }

        /// <summary>
        /// Whole-word matching, because that is what Drive does and a fake that matched substrings
        /// would let a test pass that the real index would fail. Splitting on non-alphanumerics
        /// mirrors how Drive tokenises, so `sign-in` is indexed as `sign` and `in`.
        /// </summary>
        public Task<IReadOnlyList<DriveEntry>> SearchTextAsync(string text, string? mimeType, string? driveId, CancellationToken cancellationToken = default)
        {
            SearchDriveIds.Add(driveId);

            if (SearchFindsNothing)
                return Task.FromResult<IReadOnlyList<DriveEntry>>([]);

            var terms = Tokenize(text);

            var hits = _nodes.Values
                .Where(node => mimeType is null || node.MimeType == mimeType)
                .Where(node => terms.Count > 0 && terms.All(term => Tokenize(node.Content).Contains(term)))
                .Select(node => new DriveEntry { Id = node.Id, Name = node.Name })
                .ToList();

            return Task.FromResult<IReadOnlyList<DriveEntry>>(hits);
        }

        static HashSet<string> Tokenize(string text)
        {
            var words = text.Split(
                [' ', '\t', '\r', '\n', '-', '_', '.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '*', '#', '/', '\\', '"', '\'', '`', '|', '<', '>', '=', '+'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            return new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The whole fake is one drive, named so a test can assert the query was confined.</summary>
        public string? DriveId { get; set; } = "shared-drive-1";

        public Task<string?> GetDriveIdAsync(string fileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(DriveId);

        public Task<string> CreateFolderAsync(string parentId, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Add(parentId, name, DriveGateway.FolderMimeType, string.Empty));

        public Task<string> CreateSpreadsheetAsync(string parentId, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Add(parentId, name, DriveGateway.SpreadsheetMimeType, string.Empty));

        /// <summary>
        /// Stores the markdown verbatim under the Doc mime type. The real Drive converts in both
        /// directions and is not byte-exact; that fidelity is the gateway's problem and is asserted
        /// against the stub transport, so above the seam these fakes keep the content stable.
        /// </summary>
        public Task<string> CreateDocAsync(string parentId, string name, string markdown, CancellationToken cancellationToken = default) =>
            Task.FromResult(Add(parentId, name, DriveGateway.DocumentMimeType, markdown));

        public Task<string> ReadDocAsync(string fileId, CancellationToken cancellationToken = default) =>
            _nodes.TryGetValue(fileId, out var node)
                ? Task.FromResult(node.Content)
                : throw new FileNotFoundException($"No such Drive file '{fileId}'.");

        public Task UpdateDocAsync(string fileId, string markdown, CancellationToken cancellationToken = default)
        {
            _nodes[fileId].Content = markdown;
            _nodes[fileId].ModifiedTime = Clock.GetUtcNow();

            return Task.CompletedTask;
        }

        public Task MoveAsync(string fileId, string addParentId, string removeParentId, CancellationToken cancellationToken = default)
        {
            _nodes[fileId].ParentId = addParentId;
            return Task.CompletedTask;
        }

        public Task<string> GetWebViewLinkAsync(string fileId, CancellationToken cancellationToken = default) =>
            Task.FromResult($"https://drive.google.com/file/d/{fileId}/view");

        public Task<DriveFileTimes> GetTimestampsAsync(string fileId, CancellationToken cancellationToken = default)
        {
            var node = _nodes[fileId];

            return Task.FromResult(new DriveFileTimes
            {
                CreatedTime = node.CreatedTime,
                ModifiedTime = node.ModifiedTime
            });
        }

        /// <summary>Lets a test drive Drive's own clock, which reindex trusts over the document.</summary>
        public TimeProvider Clock { get; set; } = TimeProvider.System;

        public void SetTimestamps(string fileId, DateTimeOffset created, DateTimeOffset modified)
        {
            _nodes[fileId].CreatedTime = created;
            _nodes[fileId].ModifiedTime = modified;
        }

        string Add(string parentId, string name, string mimeType, string content)
        {
            var id = NewId();
            var now = Clock.GetUtcNow();

            _nodes[id] = new Node
            {
                Id = id,
                Name = name,
                ParentId = parentId,
                MimeType = mimeType,
                Content = content,
                CreatedTime = now,
                ModifiedTime = now
            };

            return id;
        }
    }
}

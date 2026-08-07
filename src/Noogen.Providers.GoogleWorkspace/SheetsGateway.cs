using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;

namespace Noogen.Providers.GoogleWorkspace
{
    public class SheetsGateway : ISheetsGateway
    {
        readonly ISheetsClientFactory _factory;
        readonly Dictionary<string, int> _tabIdCache = [];

        public SheetsGateway(ISheetsClientFactory factory)
        {
            _factory = factory;
        }

        SheetsService Service => _factory.Create();

        public async Task<IList<IList<object>>> GetValuesAsync(string spreadsheetId, string range, CancellationToken cancellationToken = default)
        {
            var request = Service.Spreadsheets.Values.Get(spreadsheetId, range);

            // UNFORMATTED_VALUE keeps numbers as numbers and dates as serials. FORMATTED_VALUE
            // would hand back locale-rendered text, so a reader in a different locale would parse
            // different values out of the same sheet.
            request.ValueRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.UNFORMATTEDVALUE;
            request.DateTimeRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.DateTimeRenderOptionEnum.SERIALNUMBER;

            var response = await request.ExecuteAsync(cancellationToken);
            return response.Values ?? [];
        }

        public async Task UpdateValuesAsync(string spreadsheetId, string range, IList<IList<object>> values, CancellationToken cancellationToken = default)
        {
            var body = new ValueRange { Values = values };

            var request = Service.Spreadsheets.Values.Update(body, spreadsheetId, range);
            request.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

            await request.ExecuteAsync(cancellationToken);
        }

        public async Task<int> AppendRowAsync(string spreadsheetId, string tabName, IList<object> values, CancellationToken cancellationToken = default)
        {
            var body = new ValueRange { Values = [values] };

            var request = Service.Spreadsheets.Values.Append(body, spreadsheetId, A1.WholeTab(tabName));
            request.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            request.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;

            var response = await request.ExecuteAsync(cancellationToken);

            // updates.updatedRange looks like 'Tab'!A12:R12 — the row number is what we need back.
            return ParseFirstRowIndex(response.Updates?.UpdatedRange);
        }

        public async Task DeleteRowAsync(string spreadsheetId, string tabName, int rowIndex, CancellationToken cancellationToken = default)
        {
            var tabId = await GetTabIdAsync(spreadsheetId, tabName, cancellationToken);

            var request = new Request
            {
                DeleteDimension = new DeleteDimensionRequest
                {
                    Range = new DimensionRange
                    {
                        SheetId = tabId,
                        Dimension = "ROWS",
                        StartIndex = rowIndex,
                        EndIndex = rowIndex + 1
                    }
                }
            };

            await BatchUpdateAsync(spreadsheetId, [request], cancellationToken);
        }

        public async Task SetCellLinkAsync(string spreadsheetId, string tabName, int rowIndex, int columnIndex, string text, string uri, CancellationToken cancellationToken = default)
        {
            var tabId = await GetTabIdAsync(spreadsheetId, tabName, cancellationToken);

            var request = new Request
            {
                UpdateCells = new UpdateCellsRequest
                {
                    Start = new GridCoordinate
                    {
                        SheetId = tabId,
                        RowIndex = rowIndex,
                        ColumnIndex = columnIndex
                    },
                    Fields = "userEnteredValue,textFormatRuns",
                    Rows =
                    [
                        new RowData
                        {
                            Values =
                            [
                                new CellData
                                {
                                    UserEnteredValue = new ExtendedValue { StringValue = text },
                                    TextFormatRuns =
                                    [
                                        new TextFormatRun
                                        {
                                            StartIndex = 0,
                                            Format = new TextFormat { Link = new Link { Uri = uri } }
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            };

            await BatchUpdateAsync(spreadsheetId, [request], cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ListTabsAsync(string spreadsheetId, CancellationToken cancellationToken = default)
        {
            var spreadsheet = await LoadSpreadsheetAsync(spreadsheetId, cancellationToken);
            return spreadsheet.Sheets.Select(sheet => sheet.Properties.Title).ToList();
        }

        public async Task EnsureTabAsync(string spreadsheetId, string tabName, CancellationToken cancellationToken = default)
        {
            var existing = await ListTabsAsync(spreadsheetId, cancellationToken);
            if (existing.Contains(tabName))
                return;

            var request = new Request
            {
                AddSheet = new AddSheetRequest
                {
                    Properties = new SheetProperties { Title = tabName }
                }
            };

            await BatchUpdateAsync(spreadsheetId, [request], cancellationToken);
            _tabIdCache.Remove(CacheKey(spreadsheetId, tabName));
        }

        public async Task RenameTabAsync(string spreadsheetId, string tabName, string newTabName, CancellationToken cancellationToken = default)
        {
            var tabId = await GetTabIdAsync(spreadsheetId, tabName, cancellationToken);

            var request = new Request
            {
                UpdateSheetProperties = new UpdateSheetPropertiesRequest
                {
                    Properties = new SheetProperties { SheetId = tabId, Title = newTabName },
                    Fields = "title"
                }
            };

            await BatchUpdateAsync(spreadsheetId, [request], cancellationToken);

            // The id outlives the rename; only the name it is cached under changes.
            _tabIdCache.Remove(CacheKey(spreadsheetId, tabName));
            _tabIdCache[CacheKey(spreadsheetId, newTabName)] = tabId;
        }

        public async Task SetTabOrderAsync(string spreadsheetId, IReadOnlyList<string> tabNames, CancellationToken cancellationToken = default)
        {
            var spreadsheet = await LoadSpreadsheetAsync(spreadsheetId, cancellationToken);

            // Sheets reads a requested index against the order *before* that request, so a tab
            // moving right needs index + 1. Placing them front to back never moves one right:
            // after the nth request the first n tabs are already settled, so the tab we move next
            // is at or behind its target and a plain index is correct. Requests in one batch
            // apply in order, which is what makes that hold.
            var requests = new List<Request>();
            var position = 0;

            foreach (var tabName in tabNames)
            {
                var match = spreadsheet.Sheets.FirstOrDefault(sheet => sheet.Properties.Title == tabName);
                if (match is null)
                    continue;

                requests.Add(new Request
                {
                    UpdateSheetProperties = new UpdateSheetPropertiesRequest
                    {
                        Properties = new SheetProperties { SheetId = match.Properties.SheetId, Index = position },
                        Fields = "index"
                    }
                });

                position++;
            }

            if (requests.Count > 0)
                await BatchUpdateAsync(spreadsheetId, requests, cancellationToken);
        }

        public async Task FreezeHeaderAsync(string spreadsheetId, string tabName, CancellationToken cancellationToken = default)
        {
            var tabId = await GetTabIdAsync(spreadsheetId, tabName, cancellationToken);

            var request = new Request
            {
                UpdateSheetProperties = new UpdateSheetPropertiesRequest
                {
                    Properties = new SheetProperties
                    {
                        SheetId = tabId,
                        GridProperties = new GridProperties { FrozenRowCount = 1 }
                    },
                    Fields = "gridProperties.frozenRowCount"
                }
            };

            await BatchUpdateAsync(spreadsheetId, [request], cancellationToken);
        }

        public async Task HideColumnsAsync(string spreadsheetId, string tabName, int startColumnIndex, int endColumnIndex, CancellationToken cancellationToken = default)
        {
            var tabId = await GetTabIdAsync(spreadsheetId, tabName, cancellationToken);

            var request = new Request
            {
                UpdateDimensionProperties = new UpdateDimensionPropertiesRequest
                {
                    Range = new DimensionRange
                    {
                        SheetId = tabId,
                        Dimension = "COLUMNS",
                        StartIndex = startColumnIndex,
                        EndIndex = endColumnIndex
                    },
                    Properties = new DimensionProperties { HiddenByUser = true },
                    Fields = "hiddenByUser"
                }
            };

            await BatchUpdateAsync(spreadsheetId, [request], cancellationToken);
        }

        public async Task SetDateTimeFormatAsync(string spreadsheetId, string tabName, IReadOnlyList<int> columnIndexes, int startRowIndex, int? endRowIndex, string pattern, CancellationToken cancellationToken = default)
        {
            if (columnIndexes.Count == 0)
                return;

            var tabId = await GetTabIdAsync(spreadsheetId, tabName, cancellationToken);
            var requests = new List<Request>();

            foreach (var columnIndex in columnIndexes)
            {
                requests.Add(new Request
                {
                    RepeatCell = new RepeatCellRequest
                    {
                        Range = new GridRange
                        {
                            SheetId = tabId,
                            StartColumnIndex = columnIndex,
                            EndColumnIndex = columnIndex + 1,
                            StartRowIndex = startRowIndex,

                            // Left unset for an unbounded range, which is how a column-wide format
                            // reaches rows that do not exist yet.
                            EndRowIndex = endRowIndex
                        },
                        Cell = new CellData
                        {
                            UserEnteredFormat = new CellFormat
                            {
                                NumberFormat = new NumberFormat { Type = "DATE_TIME", Pattern = pattern }
                            }
                        },
                        Fields = "userEnteredFormat.numberFormat"
                    }
                });
            }

            await BatchUpdateAsync(spreadsheetId, requests, cancellationToken);
        }

        public async Task<string?> GetSpreadsheetTimeZoneAsync(string spreadsheetId, CancellationToken cancellationToken = default)
        {
            var request = Service.Spreadsheets.Get(spreadsheetId);
            request.Fields = "properties.timeZone";

            var spreadsheet = await request.ExecuteAsync(cancellationToken);
            return spreadsheet.Properties?.TimeZone;
        }

        public async Task SetSpreadsheetTimeZoneAsync(string spreadsheetId, string timeZoneId, CancellationToken cancellationToken = default)
        {
            var request = new Request
            {
                UpdateSpreadsheetProperties = new UpdateSpreadsheetPropertiesRequest
                {
                    Properties = new SpreadsheetProperties { TimeZone = timeZoneId },
                    Fields = "timeZone"
                }
            };

            await BatchUpdateAsync(spreadsheetId, [request], cancellationToken);
        }

        async Task BatchUpdateAsync(string spreadsheetId, IList<Request> requests, CancellationToken cancellationToken)
        {
            var body = new BatchUpdateSpreadsheetRequest { Requests = requests };
            await Service.Spreadsheets.BatchUpdate(body, spreadsheetId).ExecuteAsync(cancellationToken);
        }

        async Task<Spreadsheet> LoadSpreadsheetAsync(string spreadsheetId, CancellationToken cancellationToken)
        {
            var request = Service.Spreadsheets.Get(spreadsheetId);
            request.Fields = "sheets.properties(sheetId,title)";
            return await request.ExecuteAsync(cancellationToken);
        }

        async Task<int> GetTabIdAsync(string spreadsheetId, string tabName, CancellationToken cancellationToken)
        {
            var key = CacheKey(spreadsheetId, tabName);
            if (_tabIdCache.TryGetValue(key, out var cached))
                return cached;

            var spreadsheet = await LoadSpreadsheetAsync(spreadsheetId, cancellationToken);
            var match = spreadsheet.Sheets.FirstOrDefault(sheet => sheet.Properties.Title == tabName)
                ?? throw new InvalidOperationException($"Spreadsheet '{spreadsheetId}' has no tab named '{tabName}'.");

            var tabId = match.Properties.SheetId ?? 0;
            _tabIdCache[key] = tabId;
            return tabId;
        }

        static string CacheKey(string spreadsheetId, string tabName) => $"{spreadsheetId} {tabName}";

        internal static int ParseFirstRowIndex(string? updatedRange)
        {
            if (string.IsNullOrEmpty(updatedRange))
                throw new InvalidOperationException("Sheets append did not report an updated range.");

            var bang = updatedRange.LastIndexOf('!');
            var cells = bang >= 0 ? updatedRange[(bang + 1)..] : updatedRange;

            var colon = cells.IndexOf(':');
            var first = colon >= 0 ? cells[..colon] : cells;

            var digits = new string(first.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var oneBased))
                throw new InvalidOperationException($"Could not parse a row number out of updated range '{updatedRange}'.");

            return oneBased - 1;
        }
    }
}

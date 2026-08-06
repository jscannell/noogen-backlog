using System.Globalization;
using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog
{
    /// <summary>
    /// Reads and writes lifecycle tabs. The only place that knows about A1 notation, cell
    /// coercion, and which columns the Sheet owns.
    /// </summary>
    public class SheetIndex
    {
        readonly ISheetsGateway _sheets;
        readonly string _spreadsheetId;

        public SheetIndex(ISheetsGateway sheets, string spreadsheetId)
        {
            _sheets = sheets;
            _spreadsheetId = spreadsheetId;
        }

        public async Task<SheetTable> LoadAsync(BacklogPhase phase, CancellationToken cancellationToken = default)
        {
            var values = await _sheets.GetValuesAsync(_spreadsheetId, A1.WholeTab(phase.TabName()), cancellationToken);
            return new SheetTable(phase, values);
        }

        public async Task<IReadOnlyList<SheetTable>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            var tables = new List<SheetTable>();
            foreach (var phase in BacklogPhaseExtensions.All)
                tables.Add(await LoadAsync(phase, cancellationToken));

            return tables;
        }

        // --- reading ---

        public static Ticket ToTicket(SheetTable table, int dataRowIndex)
        {
            var ticket = new Ticket
            {
                Id = table.Value(dataRowIndex, SheetSchema.Id) ?? string.Empty,
                Title = table.Value(dataRowIndex, SheetSchema.Title) ?? string.Empty,
                Area = table.Value(dataRowIndex, SheetSchema.Area) ?? string.Empty,
                Owner = table.Value(dataRowIndex, SheetSchema.Owner),
                Phase = table.Phase,
                DocId = table.Value(dataRowIndex, SheetSchema.DocId),
                DocUrl = table.Value(dataRowIndex, SheetSchema.DocUrl)
            };

            var type = table.Value(dataRowIndex, SheetSchema.Type);
            ticket.Type = type is null ? TicketType.Feature : Vocabulary.Parse<TicketType>(type, SheetSchema.Type);

            ticket.Score = new WsjfScore
            {
                BusinessValue = ReadInt(table, dataRowIndex, SheetSchema.Bv),
                TimeCriticality = ReadInt(table, dataRowIndex, SheetSchema.Tc),
                RiskReductionOpportunityEnablement = ReadInt(table, dataRowIndex, SheetSchema.Rroe),
                JobSize = ReadInt(table, dataRowIndex, SheetSchema.Size)
            };

            ticket.Rank = ReadInt(table, dataRowIndex, SheetSchema.Rank);
            ticket.State = Vocabulary.ParseOptional<WorkState>(table.Value(dataRowIndex, SheetSchema.State), SheetSchema.State);
            ticket.BlockedReason = table.Value(dataRowIndex, SheetSchema.BlockedReason);
            ticket.BlockedAt = Iso.ParseOptional(table.Value(dataRowIndex, SheetSchema.BlockedAt), SheetSchema.BlockedAt);
            ticket.StartedAt = Iso.ParseOptional(table.Value(dataRowIndex, SheetSchema.StartedAt), SheetSchema.StartedAt);
            ticket.Outcome = Vocabulary.ParseOptional<Outcome>(table.Value(dataRowIndex, SheetSchema.Outcome), SheetSchema.Outcome);
            ticket.ArchivedAt = Iso.ParseOptional(table.Value(dataRowIndex, SheetSchema.ArchivedAt), SheetSchema.ArchivedAt);
            ticket.LeadDays = ReadDouble(table, dataRowIndex, SheetSchema.LeadDays);
            ticket.CycleDays = ReadDouble(table, dataRowIndex, SheetSchema.CycleDays);

            var created = table.Value(dataRowIndex, SheetSchema.Created);
            ticket.Created = created is null ? default : Iso.Parse(created, SheetSchema.Created);

            var updated = table.Value(dataRowIndex, SheetSchema.Updated);
            ticket.Updated = updated is null ? ticket.Created : Iso.Parse(updated, SheetSchema.Updated);

            return ticket;
        }

        public static IReadOnlyList<Ticket> ToTickets(SheetTable table)
        {
            var tickets = new List<Ticket>();
            for (var i = 0; i < table.Rows.Count; i++)
            {
                if (table.Value(i, SheetSchema.Id) is null)
                    continue;

                tickets.Add(ToTicket(table, i));
            }

            return tickets;
        }

        // --- writing ---

        public async Task<int> AppendAsync(SheetTable table, Ticket ticket, CancellationToken cancellationToken = default)
        {
            // Append lands after the last populated row, which is where the formulas must point.
            var projectedDataRowIndex = table.Rows.Count;
            var values = BuildRow(table, ticket, projectedDataRowIndex);

            var sheetRowIndex = await _sheets.AppendRowAsync(_spreadsheetId, table.Phase.TabName(), values, cancellationToken);
            var actualDataRowIndex = sheetRowIndex - 1;

            // Sheets may have appended somewhere other than we projected (a stray populated row
            // below the data). Rewrite the row so any row-relative formulas match reality.
            if (actualDataRowIndex != projectedDataRowIndex && table.Phase.UsesLiveFormulas())
                await WriteRowAsync(table, ticket, actualDataRowIndex, cancellationToken);

            await SetTitleLinkAsync(table, ticket, actualDataRowIndex, cancellationToken);
            return actualDataRowIndex;
        }

        public async Task WriteRowAsync(SheetTable table, Ticket ticket, int dataRowIndex, CancellationToken cancellationToken = default)
        {
            var values = BuildRow(table, ticket, dataRowIndex);
            var range = A1.Row(table.Phase.TabName(), SheetTable.SheetRowIndex(dataRowIndex), table.Headers.Count);

            await _sheets.UpdateValuesAsync(_spreadsheetId, range, [values], cancellationToken);
            await SetTitleLinkAsync(table, ticket, dataRowIndex, cancellationToken);
        }

        public Task DeleteRowAsync(SheetTable table, int dataRowIndex, CancellationToken cancellationToken = default) =>
            _sheets.DeleteRowAsync(_spreadsheetId, table.Phase.TabName(), SheetTable.SheetRowIndex(dataRowIndex), cancellationToken);

        async Task SetTitleLinkAsync(SheetTable table, Ticket ticket, int dataRowIndex, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ticket.DocUrl) || !table.Has(SheetSchema.Title))
                return;

            await _sheets.SetCellLinkAsync(
                _spreadsheetId,
                table.Phase.TabName(),
                SheetTable.SheetRowIndex(dataRowIndex),
                table.IndexOf(SheetSchema.Title),
                ticket.Title,
                ticket.DocUrl,
                cancellationToken);
        }

        /// <summary>
        /// Builds the row in the tab's own header order. Formula columns get formulas on the
        /// Backlog tab and frozen values everywhere else — the store never computes a value into
        /// a cell the Sheet owns.
        /// </summary>
        internal static IList<object> BuildRow(SheetTable table, Ticket ticket, int dataRowIndex)
        {
            var rowNumber = SheetTable.SheetRowNumber(dataRowIndex);
            var live = table.Phase.UsesLiveFormulas();
            var values = new List<object>();

            foreach (var header in table.Headers)
                values.Add(BuildCell(table, ticket, header, rowNumber, live));

            return values;
        }

        static object BuildCell(SheetTable table, Ticket ticket, string header, int rowNumber, bool live)
        {
            switch (header)
            {
                case SheetSchema.Id:
                    return ticket.Id;
                case SheetSchema.Title:
                    return EscapeUserText(ticket.Title);
                case SheetSchema.Type:
                    return Vocabulary.ToWire(ticket.Type);
                case SheetSchema.Area:
                    return EscapeUserText(ticket.Area);
                case SheetSchema.Owner:
                    return EscapeUserText(ticket.Owner);
                case SheetSchema.Bv:
                    return Number(ticket.Score.BusinessValue);
                case SheetSchema.Tc:
                    return Number(ticket.Score.TimeCriticality);
                case SheetSchema.Rroe:
                    return Number(ticket.Score.RiskReductionOpportunityEnablement);
                case SheetSchema.Size:
                    return Number(ticket.Score.JobSize);
                case SheetSchema.Cod:
                    return live ? CodFormula(table, rowNumber) : Number(ticket.Score.CostOfDelay);
                case SheetSchema.Wsjf:
                    return live ? WsjfFormula(table, rowNumber) : Number(ticket.Score.Value);
                case SheetSchema.Rank:
                    return live ? RankFormula(table, rowNumber) : string.Empty;
                case SheetSchema.State:
                    return ticket.State.HasValue ? Vocabulary.ToWire(ticket.State.Value) : string.Empty;
                case SheetSchema.BlockedReason:
                    return EscapeUserText(ticket.BlockedReason);
                case SheetSchema.BlockedAt:
                    return Iso.ToText(ticket.BlockedAt) ?? string.Empty;
                case SheetSchema.StartedAt:
                    return Iso.ToText(ticket.StartedAt) ?? string.Empty;
                case SheetSchema.Outcome:
                    return ticket.Outcome.HasValue ? Vocabulary.ToWire(ticket.Outcome.Value) : string.Empty;
                case SheetSchema.ArchivedAt:
                    return Iso.ToText(ticket.ArchivedAt) ?? string.Empty;
                case SheetSchema.LeadDays:
                    return Number(ticket.LeadDays);
                case SheetSchema.CycleDays:
                    return Number(ticket.CycleDays);
                case SheetSchema.Created:
                    return Iso.ToText(ticket.Created);
                case SheetSchema.Updated:
                    return Iso.ToText(ticket.Updated);
                case SheetSchema.DocId:
                    return ticket.DocId ?? string.Empty;
                case SheetSchema.DocUrl:
                    return ticket.DocUrl ?? string.Empty;
                default:
                    // A column a human added. Leave it alone rather than blanking it.
                    return string.Empty;
            }
        }

        internal static string CodFormula(SheetTable table, int rowNumber)
        {
            var bv = table.ColumnLetter(SheetSchema.Bv);
            var tc = table.ColumnLetter(SheetSchema.Tc);
            var rroe = table.ColumnLetter(SheetSchema.Rroe);

            return $"=IF(COUNT({bv}{rowNumber},{tc}{rowNumber},{rroe}{rowNumber})<3,\"\",{bv}{rowNumber}+{tc}{rowNumber}+{rroe}{rowNumber})";
        }

        internal static string WsjfFormula(SheetTable table, int rowNumber)
        {
            var cod = table.ColumnLetter(SheetSchema.Cod);
            var size = table.ColumnLetter(SheetSchema.Size);

            return $"=IF(OR({cod}{rowNumber}=\"\",{size}{rowNumber}=\"\",{size}{rowNumber}=0),\"\",ROUND({cod}{rowNumber}/{size}{rowNumber},2))";
        }

        internal static string RankFormula(SheetTable table, int rowNumber)
        {
            var wsjf = table.ColumnLetter(SheetSchema.Wsjf);

            // Every row on this tab is a ranking candidate by construction — the tab is the state —
            // so a plain RANK suffices. Unscored rows show blank rather than a bogus position.
            return $"=IF({wsjf}{rowNumber}=\"\",\"\",RANK({wsjf}{rowNumber},${wsjf}$2:${wsjf},0))";
        }

        /// <summary>
        /// Neutralises formula injection from user-supplied text. A leading apostrophe tells
        /// Sheets "this is literal" and is not stored as part of the value.
        /// </summary>
        internal static string EscapeUserText(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var first = text[0];
            return first is '=' or '+' or '@' or '-' ? "'" + text : text;
        }

        static object Number(int? value) => value.HasValue ? value.Value : string.Empty;

        static object Number(double? value) =>
            value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

        static int? ReadInt(SheetTable table, int dataRowIndex, string column)
        {
            var text = table.Value(dataRowIndex, column);
            if (text is null)
                return null;

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        static double? ReadDouble(SheetTable table, int dataRowIndex, string column)
        {
            var text = table.Value(dataRowIndex, column);
            if (text is null)
                return null;

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
    }
}

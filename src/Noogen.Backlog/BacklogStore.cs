using System.Text;
using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog
{
    public class BacklogStore : IBacklogStore
    {
        readonly ISheetsGateway _sheets;
        readonly IDriveGateway _drive;
        readonly SheetIndex _index;
        readonly TicketMover _mover;
        readonly string _spreadsheetId;
        readonly TimeProvider _clock;

        BacklogSettings? _settings;

        public BacklogStore(ISheetsGateway sheets, IDriveGateway drive, string spreadsheetId, TimeProvider? clock = null)
        {
            _sheets = sheets;
            _drive = drive;
            _spreadsheetId = spreadsheetId;
            _clock = clock ?? TimeProvider.System;
            _index = new SheetIndex(sheets, spreadsheetId);
            _mover = new TicketMover(_index);
        }

        DateTimeOffset Now => _clock.GetUtcNow();

        /// <summary>
        /// Also hands the configured timezone to the index. Datetime cells are wall-clock serials
        /// interpreted against it, so nothing may read or write a timestamp before this has run —
        /// which is why every entry point below starts here.
        /// </summary>
        public async Task<BacklogSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            _settings ??= await BacklogSettings.LoadAsync(_sheets, _spreadsheetId, cancellationToken);
            _index.Zone = _settings.Zone;

            return _settings;
        }

        // --- queries ---

        public async Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, CancellationToken cancellationToken = default)
        {
            await GetSettingsAsync(cancellationToken);
            var table = await _index.LoadAsync(BacklogPhase.Backlog, cancellationToken);

            var tickets = _index.ToTickets(table)
                .Where(filter.Matches)
                .OrderBy(ticket => ticket.Score.Value.HasValue ? 0 : 1)   // unscored sorts last
                .ThenByDescending(ticket => ticket.Score.Value ?? 0)
                .ThenBy(ticket => ticket.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return filter.Top.HasValue ? tickets.Take(filter.Top.Value).ToList() : tickets;
        }

        public async Task<IReadOnlyList<Ticket>> WipAsync(TicketFilter filter, CancellationToken cancellationToken = default)
        {
            await GetSettingsAsync(cancellationToken);
            var table = await _index.LoadAsync(BacklogPhase.InProgress, cancellationToken);

            var tickets = _index.ToTickets(table)
                .Where(filter.Matches)
                .OrderBy(ticket => ticket.StartedAt ?? DateTimeOffset.MaxValue)   // oldest first: aging surfaces
                .ToList();

            return filter.Top.HasValue ? tickets.Take(filter.Top.Value).ToList() : tickets;
        }

        public async Task<FlowMetrics> FlowAsync(DateTimeOffset? since, CancellationToken cancellationToken = default)
        {
            await GetSettingsAsync(cancellationToken);
            var table = await _index.LoadAsync(BacklogPhase.Archive, cancellationToken);
            return FlowMetrics.From(_index.ToTickets(table), since);
        }

        public async Task<Ticket?> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            var location = await FindAsync(id, cancellationToken);
            return location?.Ticket;
        }

        public async Task<string> GetBodyAsync(string id, CancellationToken cancellationToken = default)
        {
            var location = await RequireAsync(id, cancellationToken);
            if (string.IsNullOrEmpty(location.Ticket.DocId))
                return string.Empty;

            var raw = await _drive.ReadDocAsync(location.Ticket.DocId, cancellationToken);
            return TicketDocument.Parse(raw).Body;
        }

        // --- mutations ---

        public async Task<Ticket> CreateAsync(NewTicket request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.Title))
                throw new ArgumentException("A ticket needs a title.", nameof(request));

            request.Score.Validate();

            var settings = await GetSettingsAsync(cancellationToken);
            var tables = await _index.LoadAllAsync(cancellationToken);
            var now = Now;

            var ticket = new Ticket
            {
                Id = settings.FormatId(NextIdNumber(tables)),
                Title = request.Title.Trim(),
                Type = request.Type,
                Area = request.Area?.Trim() ?? string.Empty,
                Owner = string.IsNullOrWhiteSpace(request.Owner) ? null : request.Owner.Trim(),
                Score = request.Score.Clone(),
                Phase = BacklogPhase.Backlog,
                Created = now,
                Updated = now
            };

            var body = TicketDocument.BuildInitialBody(ticket, request.Description, settings.Zone);

            // No extension: a Google Doc has no file type in its name, and this one carries the
            // name into the Docs tab a person reads it in.
            var fileName = $"{ticket.Id}-{Slug(ticket.Title)}";

            ticket.DocId = await _drive.CreateDocAsync(
                settings.TicketsFolderId,
                fileName,
                TicketDocument.Serialize(ticket, body),
                cancellationToken);

            ticket.DocUrl = await _drive.GetWebViewLinkAsync(ticket.DocId, cancellationToken);

            var backlog = tables.First(table => table.Phase == BacklogPhase.Backlog);
            await _index.AppendAsync(backlog, ticket, cancellationToken);

            return ticket;
        }

        public async Task<Ticket> UpdateAsync(string id, TicketEdit edit, CancellationToken cancellationToken = default)
        {
            if (edit.Description is not null && string.IsNullOrWhiteSpace(edit.Description))
                throw new ArgumentException("A description needs text. To empty one, edit the document itself.", nameof(edit));

            var location = await RequireAsync(id, cancellationToken);
            var ticket = location.Ticket;

            // A row with no document is damage doctor reports, and rewriting a section of nothing
            // would be the silent no-op an edit must never be.
            if (edit.Description is not null && string.IsNullOrEmpty(ticket.DocId))
            {
                throw new InvalidOperationException(
                    $"'{ticket.Id}' has no document in Drive, so there is no description to edit. Run 'backlog doctor'.");
            }

            if (!string.IsNullOrWhiteSpace(edit.Title))
                ticket.Title = edit.Title.Trim();

            if (edit.Area is not null)
                ticket.Area = edit.Area.Trim();

            if (edit.Owner is not null)
                ticket.Owner = string.IsNullOrWhiteSpace(edit.Owner) ? null : edit.Owner.Trim();

            if (edit.Type.HasValue)
                ticket.Type = edit.Type.Value;

            return await SaveInPlaceAsync(location, null, edit.Description?.Trim(), cancellationToken);
        }

        public async Task<Ticket> ScoreAsync(string id, WsjfScore score, CancellationToken cancellationToken = default)
        {
            var location = await RequireAsync(id, cancellationToken);
            var ticket = location.Ticket;

            if (ticket.Phase != BacklogPhase.Backlog)
            {
                throw new BacklogTransitionException(
                    $"'{id}' is on the {ticket.Phase.TabName()} tab, so it is no longer subject to WSJF. " +
                    "WSJF sequences what to start next, and this item has already started — its scores are frozen as a historical record.");
            }

            score.Validate();

            if (score.BusinessValue.HasValue)
                ticket.Score.BusinessValue = score.BusinessValue;
            if (score.TimeCriticality.HasValue)
                ticket.Score.TimeCriticality = score.TimeCriticality;
            if (score.RiskReductionOpportunityEnablement.HasValue)
                ticket.Score.RiskReductionOpportunityEnablement = score.RiskReductionOpportunityEnablement;
            if (score.JobSize.HasValue)
                ticket.Score.JobSize = score.JobSize;

            return await SaveInPlaceAsync(location, null, null, cancellationToken);
        }

        public async Task<Ticket> AppendNoteAsync(string id, string note, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(note))
                throw new ArgumentException("A note needs text.", nameof(note));

            var location = await RequireAsync(id, cancellationToken);
            return await SaveInPlaceAsync(location, note.Trim(), null, cancellationToken);
        }

        public async Task<Ticket> StartAsync(string id, string? owner, bool force, CancellationToken cancellationToken = default)
        {
            var location = await RequireAsync(id, cancellationToken);
            var ticket = location.Ticket;

            if (ticket.Phase != BacklogPhase.Backlog)
                throw new BacklogTransitionException($"'{id}' is already on the {ticket.Phase.TabName()} tab.");

            var settings = await GetSettingsAsync(cancellationToken);
            var inFlight = await WipAsync(new TicketFilter(), cancellationToken);

            if (!force && inFlight.Count >= settings.WipLimit)
            {
                var names = string.Join(", ", inFlight.Select(item => $"{item.Id} ({item.Owner ?? "unowned"})"));
                throw new WipLimitExceededException(
                    $"Starting '{id}' would put {inFlight.Count + 1} items in flight against a WIP limit of {settings.WipLimit}. " +
                    $"Already in progress: {names}. Finish something first, or pass --force to override.",
                    settings.WipLimit,
                    inFlight);
            }

            var now = Now;
            ticket.StartedAt = now;
            ticket.Updated = now;
            ticket.State = WorkState.InProgress;

            if (!string.IsNullOrWhiteSpace(owner))
                ticket.Owner = owner.Trim();

            var note = force && inFlight.Count >= settings.WipLimit
                ? $"started (WIP limit of {settings.WipLimit} overridden with --force)"
                : "started";

            await UpdateDocumentAsync(ticket, note, null, cancellationToken);
            await _mover.MoveAsync(ticket, BacklogPhase.InProgress, cancellationToken);

            return ticket;
        }

        public async Task<Ticket> SetStateAsync(string id, WorkState state, string? blockedReason, CancellationToken cancellationToken = default)
        {
            var location = await RequireAsync(id, cancellationToken);
            var ticket = location.Ticket;

            if (ticket.Phase != BacklogPhase.InProgress)
            {
                throw new BacklogTransitionException(
                    $"'{id}' is on the {ticket.Phase.TabName()} tab. Work state applies only to items in progress — " +
                    (ticket.Phase == BacklogPhase.Backlog ? "run 'backlog start' first." : "restore it first."));
            }

            if (state == WorkState.Blocked && string.IsNullOrWhiteSpace(blockedReason))
                throw new ArgumentException("Blocking an item needs a reason.", nameof(blockedReason));

            var now = Now;
            ticket.State = state;
            ticket.Updated = now;

            if (state == WorkState.Blocked)
            {
                ticket.BlockedReason = blockedReason!.Trim();
                ticket.BlockedAt = now;
            }
            else
            {
                ticket.BlockedReason = null;
                ticket.BlockedAt = null;
            }

            var note = state == WorkState.Blocked
                ? $"blocked — {ticket.BlockedReason}"
                : Vocabulary.ToWire(state);

            return await SaveInPlaceAsync(location, note, null, cancellationToken);
        }

        public async Task<Ticket> ArchiveAsync(string id, Outcome outcome, string? note, CancellationToken cancellationToken = default)
        {
            var location = await RequireAsync(id, cancellationToken);
            var ticket = location.Ticket;

            if (ticket.Phase == BacklogPhase.Archive)
                throw new BacklogTransitionException($"'{id}' is already archived.");

            var settings = await GetSettingsAsync(cancellationToken);
            var now = Now;

            ticket.Outcome = outcome;
            ticket.ArchivedAt = now;
            ticket.Updated = now;
            ticket.State = null;
            ticket.BlockedReason = null;
            ticket.BlockedAt = null;

            ticket.LeadDays = ticket.Created == default ? null : FlowMetrics.Days(ticket.Created, now);
            ticket.CycleDays = ticket.StartedAt.HasValue ? FlowMetrics.Days(ticket.StartedAt.Value, now) : null;

            var activity = string.IsNullOrWhiteSpace(note)
                ? $"archived — {Vocabulary.ToWire(outcome)}"
                : $"archived — {Vocabulary.ToWire(outcome)}: {note.Trim()}";

            await UpdateDocumentAsync(ticket, activity, null, cancellationToken);
            await _mover.MoveAsync(ticket, BacklogPhase.Archive, cancellationToken);

            // Archive is the only transition that also moves the document. Items in progress keep
            // their doc in tickets/ because it is being actively edited.
            if (!string.IsNullOrEmpty(ticket.DocId))
            {
                var quarterFolderId = await EnsureQuarterFolderAsync(settings, now, cancellationToken);
                await _drive.MoveAsync(ticket.DocId, quarterFolderId, settings.TicketsFolderId, cancellationToken);
            }

            return ticket;
        }

        public async Task<Ticket> RestoreAsync(string id, CancellationToken cancellationToken = default)
        {
            var location = await RequireAsync(id, cancellationToken);
            var ticket = location.Ticket;

            if (ticket.Phase != BacklogPhase.Archive)
                throw new BacklogTransitionException($"'{id}' is not archived — it is on the {ticket.Phase.TabName()} tab.");

            var settings = await GetSettingsAsync(cancellationToken);
            var archivedAt = ticket.ArchivedAt;
            var now = Now;

            ticket.Outcome = null;
            ticket.ArchivedAt = null;
            ticket.LeadDays = null;
            ticket.CycleDays = null;
            ticket.StartedAt = null;
            ticket.State = null;
            ticket.Updated = now;

            await UpdateDocumentAsync(ticket, "restored to the backlog", null, cancellationToken);
            await _mover.MoveAsync(ticket, BacklogPhase.Backlog, cancellationToken);

            if (!string.IsNullOrEmpty(ticket.DocId) && archivedAt.HasValue)
            {
                var quarterFolderId = await EnsureQuarterFolderAsync(settings, archivedAt.Value, cancellationToken);
                await _drive.MoveAsync(ticket.DocId, settings.TicketsFolderId, quarterFolderId, cancellationToken);
            }

            return ticket;
        }

        // --- maintenance ---

        public async Task<DoctorReport> DoctorAsync(CancellationToken cancellationToken = default)
        {
            var settings = await GetSettingsAsync(cancellationToken);
            var tables = await _index.LoadAllAsync(cancellationToken);
            var report = new DoctorReport();

            var seen = new Dictionary<string, BacklogPhase>(StringComparer.OrdinalIgnoreCase);
            var referencedDocIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Datetime cells are wall-clock values read against the spreadsheet's own timezone. If
            // that disagrees with the configured one, every stored instant silently shifts.
            var spreadsheetZone = await _sheets.GetSpreadsheetTimeZoneAsync(_spreadsheetId, cancellationToken);
            if (!string.IsNullOrEmpty(spreadsheetZone) && !string.Equals(spreadsheetZone, settings.TimeZoneId, StringComparison.OrdinalIgnoreCase))
            {
                report.Add(
                    SheetSchema.ConfigTabName,
                    "timezone-mismatch",
                    $"The spreadsheet's timezone is '{spreadsheetZone}' but the config says '{settings.TimeZoneId}'. Every timestamp reads shifted. Run 'backlog init' to reconcile.");
            }

            foreach (var table in tables)
            {
                foreach (var expected in SheetSchema.Columns(table.Phase))
                {
                    if (!table.Has(expected))
                        report.Add(table.Phase.TabName(), "missing-column", $"Tab is missing the '{expected}' column.");
                }

                foreach (var ticket in _index.ToTickets(table))
                {
                    report.TicketCount++;

                    if (seen.TryGetValue(ticket.Id, out var otherPhase))
                    {
                        // The signature of an interrupted move: append succeeded, delete did not.
                        report.Add(ticket.Id, "duplicate", $"Appears on both '{otherPhase.TabName()}' and '{table.Phase.TabName()}'. An interrupted move — delete the stale row.");
                    }
                    else
                    {
                        seen[ticket.Id] = table.Phase;
                    }

                    if (string.IsNullOrEmpty(ticket.DocId))
                    {
                        report.Add(ticket.Id, "no-document", $"Row has no {SheetSchema.DriveFileId}, so the ticket has no document.");
                        continue;
                    }

                    referencedDocIds.Add(ticket.DocId);

                    string raw;
                    try
                    {
                        raw = await _drive.ReadDocAsync(ticket.DocId, cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        report.Add(ticket.Id, "unreadable-document", $"Could not read the document: {exception.Message}");
                        continue;
                    }

                    TicketDocument document;
                    try
                    {
                        document = TicketDocument.Parse(raw);
                    }
                    catch (FormatException exception)
                    {
                        report.Add(ticket.Id, "malformed-document", exception.Message);
                        continue;
                    }

                    foreach (var drift in DescribeDrift(ticket, document.Ticket))
                        report.Add(ticket.Id, "drift", drift);
                }
            }

            if (!string.IsNullOrEmpty(settings.TicketsFolderId))
            {
                var documents = await _drive.ListChildrenAsync(settings.TicketsFolderId, null, cancellationToken);
                foreach (var entry in documents)
                {
                    if (!referencedDocIds.Contains(entry.Id))
                        report.Add(entry.Name, "orphan-document", "Document is in tickets/ but no index row references it. Consider 'backlog new' or deleting it.");
                }
            }

            return report;
        }

        /// <summary>
        /// Repairs each row, merging three sources by who actually owns each field:
        ///
        /// - <b>Content</b> (title, type, area, owner, scores) comes from the document, since that
        ///   is what a human edits.
        /// - <b>Lifecycle</b> (phase, work state, started/blocked/archived, lead and cycle days)
        ///   stays as the Sheet has it. The document deliberately no longer carries these, so
        ///   taking them from there would blank them.
        /// - <b>created / updated</b> come from Drive's own file metadata, which is authoritative
        ///   and, for modifiedTime, better than anything we could maintain — it also catches a
        ///   person editing the document directly.
        ///
        /// Bounded on purpose: it repairs rows that exist and does not invent rows for orphaned
        /// documents, which <c>doctor</c> reports for a human to judge.
        /// </summary>
        public async Task<int> ReindexAsync(CancellationToken cancellationToken = default)
        {
            await GetSettingsAsync(cancellationToken);
            var repaired = 0;

            foreach (var phase in BacklogPhaseExtensions.All)
            {
                var table = await _index.LoadAsync(phase, cancellationToken);

                for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                {
                    var row = _index.ToTicket(table, rowIndex);
                    if (string.IsNullOrEmpty(row.Id) || string.IsNullOrEmpty(row.DocId))
                        continue;

                    var raw = await _drive.ReadDocAsync(row.DocId, cancellationToken);
                    var document = TicketDocument.Parse(raw).Ticket;
                    var times = await _drive.GetTimestampsAsync(row.DocId, cancellationToken);

                    row.Title = document.Title;
                    row.Type = document.Type;
                    row.Area = document.Area;
                    row.Owner = document.Owner;
                    row.Score = document.Score;

                    row.Created = times.CreatedTime ?? row.Created;
                    row.Updated = times.ModifiedTime ?? row.Updated;

                    row.DocUrl ??= await _drive.GetWebViewLinkAsync(row.DocId, cancellationToken);

                    await _index.WriteRowAsync(table, row, rowIndex, cancellationToken);
                    repaired++;
                }
            }

            return repaired;
        }

        // --- internals ---

        internal class TicketLocation
        {
            public SheetTable Table { get; set; } = null!;

            public int DataRowIndex { get; set; }

            public Ticket Ticket { get; set; } = null!;
        }

        async Task<TicketLocation?> FindAsync(string id, CancellationToken cancellationToken)
        {
            await GetSettingsAsync(cancellationToken);

            foreach (var phase in BacklogPhaseExtensions.All)
            {
                var table = await _index.LoadAsync(phase, cancellationToken);
                var rowIndex = table.FindByIdOrDefault(id);

                if (rowIndex.HasValue)
                {
                    return new TicketLocation
                    {
                        Table = table,
                        DataRowIndex = rowIndex.Value,
                        Ticket = _index.ToTicket(table, rowIndex.Value)
                    };
                }
            }

            return null;
        }

        async Task<TicketLocation> RequireAsync(string id, CancellationToken cancellationToken) =>
            await FindAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"No ticket '{id}' on any tab. Run 'backlog list' to see the queue.");

        async Task<Ticket> SaveInPlaceAsync(TicketLocation location, string? note, string? description, CancellationToken cancellationToken)
        {
            location.Ticket.Updated = Now;

            await UpdateDocumentAsync(location.Ticket, note, description, cancellationToken);
            await _index.WriteRowAsync(location.Table, location.Ticket, location.DataRowIndex, cancellationToken);

            return location.Ticket;
        }

        /// <summary>
        /// Rewrites the heading and the bullets from the ticket, and passes the body through. The
        /// two exceptions are both bounded by a heading a person can see: a note appended to the
        /// Activity Log, and the Description section replaced outright. Everything else below the
        /// bullets is the author's (invariant 9).
        /// </summary>
        async Task UpdateDocumentAsync(Ticket ticket, string? note, string? description, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(ticket.DocId))
                return;

            var settings = await GetSettingsAsync(cancellationToken);
            var raw = await _drive.ReadDocAsync(ticket.DocId, cancellationToken);
            var body = TicketDocument.Parse(raw).Body;

            if (description is not null)
                body = TicketDocument.ReplaceSection(body, TicketDocument.DescriptionHeading, description);

            if (!string.IsNullOrWhiteSpace(note))
                body = TicketDocument.AppendActivity(body, ticket.Updated, note, settings.Zone);

            await _drive.UpdateDocAsync(
                ticket.DocId,
                TicketDocument.Serialize(ticket, body),
                cancellationToken);
        }

        async Task<string> EnsureQuarterFolderAsync(BacklogSettings settings, DateTimeOffset when, CancellationToken cancellationToken)
        {
            var year = when.ToUniversalTime().Year.ToString();
            var quarter = $"Q{((when.ToUniversalTime().Month - 1) / 3) + 1}";

            var yearFolderId = await _drive.FindChildAsync(settings.ArchiveFolderId, year, DriveGateway.FolderMimeType, cancellationToken)
                ?? await _drive.CreateFolderAsync(settings.ArchiveFolderId, year, cancellationToken);

            return await _drive.FindChildAsync(yearFolderId, quarter, DriveGateway.FolderMimeType, cancellationToken)
                ?? await _drive.CreateFolderAsync(yearFolderId, quarter, cancellationToken);
        }

        /// <summary>
        /// MAX + 1 across every lifecycle tab rather than a stored counter, so the sequence is
        /// self-healing. Concurrent creates can collide; doctor reports the duplicate.
        /// </summary>
        internal static int NextIdNumber(IEnumerable<SheetTable> tables)
        {
            var highest = 0;

            foreach (var table in tables)
            {
                foreach (var id in table.Ids())
                {
                    var number = BacklogSettings.ParseIdNumber(id);
                    if (number.HasValue && number.Value > highest)
                        highest = number.Value;
                }
            }

            return highest + 1;
        }

        internal static IEnumerable<string> DescribeDrift(Ticket row, Ticket document)
        {
            if (!string.Equals(row.Title, document.Title, StringComparison.Ordinal))
                yield return $"title: sheet '{row.Title}' vs document '{document.Title}'.";

            if (row.Type != document.Type)
                yield return $"type: sheet '{Vocabulary.ToWire(row.Type)}' vs document '{Vocabulary.ToWire(document.Type)}'.";

            // No phase comparison: the document does not carry a phase. The tab it lives on is the
            // only statement of state, so there is nothing to drift against.

            if (row.Score.BusinessValue != document.Score.BusinessValue
                || row.Score.TimeCriticality != document.Score.TimeCriticality
                || row.Score.RiskReductionOpportunityEnablement != document.Score.RiskReductionOpportunityEnablement
                || row.Score.JobSize != document.Score.JobSize)
            {
                yield return "wsjf scores differ between the sheet and the document. The sheet wins — run 'backlog reindex' only if the sheet is the damaged one.";
            }
        }

        internal static string Slug(string title)
        {
            var builder = new StringBuilder(title.Length);
            var lastWasDash = false;

            foreach (var character in title.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(character);
                    lastWasDash = false;
                }
                else if (!lastWasDash && builder.Length > 0)
                {
                    builder.Append('-');
                    lastWasDash = true;
                }
            }

            var slug = builder.ToString().Trim('-');
            if (slug.Length > 50)
                slug = slug[..50].Trim('-');

            return slug.Length == 0 ? "ticket" : slug;
        }
    }
}

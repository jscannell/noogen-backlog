using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Cli
{
    public class Commands
    {
        readonly LocalConfig _config;

        public Commands(LocalConfig config)
        {
            _config = config;
        }

        IBacklogStore Store() => Program.CreateStore(_config);

        static DateTimeOffset Now => DateTimeOffset.UtcNow;

        // --- setup ---

        public async Task<int> InitAsync(CommandLine command)
        {
            var driveId = command.Option("drive") ?? _config.SharedDriveId
                ?? throw new UsageException("--drive <sharedDriveId> is required the first time.");

            var initializer = new BacklogInitializer(
                new DriveGateway(new DriveClientFactory()),
                new SheetsGateway(new SheetsClientFactory()));

            var result = await initializer.RunAsync(driveId, _config.SpreadsheetId);

            _config.SharedDriveId = driveId;
            _config.SpreadsheetId = result.SpreadsheetId;
            _config.Save();

            if (command.Json)
            {
                Output.WriteJson(result);
                return 0;
            }

            Output.WriteLine(result.CreatedSpreadsheet ? "Created the backlog index." : "Backlog index already existed — verified and repaired.");
            Output.WriteLine($"  index    {result.SpreadsheetUrl}");
            Output.WriteLine($"  tickets  {result.TicketsFolderId}");
            Output.WriteLine($"  archive  {result.ArchiveFolderId}");
            Output.WriteLine($"  config   {LocalConfig.Path}");
            return 0;
        }

        // --- queries ---

        public async Task<int> ListAsync(CommandLine command)
        {
            var store = Store();
            var tickets = await store.ListAsync(BuildFilter(command));

            if (command.Json)
            {
                Output.WriteJson(tickets.Select(ticket => TicketView.From(ticket)).ToList());
                return 0;
            }

            Output.WriteTable(
                ["rank", "id", "wsjf", "type", "area", "owner", "title"],
                tickets.Select((ticket, index) => (IReadOnlyList<string>)
                [
                    ticket.Rank.HasValue ? ticket.Rank.Value.ToString() : (ticket.Score.Value.HasValue ? (index + 1).ToString() : "-"),
                    ticket.Id,
                    Output.Number(ticket.Score.Value),
                    Vocabulary.ToWire(ticket.Type),
                    Output.Text(ticket.Area),
                    Output.Text(ticket.Owner),
                    ticket.Title
                ]).ToList());

            return 0;
        }

        public async Task<int> NextAsync(CommandLine command)
        {
            var store = Store();
            var filter = BuildFilter(command);
            filter.Top ??= 1;

            var tickets = await store.ListAsync(filter);

            if (command.Json)
            {
                Output.WriteJson(tickets.Select(ticket => TicketView.From(ticket)).ToList());
                return 0;
            }

            if (tickets.Count == 0)
            {
                Output.WriteLine("The queue is empty.");
                return 0;
            }

            foreach (var ticket in tickets)
                Output.WriteLine($"{ticket.Id}  wsjf {Output.Number(ticket.Score.Value)}  {ticket.Title}");

            return 0;
        }

        public async Task<int> WipAsync(CommandLine command)
        {
            var store = Store();
            var now = Now;

            var tickets = await store.WipAsync(BuildFilter(command));
            var flow = await store.FlowAsync(null);
            var settings = await store.GetSettingsAsync();
            var threshold = flow.CycleTimeP85;

            if (command.Json)
            {
                Output.WriteJson(new Dictionary<string, object>
                {
                    ["wipLimit"] = settings.WipLimit,
                    ["inFlight"] = tickets.Count,
                    ["agingThresholdDays"] = threshold ?? 0,
                    ["tickets"] = tickets.Select(ticket => TicketView.From(ticket, now, threshold)).ToList()
                });
                return 0;
            }

            Output.WriteLine($"{tickets.Count} of {settings.WipLimit} in flight" +
                (threshold.HasValue ? $"; aging past {Output.Number(threshold)}d (p85 cycle time)" : string.Empty));
            Output.WriteLine();

            Output.WriteTable(
                ["id", "state", "age", "owner", "title", "blocked because"],
                tickets.Select(ticket =>
                {
                    var age = ticket.AgeDays(now);
                    var aging = threshold.HasValue && age.HasValue && age.Value > threshold.Value;

                    return (IReadOnlyList<string>)
                    [
                        ticket.Id,
                        ticket.State.HasValue ? Vocabulary.ToWire(ticket.State.Value) : "-",
                        (aging ? "! " : string.Empty) + Output.Number(age) + "d",
                        Output.Text(ticket.Owner),
                        ticket.Title,
                        Output.Text(ticket.BlockedReason)
                    ];
                }).ToList());

            return 0;
        }

        public async Task<int> FlowAsync(CommandLine command)
        {
            var store = Store();
            var since = command.SinceOption("since", Now);
            var flow = await store.FlowAsync(since);

            if (command.Json)
            {
                Output.WriteJson(flow);
                return 0;
            }

            Output.WriteLine(since.HasValue ? $"Flow since {Iso.ToText(since.Value)}" : "Flow (all time)");
            Output.WriteLine($"  throughput      {flow.Throughput} done");
            Output.WriteLine($"  cycle time p50  {Output.Number(flow.CycleTimeP50)}d");
            Output.WriteLine($"  cycle time p85  {Output.Number(flow.CycleTimeP85)}d");
            Output.WriteLine($"  lead time  p50  {Output.Number(flow.LeadTimeP50)}d");
            Output.WriteLine($"  lead time  p85  {Output.Number(flow.LeadTimeP85)}d");
            return 0;
        }

        public async Task<int> ShowAsync(CommandLine command)
        {
            var store = Store();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await store.GetAsync(id) ?? throw new KeyNotFoundException($"No ticket '{id}'.");
            var body = await store.GetBodyAsync(id);

            if (command.Json)
            {
                Output.WriteJson(new Dictionary<string, object?>
                {
                    ["ticket"] = TicketView.From(ticket, Now),
                    ["body"] = body
                });
                return 0;
            }

            Output.WriteLine($"{ticket.Id}  [{Vocabulary.ToWire(ticket.Phase)}]  {ticket.Title}");
            Output.WriteLine($"  type {Vocabulary.ToWire(ticket.Type)}   area {Output.Text(ticket.Area)}   owner {Output.Text(ticket.Owner)}");
            Output.WriteLine($"  wsjf {Output.Number(ticket.Score.Value)} (bv {Output.Number(ticket.Score.BusinessValue)}, tc {Output.Number(ticket.Score.TimeCriticality)}, rroe {Output.Number(ticket.Score.RiskReductionOpportunityEnablement)}, size {Output.Number(ticket.Score.JobSize)})");

            if (ticket.State.HasValue)
                Output.WriteLine($"  state {Vocabulary.ToWire(ticket.State.Value)}{(ticket.BlockedReason is null ? string.Empty : $" — {ticket.BlockedReason}")}");

            if (ticket.Outcome.HasValue)
                Output.WriteLine($"  outcome {Vocabulary.ToWire(ticket.Outcome.Value)}   lead {Output.Number(ticket.LeadDays)}d   cycle {Output.Number(ticket.CycleDays)}d");

            Output.WriteLine($"  {Output.Text(ticket.DocUrl)}");
            Output.WriteLine();
            Output.WriteLine(body);
            return 0;
        }

        // --- capture and edit ---

        public async Task<int> NewAsync(CommandLine command)
        {
            var store = Store();

            var request = new NewTicket
            {
                Title = command.RequireOption("title"),
                Type = Vocabulary.Parse<TicketType>(command.Option("type") ?? "feature", "type"),
                Area = command.Option("area") ?? string.Empty,
                Owner = command.Has("owner") ? _config.ResolveOwner(command.Option("owner")) : null,
                Description = command.Option("description"),
                Score = ReadScore(command)
            };

            var ticket = await store.CreateAsync(request);
            return Report(command, ticket, $"Created {ticket.Id}.");
        }

        public async Task<int> EditAsync(CommandLine command)
        {
            var store = Store();
            var id = command.RequirePositional(0, "a ticket id");

            if (command.Has("status") || command.Has("phase"))
            {
                throw new UsageException(
                    "There is no --status flag: the tab a ticket lives on is its state. " +
                    "Use 'backlog start', 'block', 'unblock', 'review', 'archive', or 'restore'.");
            }

            var edit = new TicketEdit
            {
                Title = command.Option("title"),
                Area = command.Option("area"),
                Owner = command.Has("owner") ? _config.ResolveOwner(command.Option("owner")) : null,
                Type = command.Has("type") ? Vocabulary.Parse<TicketType>(command.RequireOption("type"), "type") : null
            };

            var ticket = await store.UpdateAsync(id, edit);
            return Report(command, ticket, $"Updated {ticket.Id}.");
        }

        public async Task<int> ScoreAsync(CommandLine command)
        {
            var store = Store();
            var id = command.RequirePositional(0, "a ticket id");
            var score = ReadScore(command);

            if (!score.BusinessValue.HasValue && !score.TimeCriticality.HasValue
                && !score.RiskReductionOpportunityEnablement.HasValue && !score.JobSize.HasValue)
            {
                throw new UsageException("Pass at least one of --bv, --tc, --rroe, --size.");
            }

            var ticket = await store.ScoreAsync(id, score);
            return Report(command, ticket, $"{ticket.Id} scored — wsjf {Output.Number(ticket.Score.Value)}.");
        }

        public async Task<int> NoteAsync(CommandLine command)
        {
            var store = Store();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await store.AppendNoteAsync(id, command.RequireOption("text"));
            return Report(command, ticket, $"Noted on {ticket.Id}.");
        }

        // --- lifecycle ---

        public async Task<int> StartAsync(CommandLine command)
        {
            var store = Store();
            var id = command.RequirePositional(0, "a ticket id");
            var owner = command.Has("owner") ? _config.ResolveOwner(command.Option("owner")) : _config.ResolveOwner("me");

            var ticket = await store.StartAsync(id, owner, command.HasFlag("force"));
            return Report(command, ticket, $"Started {ticket.Id} ({ticket.Owner}). It is no longer WSJF-ranked.");
        }

        public async Task<int> BlockAsync(CommandLine command)
        {
            var store = Store();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await store.SetStateAsync(id, WorkState.Blocked, command.RequireOption("reason"));
            return Report(command, ticket, $"Blocked {ticket.Id}.");
        }

        public async Task<int> SetStateAsync(CommandLine command, WorkState state)
        {
            var store = Store();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await store.SetStateAsync(id, state, null);
            return Report(command, ticket, $"{ticket.Id} is now {Vocabulary.ToWire(state)}.");
        }

        public async Task<int> ArchiveAsync(CommandLine command)
        {
            var store = Store();
            var id = command.RequirePositional(0, "a ticket id");
            var outcome = Vocabulary.Parse<Outcome>(command.Option("as") ?? "done", "outcome");

            var ticket = await store.ArchiveAsync(id, outcome, command.Option("note"));
            return Report(command, ticket,
                $"Archived {ticket.Id} as {Vocabulary.ToWire(outcome)} — lead {Output.Number(ticket.LeadDays)}d, cycle {Output.Number(ticket.CycleDays)}d. The document was moved, not deleted.");
        }

        public async Task<int> RestoreAsync(CommandLine command)
        {
            var store = Store();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await store.RestoreAsync(id);
            return Report(command, ticket, $"Restored {ticket.Id} to the backlog. Rescore it before it can rank.");
        }

        // --- maintenance ---

        public async Task<int> ReindexAsync(CommandLine command)
        {
            var store = Store();
            var repaired = await store.ReindexAsync();

            if (command.Json)
            {
                Output.WriteJson(new Dictionary<string, int> { ["repaired"] = repaired });
                return 0;
            }

            Output.WriteLine($"Rewrote {repaired} row(s) from their documents.");
            return 0;
        }

        public async Task<int> DoctorAsync(CommandLine command)
        {
            var store = Store();
            var report = await store.DoctorAsync();

            if (command.Json)
            {
                Output.WriteJson(new Dictionary<string, object>
                {
                    ["healthy"] = report.IsHealthy,
                    ["ticketCount"] = report.TicketCount,
                    ["issues"] = report.Issues
                });
                return report.IsHealthy ? 0 : 1;
            }

            if (report.IsHealthy)
            {
                Output.WriteLine($"{report.TicketCount} ticket(s), no issues.");
                return 0;
            }

            Output.WriteLine($"{report.TicketCount} ticket(s), {report.Issues.Count} issue(s):");
            Output.WriteLine();
            Output.WriteTable(
                ["id", "kind", "detail"],
                report.Issues.Select(issue => (IReadOnlyList<string>)[issue.Id, issue.Kind, issue.Detail]).ToList());

            return 1;
        }

        // --- helpers ---

        TicketFilter BuildFilter(CommandLine command) => new()
        {
            Area = command.Option("area"),
            Owner = command.Has("owner") ? _config.ResolveOwner(command.Option("owner")) : null,
            Top = command.IntOption("top")
        };

        static WsjfScore ReadScore(CommandLine command) => new()
        {
            BusinessValue = command.IntOption("bv"),
            TimeCriticality = command.IntOption("tc"),
            RiskReductionOpportunityEnablement = command.IntOption("rroe"),
            JobSize = command.IntOption("size")
        };

        static int Report(CommandLine command, Ticket ticket, string message)
        {
            if (command.Json)
                Output.WriteJson(TicketView.From(ticket, Now));
            else
                Output.WriteLine(message);

            return 0;
        }
    }
}

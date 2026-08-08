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

        Task<IBacklogStore> StoreAsync() => Program.CreateStoreAsync(_config);

        static DateTimeOffset Now => DateTimeOffset.UtcNow;

        /// <summary>
        /// Human output renders in the backlog's configured timezone; --utc opts out. JSON is
        /// deliberately never localised — it is the machine contract the skill and the future
        /// agent toolset parse, and a moving representation there would be a trap.
        /// </summary>
        static async Task<TimeZoneInfo> ZoneAsync(CommandLine command, IBacklogStore store)
        {
            if (command.HasFlag("utc"))
                return TimeZoneInfo.Utc;

            var settings = await store.GetSettingsAsync();
            return settings.Zone;
        }

        static string When(DateTimeOffset? instant, TimeZoneInfo zone) =>
            instant.HasValue && instant.Value != default ? SheetTime.FormatWithZone(instant.Value, zone) : "-";

        // --- account ---

        public async Task<int> LoginAsync(CommandLine command)
        {
            var store = Program.CreateCredentialStore();
            var account = _config.ResolveAccount(command.Option("account"));

            // Fail before promising a browser we cannot open.
            if (!Program.ResolveOAuthClient().IsConfigured)
                throw new OAuthClientNotConfiguredException(LocalConfig.OAuthClientPath);

            Output.WriteError("Opening your browser to sign in with Google...");

            var credential = await store.AuthorizeAsync(account, GoogleWorkspaceScopes.All);
            var email = await UserCredentialStore.GetEmailAsync(credential);

            // Re-key the cached token to the real address, so a machine can hold several accounts
            // without them colliding on "default". A rename, not a second authorisation — the
            // browser opens once.
            if (!string.IsNullOrEmpty(email) && account == UserCredentialStore.DefaultAccountKey)
            {
                await store.RenameAsync(account, email, GoogleWorkspaceScopes.All);
                account = email;
            }

            _config.Account = account;
            _config.DefaultOwner ??= email;
            _config.Save();

            if (command.Json)
            {
                Output.WriteJson(new Dictionary<string, object?>
                {
                    ["account"] = account,
                    ["email"] = email,
                    ["tokenProtection"] = store.Protector.Description,
                    ["osBacked"] = store.Protector.IsOsBacked
                });
                return 0;
            }

            Output.WriteLine($"Signed in as {email ?? account}.");
            Output.WriteLine($"  refresh token protected by: {store.Protector.Description}");

            WarnIfUnprotected(store);
            return 0;
        }

        public async Task<int> LogoutAsync(CommandLine command)
        {
            var store = Program.CreateCredentialStore();
            var account = _config.ResolveAccount(command.Option("account"));

            var removed = await store.RevokeAsync(account, GoogleWorkspaceScopes.All);

            if (string.Equals(_config.Account, account, StringComparison.OrdinalIgnoreCase))
            {
                _config.Account = null;
                _config.Save();
            }

            if (command.Json)
            {
                Output.WriteJson(new Dictionary<string, object> { ["account"] = account, ["removed"] = removed });
                return 0;
            }

            Output.WriteLine(removed
                ? $"Signed out {account}. The token was revoked with Google and deleted locally."
                : $"No stored credential for {account}.");
            return 0;
        }

        public async Task<int> WhoAmIAsync(CommandLine command)
        {
            var store = Program.CreateCredentialStore();
            var account = _config.ResolveAccount(null);
            var client = Program.ResolveOAuthClient();

            // whoami must answer even when the answer is "nothing is set up" — that is exactly
            // when someone runs it.
            ResolvedCredential? resolved = null;
            string? problem = null;

            try
            {
                resolved = await Program.ResolveCredentialAsync(_config);
            }
            catch (Exception exception)
            {
                problem = exception.Message;
            }

            if (command.Json)
            {
                Output.WriteJson(new Dictionary<string, object?>
                {
                    ["account"] = account,
                    ["source"] = resolved?.Source.ToString() ?? "None",
                    ["description"] = resolved?.Description,
                    ["problem"] = problem,
                    ["oauthClientId"] = client.ClientId,
                    ["oauthClientSource"] = client.Source,
                    ["tokenProtection"] = store.Protector.Description,
                    ["osBacked"] = store.Protector.IsOsBacked,
                    ["accounts"] = store.ListAccounts()
                });
                return resolved is null ? 3 : 0;
            }

            Output.WriteLine($"account        {account}");
            Output.WriteLine($"authenticated  {resolved?.Description ?? "not authenticated"}");
            Output.WriteLine($"oauth client   {(client.IsConfigured ? client.ClientId : "not configured")}");
            Output.WriteLine($"  from         {client.Source}");
            Output.WriteLine($"token store    {store.TokenDirectory}");
            Output.WriteLine($"protected by   {store.Protector.Description}");

            if (problem is not null)
                Output.WriteLine($"problem        {problem}");

            var accounts = store.ListAccounts();
            if (accounts.Count > 1)
                Output.WriteLine($"signed in      {string.Join(", ", accounts)}");

            WarnIfUnprotected(store);
            return resolved is null ? 3 : 0;
        }

        static void WarnIfUnprotected(UserCredentialStore store)
        {
            if (store.Protector.IsOsBacked)
                return;

            Output.WriteError(
                "\nWARNING: no OS keystore is available here, so the refresh token is stored unencrypted.\n" +
                "A copy of that file grants access to your Drive from anywhere until it is revoked.\n" +
                "On a headless machine, prefer a service account: set " +
                $"{LocalConfig.ServiceAccountKeyEnvironmentVariable} to a key file instead of signing in.");
        }

        // --- setup ---

        public async Task<int> InitAsync(CommandLine command)
        {
            var driveId = command.Option("drive") ?? _config.SharedDriveId
                ?? throw new UsageException("--drive <sharedDriveId> is required the first time.");

            // Seeds from this machine on first run; thereafter the Config tab wins unless
            // --timezone is passed explicitly.
            var timeZoneId = command.Option("timezone") ?? (_config.SpreadsheetId is null ? SheetTime.LocalIanaId() : null);

            var credential = await Program.ResolveCredentialAsync(_config);

            var retry = Program.CreateRetryHandler();

            var initializer = new BacklogInitializer(
                new DriveGateway(new DriveClientFactory(credential.Initializer, retry: retry)),
                new SheetsGateway(new SheetsClientFactory(credential.Initializer, retry: retry)));

            var result = await initializer.RunAsync(driveId, _config.SpreadsheetId, timeZoneId);

            _config.SharedDriveId = driveId;
            _config.SpreadsheetId = result.SpreadsheetId;
            _config.Save();

            if (command.Json)
            {
                Output.WriteJson(result);
                return 0;
            }

            Output.WriteLine(result.CreatedSpreadsheet ? "Created the backlog index." : "Backlog index already existed — verified and repaired.");
            Output.WriteLine($"  index     {result.SpreadsheetUrl}");
            Output.WriteLine($"  tickets   {result.TicketsFolderId}");
            Output.WriteLine($"  archive   {result.ArchiveFolderId}");
            Output.WriteLine($"  timezone  {result.TimeZoneId}");
            Output.WriteLine($"  config    {LocalConfig.Path}");
            return 0;
        }

        /// <summary>
        /// Unpacks the Claude Code skill this tool carries. See <see cref="EmbeddedSkill"/> for
        /// why it rides inside the binary rather than being distributed alongside it.
        /// </summary>
        public int InstallSkill(CommandLine command)
        {
            var root = command.Option("path") ?? LocalConfig.SkillsDirectory;
            var installation = EmbeddedSkill.Install(root, command.HasFlag("force"));

            if (!installation.Applied)
                return ReportSkillConflict(command, installation);

            if (command.Json)
            {
                Output.WriteJson(new Dictionary<string, object?>
                {
                    ["name"] = EmbeddedSkill.Name,
                    ["path"] = installation.Path,
                    ["upToDate"] = installation.UpToDate,
                    ["written"] = installation.Written,
                    ["removed"] = installation.Removed
                });
                return 0;
            }

            if (installation.UpToDate)
            {
                Output.WriteLine($"The {EmbeddedSkill.Name} skill at {installation.Path} is already up to date.");
                return 0;
            }

            Output.WriteLine($"Installed the {EmbeddedSkill.Name} skill to {installation.Path}");

            foreach (var file in installation.Written)
                Output.WriteLine($"  + {file}");

            foreach (var file in installation.Removed)
                Output.WriteLine($"  - {file}   (not part of this version)");

            Output.WriteLine();
            Output.WriteLine("Claude Code loads it in the next session you start.");
            return 0;
        }

        /// <summary>
        /// Mirrors the error shape <see cref="Program"/> writes, so the machine contract is the
        /// same whether the refusal comes from here or from a thrown exception.
        /// </summary>
        static int ReportSkillConflict(CommandLine command, SkillInstallation installation)
        {
            var message =
                $"The skill at {installation.Path} is not the one in this tool. " +
                "Re-run with --force to replace it.";

            if (command.Json)
            {
                Output.WriteJson(new Dictionary<string, object?>
                {
                    ["kind"] = "skill-differs",
                    ["error"] = message,
                    ["path"] = installation.Path,
                    ["differences"] = installation.Differences
                });
                return 1;
            }

            Output.WriteError($"error (skill-differs): {message}");

            foreach (var difference in installation.Differences)
                Output.WriteError($"  {difference.Kind,-8} {difference.Path}");

            return 1;
        }

        // --- queries ---

        public async Task<int> ListAsync(CommandLine command)
        {
            var store = await StoreAsync();
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
            var store = await StoreAsync();
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
            var store = await StoreAsync();
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

            var zone = await ZoneAsync(command, store);

            Output.WriteLine($"{tickets.Count} of {settings.WipLimit} in flight" +
                (threshold.HasValue ? $"; aging past {Output.Number(threshold)}d (p85 cycle time)" : string.Empty));
            Output.WriteLine();

            Output.WriteTable(
                ["id", "state", "started", "age", "owner", "title", "blocked because"],
                tickets.Select(ticket =>
                {
                    var age = ticket.AgeDays(now);
                    var aging = threshold.HasValue && age.HasValue && age.Value > threshold.Value;

                    return (IReadOnlyList<string>)
                    [
                        ticket.Id,
                        ticket.State.HasValue ? Vocabulary.ToWire(ticket.State.Value) : "-",
                        When(ticket.StartedAt, zone),
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
            var store = await StoreAsync();
            var since = command.SinceOption("since", Now);
            var flow = await store.FlowAsync(since);

            if (command.Json)
            {
                Output.WriteJson(flow);
                return 0;
            }

            var zone = await ZoneAsync(command, store);
            Output.WriteLine(since.HasValue ? $"Flow since {When(since.Value, zone)}" : "Flow (all time)");
            Output.WriteLine($"  throughput      {flow.Throughput} done");
            Output.WriteLine($"  cycle time p50  {Output.Number(flow.CycleTimeP50)}d");
            Output.WriteLine($"  cycle time p85  {Output.Number(flow.CycleTimeP85)}d");
            Output.WriteLine($"  lead time  p50  {Output.Number(flow.LeadTimeP50)}d");
            Output.WriteLine($"  lead time  p85  {Output.Number(flow.LeadTimeP85)}d");
            return 0;
        }

        public async Task<int> ShowAsync(CommandLine command)
        {
            var store = await StoreAsync();
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
            Output.WriteLine($"  wsjf {Output.Number(ticket.Score.Value)} (business value {Output.Number(ticket.Score.BusinessValue)}, time criticality {Output.Number(ticket.Score.TimeCriticality)}, risk & opportunity {Output.Number(ticket.Score.RiskReductionOpportunityEnablement)}, job size {Output.Number(ticket.Score.JobSize)})");

            if (ticket.State.HasValue)
                Output.WriteLine($"  state {Vocabulary.ToWire(ticket.State.Value)}{(ticket.BlockedReason is null ? string.Empty : $" — {ticket.BlockedReason}")}");

            if (ticket.Outcome.HasValue)
                Output.WriteLine($"  outcome {Vocabulary.ToWire(ticket.Outcome.Value)}   lead {Output.Number(ticket.LeadDays)}d   cycle {Output.Number(ticket.CycleDays)}d");

            var zone = await ZoneAsync(command, store);
            Output.WriteLine($"  created {When(ticket.Created, zone)}   updated {When(ticket.Updated, zone)}");

            if (ticket.StartedAt.HasValue || ticket.ArchivedAt.HasValue)
                Output.WriteLine($"  started {When(ticket.StartedAt, zone)}   archived {When(ticket.ArchivedAt, zone)}");

            Output.WriteLine($"  {Output.Text(ticket.DocUrl)}");
            Output.WriteLine();
            Output.WriteLine(body);
            return 0;
        }

        // --- capture and edit ---

        public async Task<int> NewAsync(CommandLine command)
        {
            var store = await StoreAsync();

            var request = new NewTicket
            {
                Title = command.RequireOption("title"),
                Type = Vocabulary.Parse<TicketType>(command.Option("type") ?? "feature", "type"),
                Area = command.Option("area") ?? string.Empty,
                Owner = command.Has("owner") ? _config.ResolveOwner(command.Option("owner")) : null,
                Description = TextInput.ReadDescription(command),
                Score = ReadScore(command)
            };

            var ticket = await store.CreateAsync(request);
            return Report(command, ticket, $"Created {ticket.Id}.");
        }

        public async Task<int> EditAsync(CommandLine command)
        {
            var store = await StoreAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var edit = new TicketEdit
            {
                Title = command.Option("title"),
                Area = command.Option("area"),
                Owner = command.Has("owner") ? _config.ResolveOwner(command.Option("owner")) : null,
                Type = command.Has("type") ? Vocabulary.Parse<TicketType>(command.RequireOption("type"), "type") : null,
                Description = TextInput.ReadDescription(command)
            };

            var ticket = await store.UpdateAsync(id, edit);
            return Report(command, ticket, $"Updated {ticket.Id}.");
        }

        public async Task<int> ScoreAsync(CommandLine command)
        {
            var store = await StoreAsync();
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
            var store = await StoreAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await store.AppendNoteAsync(id, command.RequireOption("text"));
            return Report(command, ticket, $"Noted on {ticket.Id}.");
        }

        // --- lifecycle ---

        public async Task<int> StartAsync(CommandLine command)
        {
            var store = await StoreAsync();
            var id = command.RequirePositional(0, "a ticket id");
            var owner = command.Has("owner") ? _config.ResolveOwner(command.Option("owner")) : _config.ResolveOwner("me");

            var ticket = await store.StartAsync(id, owner, command.HasFlag("force"));
            return Report(command, ticket, $"Started {ticket.Id} ({ticket.Owner}). It is no longer WSJF-ranked.");
        }

        public async Task<int> BlockAsync(CommandLine command)
        {
            var store = await StoreAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await store.SetStateAsync(id, WorkState.Blocked, command.RequireOption("reason"));
            return Report(command, ticket, $"Blocked {ticket.Id}.");
        }

        public async Task<int> SetStateAsync(CommandLine command, WorkState state)
        {
            var store = await StoreAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await store.SetStateAsync(id, state, null);
            return Report(command, ticket, $"{ticket.Id} is now {Vocabulary.ToWire(state)}.");
        }

        public async Task<int> ArchiveAsync(CommandLine command)
        {
            var store = await StoreAsync();
            var id = command.RequirePositional(0, "a ticket id");
            var outcome = Vocabulary.Parse<Outcome>(command.Option("as") ?? "done", "outcome");

            var ticket = await store.ArchiveAsync(id, outcome, command.Option("note"));
            return Report(command, ticket,
                $"Archived {ticket.Id} as {Vocabulary.ToWire(outcome)} — lead {Output.Number(ticket.LeadDays)}d, cycle {Output.Number(ticket.CycleDays)}d. The document was moved, not deleted.");
        }

        public async Task<int> RestoreAsync(CommandLine command)
        {
            var store = await StoreAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await store.RestoreAsync(id);
            return Report(command, ticket, $"Restored {ticket.Id} to the backlog. Rescore it before it can rank.");
        }

        // --- maintenance ---

        public async Task<int> ReindexAsync(CommandLine command)
        {
            var store = await StoreAsync();
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
            var store = await StoreAsync();
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
            BusinessValue = command.IntOption("bv", "business-value"),
            TimeCriticality = command.IntOption("tc", "time-criticality"),
            RiskReductionOpportunityEnablement = command.IntOption("rroe", "risk-opportunity"),
            JobSize = command.IntOption("size", "job-size")
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

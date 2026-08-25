using Noogen.Backlog.Verbs;
using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Cli
{
    /// <summary>
    /// The terminal half of the tool: read a command line, ask <see cref="BacklogApi"/>, render.
    ///
    /// Nothing here decides what an answer *is*. A verb that composes more than one store call, or
    /// that has to report something beside its result, does that in the API, because the MCP server
    /// answers the same questions and the two would otherwise drift.
    /// </summary>
    public class Commands
    {
        readonly LocalConfig _config;

        public Commands(LocalConfig config)
        {
            _config = config;
        }

        Task<BacklogApi> ApiAsync() => Program.CreateApiAsync(_config);

        /// <summary>
        /// Human output renders in the backlog's configured timezone; --utc opts out. JSON is
        /// deliberately never localised — it is the machine contract the skill, the MCP server and
        /// the future agent toolset parse, and a moving representation there would be a trap.
        /// </summary>
        static async Task<TimeZoneInfo> ZoneAsync(CommandLine command, BacklogApi api)
        {
            if (command.HasFlag("utc"))
                return TimeZoneInfo.Utc;

            var settings = await api.SettingsAsync();
            return settings.Zone;
        }

        /// <summary>
        /// Renders one of the machine contract's UTC timestamps for a person.
        ///
        /// The view carries text rather than an instant because text is what goes on the wire, and
        /// one shape is worth a parse here: a second representation held alongside it would be a
        /// second thing to keep true. An unset timestamp reads as "-" rather than year one.
        /// </summary>
        static string When(string? iso, TimeZoneInfo zone)
        {
            if (string.IsNullOrEmpty(iso))
                return "-";

            var instant = Iso.Parse(iso, "timestamp");
            return instant == default ? "-" : SheetTime.FormatWithZone(instant, zone);
        }

        static IReadOnlySet<string>? Fields(CommandLine command) => BacklogJson.ParseFields(command.Option("fields"));

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
            var api = await ApiAsync();
            var queue = await api.ListAsync(BuildFilter(command));

            if (command.Json)
            {
                Output.WriteJson(queue, Fields(command));
                return 0;
            }

            WriteQueue(queue);
            return 0;
        }

        static void WriteQueue(TicketListView queue) =>
            Output.WriteTable(
                ["rank", "id", "wsjf", "type", "area", "owner", "title"],
                queue.Tickets.Select((ticket, index) => (IReadOnlyList<string>)
                [
                    ticket.Rank.HasValue ? ticket.Rank.Value.ToString() : (ticket.Wsjf.HasValue ? (index + 1).ToString() : "-"),
                    ticket.Id,
                    Output.Number(ticket.Wsjf),
                    ticket.Type,
                    Output.Text(ticket.Area),
                    Output.Text(ticket.Owner),
                    ticket.Title
                ]).ToList());

        public async Task<int> NextAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var queue = await api.NextAsync(BuildFilter(command));

            if (command.Json)
            {
                Output.WriteJson(queue, Fields(command));
                return 0;
            }

            if (queue.Tickets.Count == 0)
            {
                Output.WriteLine("The queue is empty.");
                return 0;
            }

            foreach (var ticket in queue.Tickets)
                Output.WriteLine($"{ticket.Id}  wsjf {Output.Number(ticket.Wsjf)}  {ticket.Title}");

            return 0;
        }

        public async Task<int> WipAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var wip = await api.WipAsync(BuildFilter(command));

            if (command.Json)
            {
                Output.WriteJson(wip, Fields(command));
                return 0;
            }

            var zone = await ZoneAsync(command, api);
            var threshold = wip.AgingThresholdDays > 0 ? wip.AgingThresholdDays : (double?)null;

            Output.WriteLine($"{wip.InFlight} of {wip.WipLimit} in flight" +
                (threshold.HasValue ? $"; aging past {Output.Number(threshold)}d (p85 cycle time)" : string.Empty));
            Output.WriteLine();

            Output.WriteTable(
                ["id", "state", "started", "age", "owner", "title", "blocked because"],
                wip.Tickets.Select(ticket => (IReadOnlyList<string>)
                [
                    ticket.Id,
                    Output.Text(ticket.State),
                    When(ticket.StartedAt, zone),
                    (ticket.Aging == true ? "! " : string.Empty) + Output.Number(ticket.AgeDays) + "d",
                    Output.Text(ticket.Owner),
                    ticket.Title,
                    Output.Text(ticket.BlockedReason)
                ]).ToList());

            return 0;
        }

        public async Task<int> FindAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var text = command.RequirePositional(0, "some text to search for");

            var matches = await api.FindAsync(text, BuildFilter(command));

            if (command.Json)
            {
                Output.WriteJson(matches, Fields(command));
                return 0;
            }

            if (matches.Tickets.Count == 0)
            {
                Output.WriteLine($"Nothing matched '{text}'.");

                // Both halves of "no results" that are not "no such ticket", said once, here,
                // because this is the moment somebody is about to conclude the ticket does not
                // exist and file it again.
                Output.WriteLine("Names match on any fragment, but document text matches whole words only, "
                    + "and a document written in the last few minutes may not be indexed yet.");
                return 0;
            }

            Output.WriteTable(
                ["id", "match", "phase", "wsjf", "area", "owner", "title"],
                matches.Tickets.Select(ticket => (IReadOnlyList<string>)
                [
                    ticket.Id,
                    string.Join("+", ticket.Match ?? []),
                    ticket.Phase,
                    Output.Number(ticket.Wsjf),
                    Output.Text(ticket.Area),
                    Output.Text(ticket.Owner),
                    ticket.Title
                ]).ToList());

            return 0;
        }

        public async Task<int> FlowAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var since = command.SinceOption("since", DateTimeOffset.UtcNow);
            var view = await api.FlowAsync(since);
            var flow = view.Metrics;

            if (command.Json)
            {
                Output.WriteJson(view);
                return 0;
            }

            var zone = await ZoneAsync(command, api);
            Output.WriteLine(since.HasValue ? $"Flow since {When(Iso.ToText(since.Value), zone)}" : "Flow (all time)");
            Output.WriteLine($"  throughput      {flow.Throughput} done");
            Output.WriteLine($"  cycle time p50  {Output.Number(flow.CycleTimeP50)}d");
            Output.WriteLine($"  cycle time p85  {Output.Number(flow.CycleTimeP85)}d");
            Output.WriteLine($"  lead time  p50  {Output.Number(flow.LeadTimeP50)}d");
            Output.WriteLine($"  lead time  p85  {Output.Number(flow.LeadTimeP85)}d");
            return 0;
        }

        public async Task<int> ShowAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var detail = await api.ShowAsync(id, command.Option("section"), command.Has("full"));
            var ticket = detail.Ticket;

            if (command.Json)
            {
                Output.WriteJson(detail);
                return 0;
            }

            Output.WriteLine($"{ticket.Id}  [{ticket.Phase}]  {ticket.Title}");
            Output.WriteLine($"  type {ticket.Type}   area {Output.Text(ticket.Area)}   owner {Output.Text(ticket.Owner)}");
            Output.WriteLine($"  wsjf {Output.Number(ticket.Wsjf)} (business value {Output.Number(ticket.Bv)}, time criticality {Output.Number(ticket.Tc)}, risk & opportunity {Output.Number(ticket.Rroe)}, job size {Output.Number(ticket.Size)})");

            if (ticket.State is not null)
                Output.WriteLine($"  state {ticket.State}{(ticket.BlockedReason is null ? string.Empty : $" — {ticket.BlockedReason}")}");

            if (ticket.Outcome is not null)
                Output.WriteLine($"  outcome {ticket.Outcome}   lead {Output.Number(ticket.LeadDays)}d   cycle {Output.Number(ticket.CycleDays)}d");

            var zone = await ZoneAsync(command, api);
            Output.WriteLine($"  created {When(ticket.Created, zone)}   updated {When(ticket.Updated, zone)}");

            if (ticket.StartedAt is not null || ticket.ArchivedAt is not null)
                Output.WriteLine($"  started {When(ticket.StartedAt, zone)}   archived {When(ticket.ArchivedAt, zone)}");

            Output.WriteLine($"  {Output.Text(ticket.DocUrl)}");
            Output.WriteLine();
            Output.WriteLine(detail.Body);
            return 0;
        }

        // --- capture and edit ---

        public async Task<int> NewAsync(CommandLine command)
        {
            TextInput.RejectSharedStandardInput(command);

            var api = await ApiAsync();

            var request = new NewTicket
            {
                Title = command.RequireOption("title"),
                Type = Vocabulary.Parse<TicketType>(command.Option("type") ?? "feature", "type"),
                Area = command.Option("area") ?? string.Empty,
                Owner = command.Has("owner") ? _config.ResolveOwner(command.Option("owner")) : null,
                Description = TextInput.ReadDescription(command),
                AcceptanceCriteria = TextInput.ReadAcceptanceCriteria(command),
                Score = ReadScore(command)
            };

            var filed = await api.CreateAsync(request);

            Remind(filed);

            return Report(command, filed, $"Created {filed.Ticket.Id}.");
        }

        /// <summary>
        /// Names the sections that went in as `*TODO*`, and how to fill them.
        ///
        /// Stderr, and before the report: stdout under `--json` is one document, and this is a
        /// reminder rather than part of the result.
        /// </summary>
        static void Remind(NewTicketView filed)
        {
            if (filed.Reminder is null)
                return;

            var id = filed.Ticket.Id;

            Output.WriteError(
                $"{id} has no {string.Join(" and no ", filed.MissingSections)} — the section(s) say *TODO*. Fill in with:\n"
                + $"  backlog edit {id}"
                + (filed.MissingSections.Contains("description") ? " --description-file <path>" : string.Empty)
                + (filed.MissingSections.Contains("acceptance criteria") ? " --acceptance-criteria-file <path>" : string.Empty));
        }

        public async Task<int> EditAsync(CommandLine command)
        {
            TextInput.RejectSharedStandardInput(command);

            var api = await ApiAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var edit = new TicketEdit
            {
                Title = command.Option("title"),
                Area = command.Option("area"),
                Owner = command.Has("owner") ? _config.ResolveOwner(command.Option("owner")) : null,
                Type = command.Has("type") ? Vocabulary.Parse<TicketType>(command.RequireOption("type"), "type") : null,
                Description = TextInput.ReadDescription(command),
                AcceptanceCriteria = TextInput.ReadAcceptanceCriteria(command),
                Note = TextInput.ReadProse(command, "note")
            };

            var ticket = await api.UpdateAsync(id, edit);
            return Report(command, ticket, $"Updated {ticket.Id}.");
        }

        public async Task<int> ScoreAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await api.ScoreAsync(id, ReadScore(command));
            return Report(command, ticket, $"{ticket.Id} scored — wsjf {Output.Number(ticket.Wsjf)}.");
        }

        public async Task<int> NoteAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await api.NoteAsync(id, TextInput.RequireProse(command, "text"));
            return Report(command, ticket, $"Noted on {ticket.Id}.");
        }

        // --- lifecycle ---

        public async Task<int> StartAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var id = command.RequirePositional(0, "a ticket id");
            var owner = command.Has("owner") ? _config.ResolveOwner(command.Option("owner")) : _config.ResolveOwner("me");

            var ticket = await api.StartAsync(id, owner, command.HasFlag("force"));
            return Report(command, ticket, $"Started {ticket.Id} ({ticket.Owner}). It is no longer WSJF-ranked.");
        }

        public async Task<int> BlockAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await api.SetStateAsync(id, WorkState.Blocked, TextInput.RequireProse(command, "reason"));
            return Report(command, ticket, $"Blocked {ticket.Id}.");
        }

        public async Task<int> SetStateAsync(CommandLine command, WorkState state)
        {
            var api = await ApiAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await api.SetStateAsync(id, state, null);
            return Report(command, ticket, $"{ticket.Id} is now {Vocabulary.ToWire(state)}.");
        }

        public async Task<int> ArchiveAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var id = command.RequirePositional(0, "a ticket id");
            var outcome = Vocabulary.Parse<Outcome>(command.Option("as") ?? "done", "outcome");

            var ticket = await api.ArchiveAsync(id, outcome, TextInput.ReadProse(command, "note"));
            return Report(command, ticket,
                $"Archived {ticket.Id} as {Vocabulary.ToWire(outcome)} — lead {Output.Number(ticket.LeadDays)}d, cycle {Output.Number(ticket.CycleDays)}d. The document was moved, not deleted.");
        }

        public async Task<int> RestoreAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var id = command.RequirePositional(0, "a ticket id");

            var ticket = await api.RestoreAsync(id);
            return Report(command, ticket, $"Restored {ticket.Id} to the backlog. Rescore it before it can rank.");
        }

        // --- maintenance ---

        public async Task<int> ReindexAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var reindexed = await api.ReindexAsync();

            if (command.Json)
            {
                Output.WriteJson(reindexed);
                return 0;
            }

            Output.WriteLine($"Rewrote {reindexed.Repaired} row(s) from their documents.");
            return 0;
        }

        public async Task<int> DoctorAsync(CommandLine command)
        {
            var api = await ApiAsync();
            var report = await api.DoctorAsync();

            if (command.Json)
            {
                Output.WriteJson(report);
                return report.Healthy ? 0 : 1;
            }

            if (report.Healthy)
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

        static int Report(CommandLine command, IBacklogView view, string message)
        {
            if (command.Json)
                Output.WriteJson(view);
            else
                Output.WriteLine(message);

            return 0;
        }
    }
}

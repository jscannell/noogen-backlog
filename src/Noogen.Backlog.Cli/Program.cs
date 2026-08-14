using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Cli
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // Parsing is inside the try because it can now refuse the line itself — an option
            // declared to take a value and given none is a usage error rather than a flag nothing
            // reads. That leaves no CommandLine to ask about --json, which is why Fail also gets
            // the raw arguments.
            CommandLine? command = null;

            try
            {
                command = CommandLine.Parse(args);
                return await RunAsync(command);
            }
            catch (UsageException exception)
            {
                Fail(command, args, "usage", exception.Message);
                return 2;
            }
            catch (WipLimitExceededException exception)
            {
                Fail(command, args, "wip-limit", exception.Message);
                return 1;
            }
            catch (BacklogTransitionException exception)
            {
                Fail(command, args, "illegal-transition", exception.Message);
                return 1;
            }
            catch (KeyNotFoundException exception)
            {
                Fail(command, args, "not-found", exception.Message);
                return 1;
            }
            catch (NotSignedInException exception)
            {
                Fail(command, args, "not-signed-in", exception.Message);
                return 3;
            }
            catch (OAuthClientNotConfiguredException exception)
            {
                Fail(command, args, "oauth-client-missing", exception.Message);
                return 3;
            }
            catch (OAuthClientInvalidException exception)
            {
                Fail(command, args, "oauth-client-invalid", exception.Message);
                return 3;
            }
            catch (Exception exception) when (GoogleRateLimit.IsRateLimited(exception))
            {
                Fail(command, args, "rate-limited",
                    "Google is rate limiting requests to this backlog, and the command kept being refused after " +
                    "several waits. Nothing was half-written — a rate-limited request is rejected, not applied. " +
                    "Wait a minute and run it again; if it persists, someone may be running a large 'reindex' or " +
                    "'doctor' against the same backlog.");
                return 4;
            }
            catch (ArgumentException exception)
            {
                Fail(command, args, "invalid-argument", exception.Message);
                return 1;
            }
            catch (FormatException exception)
            {
                Fail(command, args, "malformed", exception.Message);
                return 1;
            }
            catch (Exception exception)
            {
                Fail(command, args, "error", exception.Message);
                return 1;
            }
        }

        static async Task<int> RunAsync(CommandLine command)
        {
            if (command.Verb is "help" or "h" or "?")
            {
                WriteHelp();
                return 0;
            }

            // Before anything that costs a request or a sign-in: an option no verb reads used to
            // be ignored, so a typo — or a flag that only exists on another verb — still reported
            // success. See Verbs.
            Verbs.Validate(command);

            var config = LocalConfig.Load();
            var commands = new Commands(config);

            switch (command.Verb)
            {
                case "login":
                    return await commands.LoginAsync(command);
                case "logout":
                    return await commands.LogoutAsync(command);
                case "whoami":
                    return await commands.WhoAmIAsync(command);
                case "init":
                    return await commands.InitAsync(command);
                case "install-skill":
                    return commands.InstallSkill(command);
                case "list":
                    return await commands.ListAsync(command);
                case "next":
                    return await commands.NextAsync(command);
                case "wip":
                    return await commands.WipAsync(command);
                case "flow":
                    return await commands.FlowAsync(command);
                case "show":
                    return await commands.ShowAsync(command);
                case "new":
                    return await commands.NewAsync(command);
                case "edit":
                    return await commands.EditAsync(command);
                case "score":
                    return await commands.ScoreAsync(command);
                case "note":
                    return await commands.NoteAsync(command);
                case "start":
                    return await commands.StartAsync(command);
                case "block":
                    return await commands.BlockAsync(command);
                case "unblock":
                    return await commands.SetStateAsync(command, WorkState.InProgress);
                case "review":
                    return await commands.SetStateAsync(command, WorkState.InReview);
                case "archive":
                    return await commands.ArchiveAsync(command);
                case "restore":
                    return await commands.RestoreAsync(command);
                case "reindex":
                    return await commands.ReindexAsync(command);
                case "doctor":
                    return await commands.DoctorAsync(command);
                default:
                    throw new UsageException($"Unknown command '{command.Verb}'. Run 'backlog help'.");
            }
        }

        /// <summary>
        /// Passes this assembly so the client baked in at build time is found. Without it the
        /// tool would fall back to requiring a file on every user's machine.
        /// </summary>
        internal static OAuthClientSettings ResolveOAuthClient() =>
            OAuthClientSettings.Resolve(LocalConfig.OAuthClientPath, typeof(Program).Assembly);

        internal static UserCredentialStore CreateCredentialStore() =>
            new(ResolveOAuthClient(), LocalConfig.TokenDirectory);

        internal static async Task<ResolvedCredential> ResolveCredentialAsync(LocalConfig config, string? account = null)
        {
            var resolver = new GoogleCredentialResolver(CreateCredentialStore(), LocalConfig.ServiceAccountKeyPath);
            return await resolver.ResolveAsync(config.ResolveAccount(account), GoogleWorkspaceScopes.All);
        }

        /// <summary>
        /// One handler for both services: a quota belongs to the account, and the two clients
        /// spend the same one.
        /// </summary>
        internal static RateLimitRetryHandler CreateRetryHandler() => new(new ConsoleRetryListener());

        internal static async Task<IBacklogStore> CreateStoreAsync(LocalConfig config)
        {
            var credential = await ResolveCredentialAsync(config);
            var retry = CreateRetryHandler();

            return new BacklogStore(
                new SheetsGateway(new SheetsClientFactory(credential.Initializer, retry: retry)),
                new DriveGateway(new DriveClientFactory(credential.Initializer, retry: retry)),
                config.RequireSpreadsheetId());
        }

        static void Fail(CommandLine? command, string[] args, string kind, string message)
        {
            if (command?.Json ?? CommandLine.WantsJson(args))
            {
                Output.WriteJson(new Dictionary<string, string>
                {
                    ["kind"] = kind,
                    ["error"] = message
                });
            }
            else
            {
                Output.WriteError($"error ({kind}): {message}");
            }
        }

        static void WriteHelp()
        {
            Output.WriteLine("""
                backlog — a WSJF-prioritised Kanban backlog stored in Google Drive.

                Work moves Backlog -> In Progress -> Archive. The tab a ticket lives on is its
                state, so the verbs below are the transitions; there is no free-form status flag.
                Only unstarted work is WSJF-ranked.

                QUEUE
                  list [--area A] [--owner O] [--top N]   Unstarted work in rank order
                  next [--owner me]                       Highest-ranked item(s)
                  show <id> [--section S] [--full]        One ticket, with its body
                  flow [--since 90d]                      Throughput and cycle-time p50/p85

                  show trims the Activity Log to the last few entries; --full prints all of
                  them, and --section description (or acceptance-criteria, notes, activity-log)
                  prints just that one — which is what you want before rewriting it.

                WORK IN FLIGHT
                  wip [--owner O]                         In Progress, oldest first, with aging
                  start <id> [--owner me] [--force]       Pull an item (respects the WIP limit)
                  block <id> --reason "..."               Mark blocked
                  unblock <id>                            Back to in-progress
                  review <id>                             Complete, awaiting test/review

                CAPTURE AND EDIT
                  new --title "..." [--type feature] [--area A] [--owner O]
                      [--bv N --tc N --rroe N --size N]
                      [--description "..."] [--acceptance-criteria "..."]
                  edit <id> [--title ...] [--area ...] [--owner ...] [--type ...]
                       [--description "..."]             Replaces the Description section
                       [--acceptance-criteria "..."]     Replaces the Acceptance Criteria section
                       [--note "..."]                    Also log why, in the same write

                  Those two sections are the only prose the tool writes, and a ticket filed
                  without them says *TODO* until somebody fills them in — write the criteria as
                  a `- [ ] ...` checklist. Prose given inline goes through the shell, which on
                  Windows splits the value at an embedded double quote, so for anything longer
                  than a line use either of:
                    --description-file body.md           Read the section from a file
                    --description -                      Read it from standard input
                  Both spellings exist for --acceptance-criteria too, and only one option per
                  command may read standard input.
                  e.g.  Get-Content body.md -Raw | backlog new --title "..." --description -
                  score <id> [--bv N] [--tc N] [--rroe N] [--size N]
                  note <id> --text "..."                  Append to the Activity Log

                FINISHING
                  archive <id> --as done|cancelled|duplicate [--note "..."]
                  restore <id>                            Archive -> Backlog

                ACCOUNT
                  login [--account name]                  Sign in with your own Google account
                  logout [--account name]                 Revoke and delete the local token
                  whoami                                  Who you are and how you authenticated

                MAINTENANCE
                  init --drive <id> [--timezone America/New_York]   One-time setup (idempotent)
                  install-skill [--path DIR] [--force]    Write the Claude Code skill this tool
                                                          carries into ~/.claude/skills
                  doctor                                  Check the index for drift and duplicates
                  reindex                                 Rebuild rows from their documents

                Every command accepts --json for machine-readable output, which is always UTC.
                On list, next and wip, --fields id,wsjf,title narrows it to the columns you
                asked for. Human output uses the backlog's configured timezone; --utc shows UTC.
                WSJF scores are modified Fibonacci: 1, 2, 3, 5, 8, 13, 20. The score flags are
                also spelled out: --business-value, --time-criticality, --risk-opportunity,
                --job-size.
                """);
        }
    }
}

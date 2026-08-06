using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Cli
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var command = CommandLine.Parse(args);

            try
            {
                return await RunAsync(command);
            }
            catch (UsageException exception)
            {
                Fail(command, "usage", exception.Message);
                return 2;
            }
            catch (WipLimitExceededException exception)
            {
                Fail(command, "wip-limit", exception.Message);
                return 1;
            }
            catch (BacklogTransitionException exception)
            {
                Fail(command, "illegal-transition", exception.Message);
                return 1;
            }
            catch (KeyNotFoundException exception)
            {
                Fail(command, "not-found", exception.Message);
                return 1;
            }
            catch (ArgumentException exception)
            {
                Fail(command, "invalid-argument", exception.Message);
                return 1;
            }
            catch (FormatException exception)
            {
                Fail(command, "malformed", exception.Message);
                return 1;
            }
            catch (Exception exception)
            {
                Fail(command, "error", exception.Message);
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

            var config = LocalConfig.Load();
            var commands = new Commands(config);

            switch (command.Verb)
            {
                case "init":
                    return await commands.InitAsync(command);
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

        internal static IBacklogStore CreateStore(LocalConfig config) =>
            new BacklogStore(
                new SheetsGateway(new SheetsClientFactory()),
                new DriveGateway(new DriveClientFactory()),
                config.RequireSpreadsheetId());

        static void Fail(CommandLine command, string kind, string message)
        {
            if (command.Json)
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
                  show <id>                               One ticket, with its body
                  flow [--since 90d]                      Throughput and cycle-time p50/p85

                WORK IN FLIGHT
                  wip [--owner O]                         In Progress, oldest first, with aging
                  start <id> [--owner me] [--force]       Pull an item (respects the WIP limit)
                  block <id> --reason "..."               Mark blocked
                  unblock <id>                            Back to in-progress
                  review <id>                             Complete, awaiting test/review

                CAPTURE AND EDIT
                  new --title "..." [--type feature] [--area A] [--owner O]
                      [--bv N --tc N --rroe N --size N] [--description "..."]
                  edit <id> [--title ...] [--area ...] [--owner ...] [--type ...]
                  score <id> [--bv N] [--tc N] [--rroe N] [--size N]
                  note <id> --text "..."                  Append to the Activity Log

                FINISHING
                  archive <id> --as done|cancelled|duplicate [--note "..."]
                  restore <id>                            Archive -> Backlog

                MAINTENANCE
                  init --drive <sharedDriveId>            One-time setup (idempotent)
                  doctor                                  Check the index for drift and duplicates
                  reindex                                 Rebuild rows from their documents

                Every command accepts --json for machine-readable output.
                WSJF scores are modified Fibonacci: 1, 2, 3, 5, 8, 13, 20.
                """);
        }
    }
}

using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Cli
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // Parsing is inside the try because it can refuse the line itself — an option declared
            // to take a value and given none is a usage error rather than a flag nothing reads.
            // That leaves no CommandLine to ask about --json, which is why Fail also gets the raw
            // arguments.
            CommandLine? command = null;

            try
            {
                command = CommandLine.Parse(args);
                return await RunAsync(command);
            }
            catch (Exception exception)
            {
                var kind = BacklogFault.KindOf(exception);
                Fail(command, args, kind, BacklogFault.MessageOf(exception));

                return ExitCodeFor(kind);
            }
        }

        /// <summary>
        /// What a failure costs the caller's shell. The kind is the shared contract — the MCP
        /// server reports the same one — but an exit code is a thing only a process has, so the
        /// mapping lives here rather than beside <see cref="BacklogFault"/>.
        ///
        /// 2 is a command line nobody could have run. 3 is "you are not set up". 4 is Google
        /// pushing back, which is worth telling apart from a real failure because nothing was
        /// written and retrying later works. Everything else is 1.
        /// </summary>
        static int ExitCodeFor(string kind) => kind switch
        {
            BacklogFault.Usage => 2,
            BacklogFault.NotSignedIn => 3,
            BacklogFault.OAuthClientMissing => 3,
            BacklogFault.OAuthClientInvalid => 3,
            BacklogFault.RateLimited => 4,
            _ => 1
        };

        static async Task<int> RunAsync(CommandLine command)
        {
            // `help <verb>` answers about one verb rather than the whole surface. Reading all of it
            // to learn one thing is what makes a self-describing tool expensive.
            if (command.Verb is "help" or "h" or "?")
            {
                Output.WriteLine(command.Positionals.Count > 0
                    ? VerbHelp.Write(command.Positionals[0])
                    : VerbHelp.Write());

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
                case "find":
                    return await commands.FindAsync(command);
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

        internal static async Task<BacklogApi> CreateApiAsync(LocalConfig config) =>
            new(await CreateStoreAsync(config));

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
    }
}

using System.Reflection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Mcp
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var config = ServerConfig.FromEnvironment();

            BacklogTool tool;

            try
            {
                tool = await CreateToolAsync(config, StartupLoggers(builder));
            }
            catch (Exception exception)
            {
                // A server that cannot reach the backlog has nothing to serve, so it says why and
                // stops rather than accepting calls it will fail one at a time.
                Console.Error.WriteLine(
                    $"error ({BacklogFault.KindOf(exception)}): {BacklogFault.MessageOf(exception)}");

                return 1;
            }

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation
                    {
                        Name = "noogen-backlog",
                        Title = "Noogen backlog",
                        Version = Version
                    };

                    options.ServerInstructions = ServerGuidance.Instructions;
                })

                // 2026-07-28 removed protocol sessions, so there is no per-connection state to
                // keep and stateless is both the SDK's default and the only shape that matches
                // the specification. HTTP+SSE is deprecated; this is Streamable HTTP.
                .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
                .WithTools([BacklogTool.Describe(tool)])

                // By type rather than by scanning the assembly: there is one resource class, and
                // naming it says which one rather than trusting whatever a future file declares.
                .WithResources([typeof(BacklogGuide)])

                // From 2026-07-28 a cacheable result has to say how long it keeps, and the SDK's
                // default says "immediately stale" — which understates a tool list and a set of
                // guides that are the same for every caller and cannot change while this process
                // runs. See CacheHints for what the ten minutes is measuring.
                .WithRequestFilters(filters => filters
                    .AddListToolsFilter(CacheHints.On<ListToolsRequestParams, ListToolsResult>())
                    .AddListResourcesFilter(CacheHints.On<ListResourcesRequestParams, ListResourcesResult>())
                    .AddListResourceTemplatesFilter(
                        CacheHints.On<ListResourceTemplatesRequestParams, ListResourceTemplatesResult>())
                    .AddReadResourceFilter(CacheHints.On<ReadResourceRequestParams, ReadResourceResult>()));

            var app = builder.Build();

            app.MapMcp();

            await app.RunAsync();

            return 0;
        }

        static string Version =>
            typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "1.0.0";

        /// <summary>
        /// Not disposed on purpose. It exists so the retry listener has somewhere to write before
        /// the host's own logging is built, and the thing it is attached to — the HTTP handler
        /// under both Google clients — lives as long as the process does.
        /// </summary>
        static ILoggerFactory StartupLoggers(WebApplicationBuilder builder) =>
            LoggerFactory.Create(logging =>
            {
                logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
                logging.AddConsole();
            });

        /// <summary>
        /// Everything the server needs, resolved once, here.
        ///
        /// Credentials in particular: resolution can need I/O and, for a signed-in user, a token
        /// refresh, and doing that inside a request would put that cost — and its failures — on
        /// whichever call happened to be first. It also means a misconfigured server fails at
        /// startup, where somebody is watching, rather than on a tool call, where nobody is.
        /// </summary>
        static async Task<BacklogTool> CreateToolAsync(ServerConfig config, ILoggerFactory loggers)
        {
            var spreadsheetId = config.RequireSpreadsheetId();

            // Null where no token directory is configured, which is the ordinary case for a
            // deployed server: there is no signed-in person here, only a service account or the
            // platform's own identity.
            var users = config.TokenDirectory is null
                ? null
                : new UserCredentialStore(OAuthClientSettings.Resolve(config.OAuthClientPath), config.TokenDirectory);

            var resolver = new GoogleCredentialResolver(users, config.ServiceAccountKeyPath);
            var credential = await resolver.ResolveAsync(config.Account, GoogleWorkspaceScopes.All);

            // One handler for both services: a quota belongs to the account, and the two clients
            // spend the same one.
            var retry = new RateLimitRetryHandler(
                new LoggingRetryListener(loggers.CreateLogger<RateLimitRetryHandler>()));

            var store = new BacklogStore(
                new SheetsGateway(new SheetsClientFactory(credential.Initializer, retry: retry)),
                new DriveGateway(new DriveClientFactory(credential.Initializer, retry: retry)),
                spreadsheetId);

            var identity = new ServerIdentity(
                credential.Source, credential.Description, config.Owner, spreadsheetId);

            // Said here and nowhere else. Which credential was chosen and which backlog it opens is
            // what the person who deployed this needs to check, and it names a key file's path or
            // an operator's address — so it goes to the log rather than into `whoami`, where every
            // caller would read it.
            loggers.CreateLogger(typeof(Program).FullName!).LogInformation(
                "Serving backlog {SpreadsheetId} as {Credential}; writes are attributed to {Owner}.",
                spreadsheetId,
                credential.Description,
                identity.Owner ?? "nobody");

            return new BacklogTool(new BacklogApi(store), identity);
        }
    }
}

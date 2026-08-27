using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Noogen.Backlog.Verbs;

namespace Noogen.Backlog.Mcp
{
    /// <summary>
    /// One tool, carrying every verb.
    ///
    /// Not one tool per verb, and not by preference: a 2026-07-28 server's tool list may not vary
    /// per connection or as a consequence of an earlier call, so a surface cannot be unlocked after
    /// discovery. Whatever a caller is going to learn about this backlog, they learn it *inside* a
    /// tool result. Eighteen tool definitions would then be eighteen standing costs in every
    /// conversation that never touches the backlog at all — so there is one, it names the verbs, and
    /// everything below that is asked for: `help` for the surface, `help` with a verb for one of
    /// them, and a refusal that names what the verb actually reads at the moment it was got wrong.
    ///
    /// It is `verb` plus `options` rather than a command-line string because there is no shell on
    /// this path. Prose arrives as a JSON string with its newlines and quotes intact, which is the
    /// whole reason the CLI's `--name-file` and `--name -` spellings exist and the reason they are
    /// not offered here. Taking one string and splitting it again would rebuild, deliberately, the
    /// failure the CLI's positional cap exists to catch.
    ///
    /// Nothing here decides what an answer *is*. Every verb is one call into
    /// <see cref="BacklogApi"/>, and the two that are not — `help` and `whoami` — are questions
    /// about this server rather than about the backlog.
    /// </summary>
    public class BacklogTool
    {
        public const string ToolName = "backlog";

        const string VerbDescription =
            "Which operation to run. Call this tool with verb 'help' for what each one does.";

        const string OptionsDescription =
            "Everything the verb reads, by name — the keys its usage lists inside braces. Prose is "
            + "an ordinary string; newlines and quotes survive.";

        /// <summary>
        /// What the tool is, and the one thing its schema cannot say: how a verb's arguments are
        /// spelled, and where to find out which ones it takes.
        ///
        /// The first paragraph is a discovery surface rather than a summary — it is what a model
        /// weighs when somebody says "create a ticket", against every other tool it has. So it
        /// carries the words a person actually uses: ticket, create, score, start, block, finish,
        /// what to work on next. It is deliberately the same vocabulary as the skill's frontmatter
        /// `description`, which was tuned for this exact job and is what a caller *with* a skill
        /// matches on; a caller without one should not be worse off.
        ///
        /// The second paragraph says the ticket text is here, and nothing on this surface names
        /// the storage behind it. A caller told the tickets are Google Docs in a shared drive
        /// reasons that it has no Drive tool and therefore cannot read one — so it stops at the
        /// headline `show` returns and never looks at the document `show` already gave it.
        ///
        /// Everything else a caller needs before touching the backlog — the three columns, that the
        /// tab is the state, that `find` comes before `new` — is in the server's own instructions,
        /// which are loaded in every conversation whether the backlog is touched or not. Saying it
        /// twice would cost twice and drift once.
        /// </summary>
        const string ToolDescription =
            "The Noogen backlog: a WSJF-prioritized Kanban board of work tickets. "
            + "Create a ticket, score it, start, block or finish it; answer what to work on next, "
            + "what is in flight, and whether a ticket for something already exists.\n"
            + "\n"
            + "This tool holds the tickets themselves. 'show' returns a ticket's whole text — its "
            + "description, acceptance criteria, notes and activity log — and 'find' searches that "
            + "text. Reading or writing a ticket needs no other tool and no file access.\n"
            + "\n"
            + "'options' carries everything the verb reads, keyed by name: 'show {id, section?}' "
            + "is called as {\"verb\": \"show\", \"options\": {\"id\": \"NG-0007\"}}. Those key "
            + "names are not guessable, so call {\"verb\": \"help\", \"options\": {\"verb\": \"<name>\"}} "
            + "before a verb's first use, or {\"verb\": \"help\"} for the whole surface.";

        readonly BacklogApi _api;
        readonly ServerIdentity _identity;

        public BacklogTool(BacklogApi api, ServerIdentity identity)
        {
            _api = api;
            _identity = identity;
        }

        /// <summary>
        /// The tool as the server offers it: the verb list travels in the schema, so a client can
        /// refuse a typo without a round trip, and the list comes from the catalog rather than from
        /// a second copy here.
        /// </summary>
        public static McpServerTool Describe(BacklogTool tool) =>
            McpServerTool.Create(tool.InvokeAsync, new McpServerToolCreateOptions
            {
                Name = ToolName,
                Description = ToolDescription,

                // Archive moves a document; nothing here deletes one (invariant 11). The backlog is
                // a closed, well-defined world, and the same call twice is not the same call —
                // `note` appends twice.
                Destructive = false,
                OpenWorld = false,
                Idempotent = false,
                ReadOnly = false,

                SchemaCreateOptions = new AIJsonSchemaCreateOptions { TransformSchemaNode = ListTheVerbs }
            });

        /// <summary>
        /// Puts the offered verbs into the schema as an enum. The schema is generated one parameter
        /// at a time, so the parameter is recognised by the description it was declared with rather
        /// than by a position that is not there to read.
        /// </summary>
        static JsonNode ListTheVerbs(AIJsonSchemaCreateContext context, JsonNode schema)
        {
            if (context.TypeInfo.Type != typeof(string) || schema is not JsonObject node)
                return schema;

            if (!string.Equals((string?)node["description"], VerbDescription, StringComparison.Ordinal))
                return schema;

            node["enum"] = new JsonArray(
                [.. VerbCatalog.On(VerbSurface.Mcp).Select(verb => (JsonNode)JsonValue.Create(verb.Name))]);

            return node;
        }

        public async Task<CallToolResult> InvokeAsync(
            [Description(VerbDescription)] string verb,
            [Description(OptionsDescription)] JsonObject? options = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await RunAsync(verb, options, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Every refusal the backlog makes is an answer to a well-formed call, so it comes
                // back as a result rather than as a protocol error — and under the same one-word
                // kind the CLI turns into an exit code.
                return ToolResults.Failure(BacklogFault.KindOf(exception), BacklogFault.MessageOf(exception));
            }
        }

        async Task<CallToolResult> RunAsync(string verb, JsonObject? options, CancellationToken cancellationToken)
        {
            var definition = VerbCatalog.Find((verb ?? string.Empty).Trim())
                ?? throw new UsageException(Unknown(verb));

            if (!definition.OfferedOn(VerbSurface.Mcp))
                throw new UsageException($"'{definition.Name}' is not offered here. {definition.McpRefusal}");

            var given = VerbArguments.Read(definition, options);

            switch (definition.Name)
            {
                case "help":
                    return Help(given);

                case "whoami":
                    return ToolResults.Answer(_identity, null, _identity.Describe());

                case "list":
                {
                    var queue = await _api.ListAsync(Filter(given), cancellationToken);
                    return ToolResults.Answer(queue, Fields(given), Queued(queue));
                }

                case "next":
                {
                    var queue = await _api.NextAsync(Filter(given), cancellationToken);
                    return ToolResults.Answer(queue, Fields(given), Queued(queue));
                }

                case "wip":
                {
                    var wip = await _api.WipAsync(Filter(given), cancellationToken);
                    return ToolResults.Answer(wip, Fields(given), InFlight(wip));
                }

                case "find":
                {
                    var text = given.RequireSubject();
                    var matches = await _api.FindAsync(text, Filter(given), cancellationToken);

                    return ToolResults.Answer(matches, Fields(given), Matched(matches, text));
                }

                case "flow":
                {
                    var since = OptionValue.Since(given.Text("since"), DateTimeOffset.UtcNow, "'since'");
                    var flow = await _api.FlowAsync(since, cancellationToken);

                    return ToolResults.Answer(flow, null, Flowed(flow));
                }

                case "show":
                {
                    var detail = await _api.ShowAsync(
                        given.RequireSubject(), given.Text("section"), given.Flag("full"), cancellationToken);

                    return ToolResults.Answer(detail, null,
                        Headline(detail.Ticket) + " Its text is in this result, under 'body'.");
                }

                case "new":
                {
                    var filed = await _api.CreateAsync(new NewTicket
                    {
                        Title = given.Require("title"),
                        Type = Vocabulary.Parse<TicketType>(given.Text("type") ?? "feature", "type"),
                        Area = given.Text("area") ?? string.Empty,

                        // Only when asked. A ticket nobody has picked up has no owner, and
                        // stamping the server's identity on every one filed would make `list
                        // --owner` answer with the whole backlog.
                        Owner = given.Has("owner") ? _identity.ResolveOwner(given.Text("owner")) : null,
                        Description = given.Prose("description"),
                        AcceptanceCriteria = given.Prose("acceptance-criteria"),
                        Score = Score(given)
                    }, cancellationToken);

                    var said = $"Created {filed.Ticket.Id}.";

                    return ToolResults.Answer(filed, null,
                        filed.Reminder is null ? said : said + "\n\n" + filed.Reminder);
                }

                case "edit":
                {
                    var ticket = await _api.UpdateAsync(given.RequireSubject(), new TicketEdit
                    {
                        Title = given.Text("title"),
                        Area = given.Text("area"),
                        Owner = given.Has("owner") ? _identity.ResolveOwner(given.Text("owner")) : null,
                        Type = given.Has("type") ? Vocabulary.Parse<TicketType>(given.Require("type"), "type") : null,
                        Description = given.Prose("description"),
                        AcceptanceCriteria = given.Prose("acceptance-criteria"),
                        Note = given.Prose("note")
                    }, cancellationToken);

                    return ToolResults.Answer(ticket, null, $"Updated {ticket.Id}.");
                }

                case "score":
                {
                    var ticket = await _api.ScoreAsync(given.RequireSubject(), Score(given), cancellationToken);
                    return ToolResults.Answer(ticket, null, $"{ticket.Id} scored — wsjf {Number(ticket.Wsjf)}.");
                }

                case "note":
                {
                    var ticket = await _api.NoteAsync(
                        given.RequireSubject(), given.RequireProse("text"), cancellationToken);

                    return ToolResults.Answer(ticket, null, $"Noted on {ticket.Id}.");
                }

                case "start":
                {
                    var ticket = await _api.StartAsync(
                        given.RequireSubject(),
                        _identity.ResolveOwner(given.Text("owner")),
                        given.Flag("force"),
                        cancellationToken);

                    return ToolResults.Answer(ticket, null,
                        $"Started {ticket.Id} ({ticket.Owner}). It is no longer WSJF-ranked.");
                }

                case "block":
                {
                    var ticket = await _api.SetStateAsync(
                        given.RequireSubject(), WorkState.Blocked, given.RequireProse("reason"), cancellationToken);

                    return ToolResults.Answer(ticket, null, $"Blocked {ticket.Id}.");
                }

                case "unblock":
                    return await SetStateAsync(given, WorkState.InProgress, cancellationToken);

                case "review":
                    return await SetStateAsync(given, WorkState.InReview, cancellationToken);

                case "archive":
                {
                    var outcome = Vocabulary.Parse<Outcome>(given.Text("as") ?? "done", "outcome");

                    var ticket = await _api.ArchiveAsync(
                        given.RequireSubject(), outcome, given.Prose("note"), cancellationToken);

                    return ToolResults.Answer(ticket, null,
                        $"Archived {ticket.Id} as {Vocabulary.ToWire(outcome)} — lead {Number(ticket.LeadDays)}d, "
                        + $"cycle {Number(ticket.CycleDays)}d. The document was moved, not deleted.");
                }

                case "restore":
                {
                    var ticket = await _api.RestoreAsync(given.RequireSubject(), cancellationToken);

                    return ToolResults.Answer(ticket, null,
                        $"Restored {ticket.Id} to the backlog. Rescore it before it can rank.");
                }

                case "doctor":
                {
                    var report = await _api.DoctorAsync(cancellationToken);

                    return ToolResults.Answer(report, null, report.Healthy
                        ? $"{report.TicketCount} ticket(s), no issues."
                        : $"{report.TicketCount} ticket(s), {report.Issues.Count} issue(s).");
                }

                case "reindex":
                {
                    var reindexed = await _api.ReindexAsync(cancellationToken);

                    return ToolResults.Answer(reindexed, null,
                        $"Rewrote {reindexed.Repaired} row(s) from their documents.");
                }

                default:
                    // Reachable only by declaring a verb in the catalog and not wiring it here,
                    // which is a mistake in this file rather than in the call.
                    throw new UsageException(
                        $"'{definition.Name}' is declared but not implemented on this server.");
            }
        }

        async Task<CallToolResult> SetStateAsync(VerbArguments given, WorkState state, CancellationToken cancellationToken)
        {
            var ticket = await _api.SetStateAsync(given.RequireSubject(), state, null, cancellationToken);

            return ToolResults.Answer(ticket, null, $"{ticket.Id} is now {Vocabulary.ToWire(state)}.");
        }

        /// <summary>
        /// The three questions this tool answers about itself: the whole surface, one verb, or one
        /// of the guides. Handled here rather than in <see cref="BacklogApi"/> because none of them
        /// is a question about the backlog.
        /// </summary>
        CallToolResult Help(VerbArguments given)
        {
            var topic = given.Text("topic");
            var verb = given.Text("verb");

            if (topic is not null && verb is not null)
            {
                throw new UsageException(
                    "'help' answers one question at a time: pass 'verb' for what a verb takes, or "
                    + "'topic' for a guide, not both.");
            }

            if (topic is not null)
                return ToolResults.Prose(BacklogGuide.Read(topic));

            return ToolResults.Prose(verb is null
                ? VerbHelp.Write(VerbSurface.Mcp)
                : VerbHelp.Write(verb, VerbSurface.Mcp));
        }

        static string Unknown(string? verb) =>
            $"Unknown verb '{verb}'. This server offers: "
            + $"{string.Join(", ", VerbCatalog.On(VerbSurface.Mcp).Select(offered => offered.Name))}. "
            + "Call {\"verb\": \"help\"} for what each one does.";

        TicketFilter Filter(VerbArguments given) => new()
        {
            Area = given.Text("area"),
            Owner = given.Has("owner") ? _identity.ResolveOwner(given.Text("owner")) : null,
            Top = given.Number("top")
        };

        static IReadOnlySet<string>? Fields(VerbArguments given) => BacklogJson.ParseFields(given.Text("fields"));

        static WsjfScore Score(VerbArguments given) => new()
        {
            BusinessValue = given.Number("bv"),
            TimeCriticality = given.Number("tc"),
            RiskReductionOpportunityEnablement = given.Number("rroe"),
            JobSize = given.Number("size")
        };

        // --- what the text half says ---

        static string Queued(TicketListView queue) => queue.Tickets.Count == 0
            ? "Nothing is queued."
            : $"{queue.Tickets.Count} ticket(s), highest WSJF first.";

        static string InFlight(WipView wip)
        {
            var aging = wip.AgingThresholdDays > 0
                ? $"; aging past {Number(wip.AgingThresholdDays)}d, which is p85 cycle time"
                : string.Empty;

            return $"{wip.InFlight} of {wip.WipLimit} in flight{aging}.";
        }

        /// <summary>
        /// Says which half of the search answered, and — when neither did — why that is not proof
        /// the ticket does not exist. This is the moment somebody is about to file a duplicate.
        /// </summary>
        static string Matched(TicketListView matches, string text) => matches.Tickets.Count == 0
            ? $"Nothing matched '{text}'. Names match on any fragment, but document text matches "
              + "whole words only, and a document written in the last few minutes may not be indexed yet."
            : $"{matches.Tickets.Count} match(es). Each carries 'match': 'name' is the index, which is "
              + "current and matches fragments; 'body' is the document text index, which lags and matches whole words.";

        static string Flowed(FlowView view)
        {
            var flow = view.Metrics;

            return $"{flow.Throughput} done — cycle time p50 {Number(flow.CycleTimeP50)}d, p85 "
                + $"{Number(flow.CycleTimeP85)}d; lead time p50 {Number(flow.LeadTimeP50)}d, p85 "
                + $"{Number(flow.LeadTimeP85)}d.";
        }

        static string Headline(TicketView ticket) =>
            $"{ticket.Id} [{ticket.Phase}] {ticket.Title} — wsjf {Number(ticket.Wsjf)}.";

        static string Number(double? value) =>
            value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : "-";
    }
}

using System.Text;

namespace Noogen.Backlog.Verbs
{
    /// <summary>
    /// Which front ends offer a verb or an option.
    ///
    /// <see cref="Every"/> rather than a name that counts them: a REST front end would be a third
    /// flag, and every declaration site that said "both" would then be saying something false. As
    /// it is, adding one extends this enum and nothing else.
    /// </summary>
    [Flags]
    public enum VerbSurface
    {
        Cli = 1,
        Mcp = 2,
        Every = Cli | Mcp
    }

    /// <summary>
    /// One option a verb reads, and what it is for.
    ///
    /// The description is here rather than in a help string because two front ends have to answer
    /// "what does this verb accept?" and a second copy of the answer is a copy that goes stale.
    /// </summary>
    public class VerbOption
    {
        public VerbOption(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }

        public string Description { get; }

        /// <summary>A second spelling of the same option. The score flags have one; nothing else does.</summary>
        public string? Alias { get; init; }

        /// <summary>False for a valueless flag. Shape is a property of the name, not of the verb.</summary>
        public bool TakesValue { get; init; } = true;

        /// <summary>
        /// Free text a person wrote. On the command line that earns two extra spellings
        /// (<c>--name-file</c> and <c>--name -</c>) because a shell damages a quoted value; over
        /// MCP it is an ordinary JSON string and needs neither.
        /// </summary>
        public bool IsProse { get; init; }

        /// <summary>Whether the verb refuses to run without it. Used to write usage, not to enforce.</summary>
        public bool Required { get; init; }

        public VerbSurface Surfaces { get; init; } = VerbSurface.Every;

        /// <summary>The spelling that reads the value from a file, derived rather than written out.</summary>
        public string FileName => Name + "-file";

        /// <summary>Every spelling this option answers to on the command line.</summary>
        public IEnumerable<string> CommandLineNames()
        {
            yield return Name;

            if (Alias is not null)
                yield return Alias;

            if (IsProse)
                yield return FileName;
        }
    }

    /// <summary>One verb: what it does, what it takes, and which front ends offer it.</summary>
    public class VerbDefinition
    {
        public VerbDefinition(string name, string summary)
        {
            Name = name;
            Summary = summary;
        }

        public string Name { get; }

        /// <summary>One line, in the imperative. What a caller reads before choosing this verb.</summary>
        public string Summary { get; }

        /// <summary>What the single positional argument is, or null for a verb that takes none.</summary>
        public string? Positional { get; init; }

        /// <summary>
        /// Whether the verb refuses to run without it. False on <c>help</c>, which answers about
        /// the whole surface when it is given nothing — and usage has to say so, because a reader
        /// who cannot tell will not discover the cheaper question.
        /// </summary>
        public bool PositionalRequired { get; init; } = true;

        /// <summary>
        /// The one word usage calls that argument. Named rather than cut out of the description,
        /// because "some text to search for" ends in the wrong word and `find &lt;for&gt;` is worse
        /// than no usage line at all.
        /// </summary>
        public string PositionalName { get; init; } = "id";

        public IReadOnlyList<VerbOption> Options { get; init; } = [];

        public VerbSurface Surfaces { get; init; } = VerbSurface.Every;

        /// <summary>Why the MCP server does not offer this verb. Set on exactly the CLI-only ones.</summary>
        public string? McpRefusal { get; init; }

        public string Group { get; init; } = string.Empty;

        public bool OfferedOn(VerbSurface surface) => Surfaces.HasFlag(surface);

        public IEnumerable<VerbOption> OptionsOn(VerbSurface surface) =>
            Options.Where(option => option.Surfaces.HasFlag(surface));

        public VerbOption? Option(string name) =>
            Options.FirstOrDefault(option =>
                option.CommandLineNames().Contains(name, StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Usage as the pieces it is made of: the verb and its positional first, then one entry
        /// per option, required ones before optional ones.
        ///
        /// Pieces rather than a string because a piece is where a line may break. `new` takes ten
        /// options and reads as two hundred characters on one line; splitting that on spaces would
        /// tear `[--type <value>]` in half.
        /// </summary>
        public IReadOnlyList<string> UsageParts(VerbSurface surface)
        {
            if (surface == VerbSurface.Mcp)
                return CallParts();

            var head = new StringBuilder(Name);

            if (Positional is not null)
            {
                head.Append(PositionalRequired ? " <" : " [<")
                    .Append(PositionalName)
                    .Append(PositionalRequired ? ">" : ">]");
            }

            var parts = new List<string> { head.ToString() };

            var dash = VerbCatalog.OptionPrefix(surface);

            foreach (var option in OptionsOn(surface).OrderByDescending(option => option.Required))
            {
                var value = option.TakesValue ? " <value>" : string.Empty;

                parts.Add(option.Required
                    ? $"{dash}{option.Name}{value}"
                    : $"[{dash}{option.Name}{value}]");
            }

            return parts;
        }

        /// <summary>
        /// The call as the shape it actually is: `show {id, section?, full?}`.
        ///
        /// There is no argument position over MCP and no dash to prefix — every name is a key of
        /// `options` — so the command line's rendering says two things that are not true there. Two
        /// dashes teach a spelling this surface refuses, and bare names in a row read as positional
        /// arguments: `block id reason` looks like three of them. Braces say "one object", and a
        /// trailing `?` says the verb runs without that key.
        ///
        /// Deliberately not JSON Schema. This is the only description of what a verb takes — the
        /// tool's own input schema stops at `verb` and a free-form `options` — so it is read on
        /// demand by a model paying for it, and a schema would cost several times this to say the
        /// same thing.
        ///
        /// One piece per key, because a piece is where a line may break: `new` takes ten.
        /// </summary>
        IReadOnlyList<string> CallParts()
        {
            var keys = new List<string>();

            if (Positional is not null)
                keys.Add(PositionalName + (PositionalRequired ? string.Empty : "?"));

            foreach (var option in OptionsOn(VerbSurface.Mcp))
                keys.Add(option.Name + (option.Required ? string.Empty : "?"));

            // A verb that reads nothing is its own call. `whoami {}` would be inviting an argument.
            if (keys.Count == 0)
                return [Name];

            var parts = new List<string> { Name };

            for (var i = 0; i < keys.Count; i++)
            {
                parts.Add((i == 0 ? "{" : string.Empty)
                    + keys[i]
                    + (i == keys.Count - 1 ? "}" : ","));
            }

            return parts;
        }

        /// <summary>Usage on one line, for somewhere with no width to respect.</summary>
        public string Usage(VerbSurface surface) => string.Join(' ', UsageParts(surface));
    }

    /// <summary>A heading in the help, with the prose that belongs under it.</summary>
    public class VerbGroup
    {
        public VerbGroup(string title, string? note = null)
        {
            Title = title;
            Note = note;
        }

        public string Title { get; }

        /// <summary>What a caller needs to know about this group that no single verb's summary says.</summary>
        public string? Note { get; }
    }

    /// <summary>
    /// Every verb, every option each one reads, and what each is for.
    ///
    /// This is the answer to "what does this verb accept?", and it is one table because there is
    /// more than one thing that has to ask. The command-line parser reads it to decide whether a
    /// name takes a value; <see cref="Cli"/> validation reads it to refuse a name nobody declared;
    /// the help is written from it; and the MCP server both validates against it and hands it back
    /// to a caller that asked how to use a verb.
    ///
    /// Every one of those used to be written out separately. The cost was not duplication, it was
    /// silence: <c>edit --description</c> parsed cleanly, matched nothing, and still printed
    /// "Updated NG-12." — a truthful message about a no-op. A typo did the same. Declaring the
    /// surface once makes an unrecognised option an error instead, and makes the help incapable of
    /// describing a flag the code does not read.
    ///
    /// Adding an option to a verb means adding it here. That is the point.
    /// </summary>
    public static class VerbCatalog
    {
        const string Queue = "QUEUE";
        const string InFlight = "WORK IN FLIGHT";
        const string Capture = "CAPTURE AND EDIT";
        const string Finishing = "FINISHING";
        const string Account = "ACCOUNT";
        const string Maintenance = "MAINTENANCE";
        const string Learning = "LEARNING THE SURFACE";

        /// <summary>What every verb that acts on one ticket calls its positional argument.</summary>
        const string TicketId = "a ticket id";

        const string LifecycleGuidance =
            "There is no --status flag: the tab a ticket lives on is its state. " +
            "Use 'backlog start', 'block', 'unblock', 'review', 'archive', or 'restore'.";

        /// <summary>
        /// Honoured on every verb, and CLI-only. Over MCP a result is always structured and always
        /// UTC — invariant 13 says the machine contract does not move with a display setting, and
        /// there is no terminal on that path to render for.
        /// </summary>
        public static IReadOnlyList<VerbOption> Modifiers { get; } =
        [
            new VerbOption("json", "Emit the machine contract instead of a table. Always UTC.")
                { TakesValue = false, Surfaces = VerbSurface.Cli },
            new VerbOption("utc", "Render times as UTC rather than the backlog's timezone.")
                { TakesValue = false, Surfaces = VerbSurface.Cli }
        ];

        static VerbOption Area => new("area", "Only tickets in this area.");

        static VerbOption Owner => new("owner", "Only tickets with this owner. 'me' resolves to the configured owner.");

        static VerbOption Top => new("top", "At most this many tickets. The cheapest way to read less.");

        static VerbOption Fields => new("fields", "Narrow each ticket to these keys, comma-separated: " + BacklogJson.KnownFields + ".");

        // The same two names mean different things on a query and on a write: one picks which
        // tickets come back, the other sets a value on the one in hand. Reusing the filter's
        // wording made `new --area` read as though it searched.
        static VerbOption AreaOf => new("area", "Which part of the system this belongs to.");

        static VerbOption OwnerOf => new("owner", "Who owns it. 'me' resolves to the configured owner.");

        static VerbOption[] Filter => [Area, Owner, Top, Fields];

        static VerbOption[] Score =>
        [
            new VerbOption("bv", "Business value, modified Fibonacci: 1, 2, 3, 5, 8, 13, 20.") { Alias = "business-value" },
            new VerbOption("tc", "Time criticality, on the same scale.") { Alias = "time-criticality" },
            new VerbOption("rroe", "Risk reduction and opportunity enablement, on the same scale.") { Alias = "risk-opportunity" },
            new VerbOption("size", "Job size, on the same scale. WSJF divides by it.") { Alias = "job-size" }
        ];

        static VerbOption Description => new("description",
            "Replaces the whole Description section. Read it first; headings inside it start at ###.")
        { IsProse = true };

        static VerbOption AcceptanceCriteria => new("acceptance-criteria",
            "Replaces the whole Acceptance Criteria section. A checklist, one observable condition per line.")
        { IsProse = true };

        public static IReadOnlyList<VerbGroup> Groups { get; } =
        [
            new VerbGroup(Queue,
                "'show' trims the Activity Log to the last few entries; 'full' gives all of them, and "
                + "'section description' (or acceptance-criteria, notes, activity-log) gives just that "
                + "one — which is what you want before rewriting it.\n\n"
                + "'find' reads two sources and says which one hit: names — id, title, area, owner — "
                + "come from the index and match on any fragment, "
                + "while document prose comes from a full-text index, which matches whole words only, "
                + "covers the whole document including the Activity Log, and may not yet know about a "
                + "document written minutes ago."),

            new VerbGroup(InFlight),

            new VerbGroup(Capture,
                "Description and Acceptance Criteria are the only prose sections this tool writes, and a "
                + "ticket filed without them says *TODO* until somebody fills them in — write the criteria "
                + "as a '- [ ] ...' checklist."),

            new VerbGroup(Finishing),
            new VerbGroup(Account),
            new VerbGroup(Maintenance),
            new VerbGroup(Learning)
        ];

        /// <summary>
        /// The guidance a text-driven front end can hand back on demand — the skill's own files,
        /// named as a caller asks for them.
        ///
        /// It is a list of topics rather than of file names because a topic is what somebody asks
        /// for; which file carries it is the server's business. `overview` is the skill itself.
        /// </summary>
        public static IReadOnlyList<string> Guides { get; } = ["overview", "writing-style", "wsjf", "prose-input"];

        static string GuideTopics => string.Join(", ", Guides);

        public static IReadOnlyList<VerbDefinition> All { get; } =
        [
            new VerbDefinition("list", "Unstarted work in WSJF rank order.")
                { Group = Queue, Options = [.. Filter] },

            new VerbDefinition("next", "The highest-ranked item, and the answer to \"what should I work on?\".")
                { Group = Queue, Options = [.. Filter] },

            new VerbDefinition("find", "Search every tab, matching names in the index and prose in the documents.")
                { Group = Queue, Positional = "some text to search for", PositionalName = "text", Options = [Area, Owner, Top, Fields] },

            new VerbDefinition("show", "One ticket and the full text of its document.")
            {
                Group = Queue,
                Positional = TicketId,
                Options =
                [
                    new VerbOption("section", "Return only this heading — description, acceptance-criteria, notes, activity-log, or any the document has."),
                    new VerbOption("full", "Return the whole Activity Log rather than the last few entries.") { TakesValue = false }
                ]
            },

            new VerbDefinition("flow", "Throughput and cycle-time percentiles.")
            {
                Group = Queue,
                Options = [new VerbOption("since", "Only work archived since this far back: 90d, 12w, 6m.")]
            },

            new VerbDefinition("wip", "Work in flight, oldest first, flagging what has aged past p85 cycle time.")
                { Group = InFlight, Options = [.. Filter] },

            new VerbDefinition("start", "Pull an item into In Progress. Refuses to breach the WIP limit.")
            {
                Group = InFlight,
                Positional = TicketId,
                Options =
                [
                    new VerbOption("owner", "Who is pulling it. Defaults to the configured owner."),
                    new VerbOption("force", "Start it anyway, past the WIP limit. Only when a person asks after being told.") { TakesValue = false }
                ]
            },

            new VerbDefinition("block", "Mark work blocked, and say what is blocking it.")
            {
                Group = InFlight,
                Positional = TicketId,
                Options =
                [
                    new VerbOption("reason", "What is blocking it and what would unblock it. Lands in the Activity Log.")
                        { IsProse = true, Required = true }
                ]
            },

            new VerbDefinition("unblock", "Back to in-progress.")
                { Group = InFlight, Positional = TicketId },

            new VerbDefinition("review", "Complete, awaiting test or review.")
                { Group = InFlight, Positional = TicketId },

            new VerbDefinition("new", "File a ticket. Run 'find' first — this will happily file the one that already exists.")
            {
                Group = Capture,
                Options =
                [
                    new VerbOption("title", "A short factual clause naming the component and the change.") { Required = true },
                    new VerbOption("type", "feature, bug, chore or spike. Defaults to feature."),
                    AreaOf,
                    OwnerOf,
                    Description,
                    AcceptanceCriteria,
                    .. Score
                ]
            },

            new VerbDefinition("edit", "Correct a ticket's fields or rewrite one of its two prose sections.")
            {
                Group = Capture,
                Positional = TicketId,
                Options =
                [
                    new VerbOption("title", "A short factual clause naming the component and the change."),
                    AreaOf,
                    OwnerOf,
                    new VerbOption("type", "feature, bug, chore or spike."),
                    new VerbOption("note", "Also record why, in the Activity Log, in the same write.") { IsProse = true },
                    Description,
                    AcceptanceCriteria
                ]
            },

            new VerbDefinition("score", "Set WSJF scores. Refused once work has started — those numbers are history.")
                { Group = Capture, Positional = TicketId, Options = [.. Score] },

            new VerbDefinition("note", "Append one line to the Activity Log.")
            {
                Group = Capture,
                Positional = TicketId,
                Options =
                [
                    new VerbOption("text", "What happened, and what it means for the ticket. One or two past-tense sentences.")
                        { IsProse = true, Required = true }
                ]
            },

            new VerbDefinition("archive", "Finish a ticket. The document is moved, never deleted.")
            {
                Group = Finishing,
                Positional = TicketId,
                Options =
                [
                    new VerbOption("as", "done, cancelled or duplicate. Defaults to done."),
                    new VerbOption("note", "Why it ended this way. Lands in the Activity Log.") { IsProse = true }
                ]
            },

            new VerbDefinition("restore", "Return an archived ticket to the backlog. Rescore it before it can rank.")
                { Group = Finishing, Positional = TicketId },

            new VerbDefinition("login", "Sign in with your own Google account. Opens a browser once.")
            {
                Group = Account,
                Surfaces = VerbSurface.Cli,
                McpRefusal = "Signing in opens a browser on the machine running the server, which is not yours. "
                    + "Sign in where you run the 'backlog' tool, or ask whoever hosts this server which identity it acts as.",
                Options = [new VerbOption("account", "Which stored account to use. Defaults to the configured one.")]
            },

            new VerbDefinition("logout", "Revoke the token with Google and delete the local copy.")
            {
                Group = Account,
                Surfaces = VerbSurface.Cli,
                McpRefusal = "There is no credential of yours on this server to revoke.",
                Options = [new VerbOption("account", "Which stored account to sign out.")]
            },

            new VerbDefinition("whoami", "Who the backlog is reached as, and whose name a write lands under.")
                { Group = Account },

            new VerbDefinition("init", "One-time setup of the index and folders. Idempotent.")
            {
                Group = Maintenance,
                Surfaces = VerbSurface.Cli,
                McpRefusal = "Which backlog this server serves is its configuration, not a tool call.",
                Options =
                [
                    new VerbOption("drive", "The shared drive id to build the backlog in."),
                    new VerbOption("timezone", "IANA name, e.g. America/New_York. Seeds from this machine on first run.")
                ]
            },

            new VerbDefinition("install-skill", "Write the Claude Code skill this tool carries into ~/.claude/skills.")
            {
                Group = Maintenance,
                Surfaces = VerbSurface.Cli,
                McpRefusal = "The server cannot reach your ~/.claude. The same guidance is served as resources.",
                Options =
                [
                    new VerbOption("path", "Install under this directory instead of ~/.claude/skills."),
                    new VerbOption("force", "Replace an existing copy that differs, and remove files this version does not carry.")
                        { TakesValue = false }
                ]
            },

            new VerbDefinition("doctor", "Check the index for drift, duplicates and damaged documents.")
                { Group = Maintenance },

            new VerbDefinition("reindex", "Rebuild index rows from their documents.")
                { Group = Maintenance },

            // Declared like anything else rather than special-cased in each front end, so that
            // asking about the surface is itself part of the surface — and so that a verb or an
            // option added below cannot be missing from the answer.
            new VerbDefinition("help", "The verbs this surface offers, or one of them in detail.")
            {
                Group = Learning,
                Positional = "a verb to explain; omit it for the whole surface",
                PositionalName = "verb",
                PositionalRequired = false,
                Options =
                [
                    // Only where there is nothing to write it to: the CLI installs the same
                    // guidance as a skill, and a caller reaching this server has no ~/.claude.
                    new VerbOption("topic", "A guide to read whole: " + GuideTopics + ".")
                        { Surfaces = VerbSurface.Mcp }
                ]
            }
        ];


        static readonly Dictionary<string, VerbDefinition> ByName =
            All.ToDictionary(verb => verb.Name, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Options refused on purpose, answered with the thing to do instead. The generic message
        /// lists what a verb accepts, which does not answer "then what do I use?" — and that
        /// question is why the flag was reached for in the first place.
        /// </summary>
        static readonly Dictionary<string, string> Guidance = new(StringComparer.OrdinalIgnoreCase)
        {
            ["edit:status"] = LifecycleGuidance,
            ["edit:phase"] = LifecycleGuidance,
            ["new:status"] = LifecycleGuidance,
            ["new:phase"] = LifecycleGuidance
        };

        /// <summary>
        /// How a surface spells an option's name. A command line prefixes two dashes; a JSON object
        /// has nothing to prefix, and showing dashes there would be teaching a spelling that is
        /// refused. Said once, because usage and the option list are written in two places and a
        /// caller reads them as one answer.
        /// </summary>
        public static string OptionPrefix(VerbSurface surface) => surface == VerbSurface.Cli ? "--" : string.Empty;

        public static VerbDefinition? Find(string verb) =>
            ByName.TryGetValue(verb, out var definition) ? definition : null;

        public static VerbDefinition Require(string verb) =>
            Find(verb) ?? throw new UsageException($"Unknown command '{verb}'. Run 'backlog help'.");

        public static IEnumerable<VerbDefinition> On(VerbSurface surface) =>
            All.Where(verb => verb.OfferedOn(surface));

        public static string? GuidanceFor(string verb, string option) =>
            Guidance.TryGetValue($"{verb}:{option}", out var hint) ? hint : null;

        /// <summary>Every option spelling <paramref name="verb"/> reads on the command line, in order.</summary>
        public static IReadOnlyList<string> CommandLineNames(string verb)
        {
            var definition = Find(verb);

            return definition is null
                ? []
                : [.. definition.OptionsOn(VerbSurface.Cli).SelectMany(option => option.CommandLineNames())];
        }
    }
}

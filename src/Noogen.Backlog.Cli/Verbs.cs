namespace Noogen.Backlog.Cli
{
    /// <summary>
    /// The options each verb actually reads.
    ///
    /// <see cref="CommandLine"/> collects any <c>--name</c> it is handed, so an option that no
    /// command reads is invisible. <c>edit --description</c> parsed cleanly, matched nothing, and
    /// the edit still printed "Updated NG-12." — a truthful message about a no-op that reads as a
    /// confirmation. A typo (<c>--titel</c>, <c>--aera</c>) did the same. Declaring the surface
    /// makes an unrecognised option a usage error instead.
    ///
    /// It is declared here, in one table, rather than at the call sites: a call site can only see
    /// the options it asks for, and the bug is an option nobody asks for. Adding a flag to a
    /// command means adding it here too, which is the point — the table is the answer to "what
    /// does this verb accept?", and the help text is written from the same knowledge.
    ///
    /// Positional arguments have the same hole and a worse failure. A verb reads
    /// <c>Positionals[0]</c> and nothing looked at the rest, so a description that the shell had
    /// torn into fragments arrived as extra positionals, was dropped, and the command exited 0
    /// with a truncated ticket. <see cref="Validate"/> now rejects the ones no verb reads.
    ///
    /// The table declares each name's <em>shape</em> as well, and <see cref="CommandLine"/> parses
    /// from that rather than guessing. See <see cref="Valueless"/>.
    /// </summary>
    public static class Verbs
    {
        /// <summary>
        /// Honoured everywhere, and documented that way: `--json` is the machine contract every
        /// command supports, and `--utc` is its human-output counterpart.
        /// </summary>
        static readonly string[] Modifiers = ["json", "utc"];

        /// <summary>
        /// Both spellings of every WSJF flag — see <c>CommandLine.IntOption(name, alias)</c> for
        /// why there are two.
        /// </summary>
        static readonly string[] ScoreFlags =
        [
            "bv", "business-value",
            "tc", "time-criticality",
            "rroe", "risk-opportunity",
            "size", "job-size"
        ];

        /// <summary>What <c>Commands.BuildFilter</c> reads, for the three verbs that call it.</summary>
        static readonly string[] FilterFlags = ["area", "owner", "top"];

        /// <summary>
        /// The prose sections the CLI authors, each with the two spellings that never reach the
        /// command line. Both verbs that write a document take all four — a ticket is filed and
        /// corrected through the same surface, and acceptance criteria left to Docs alone were the
        /// ones that never got written.
        /// </summary>
        static readonly string[] ProseFlags =
        [
            "description", "description-file",
            "acceptance-criteria", "acceptance-criteria-file"
        ];

        static readonly Dictionary<string, string[]> Accepted = new(StringComparer.OrdinalIgnoreCase)
        {
            ["login"] = ["account"],
            ["logout"] = ["account"],
            ["whoami"] = [],
            ["init"] = ["drive", "timezone"],
            ["install-skill"] = ["path", "force"],
            ["list"] = FilterFlags,
            ["next"] = FilterFlags,
            ["wip"] = FilterFlags,
            ["flow"] = ["since"],
            ["show"] = [],
            ["new"] = ["title", "type", "area", "owner", .. ProseFlags, .. ScoreFlags],
            ["edit"] = ["title", "area", "owner", "type", .. ProseFlags],
            ["score"] = [.. ScoreFlags],
            ["note"] = ["text"],
            ["start"] = ["owner", "force"],
            ["block"] = ["reason"],
            ["unblock"] = [],
            ["review"] = [],
            ["archive"] = ["as", "note"],
            ["restore"] = [],
            ["reindex"] = [],
            ["doctor"] = []
        };

        /// <summary>
        /// The names that carry no value. Everything else a verb accepts takes one.
        ///
        /// Without this the parser had to guess from the next argument, and it guessed wrong three
        /// ways. <c>doctor --json extra</c> bound <c>extra</c> to <c>--json</c>, so the flag went
        /// unset, human output was printed under the machine contract, and the stray positional
        /// NG-0045 exists to refuse was never seen as one. A value beginning with two dashes could
        /// not be passed at all. And <c>edit NG-12 --title</c> became a valueless flag named
        /// <c>title</c>, which reads as "leave the title alone" — the same silent no-op this table
        /// was written to end.
        ///
        /// Shape is a property of the name, not of the verb: <c>--force</c> means the same thing on
        /// <c>start</c> and on <c>install-skill</c>, and a name that took a value on one verb and
        /// none on another would be a trap for anyone reading a command line. So it is one set
        /// beside <see cref="Accepted"/> rather than a second spelling of every entry in it.
        /// </summary>
        static readonly HashSet<string> Valueless = new(StringComparer.OrdinalIgnoreCase)
        {
            "json", "utc", "force"
        };

        /// <summary>
        /// The verbs that take a ticket id. It is the only positional argument in the surface, so
        /// this is the whole arity table: every other verb takes none.
        /// </summary>
        static readonly HashSet<string> TakesTicketId = new(StringComparer.OrdinalIgnoreCase)
        {
            "show", "edit", "score", "note", "start", "block", "unblock", "review", "archive", "restore"
        };

        const string Lifecycle =
            "There is no --status flag: the tab a ticket lives on is its state. " +
            "Use 'backlog start', 'block', 'unblock', 'review', 'archive', or 'restore'.";

        /// <summary>
        /// Options refused on purpose, answered with the thing to do instead. The generic message
        /// lists what a verb accepts, which does not answer "then what do I use?" — and that
        /// question is why the flag was reached for in the first place.
        /// </summary>
        static readonly Dictionary<string, string> Guidance = new(StringComparer.OrdinalIgnoreCase)
        {
            ["edit:status"] = Lifecycle,
            ["edit:phase"] = Lifecycle,
            ["new:status"] = Lifecycle,
            ["new:phase"] = Lifecycle
        };

        /// <summary>
        /// Whether <paramref name="verb"/> reads an option called <paramref name="name"/> at all.
        /// False for a name nobody declared, which is what makes an unknown option inert at parse
        /// time: it consumes nothing, so the argument behind it is still there for
        /// <see cref="Validate"/> to see and report.
        /// </summary>
        public static bool Accepts(string verb, string name) =>
            Modifiers.Contains(name, StringComparer.OrdinalIgnoreCase)
            || (Accepted.TryGetValue(verb, out var accepted)
                && accepted.Contains(name, StringComparer.OrdinalIgnoreCase));

        /// <summary>Whether <c>--name</c> is followed by its value on this verb.</summary>
        public static bool TakesValue(string verb, string name) =>
            Accepts(verb, name) && !Valueless.Contains(name);

        /// <summary>Whether <c>--name</c> is one of the valueless flags this verb reads.</summary>
        public static bool IsFlag(string verb, string name) =>
            Accepts(verb, name) && Valueless.Contains(name);

        /// <summary>
        /// Rejects anything this verb does not read, in the order it was typed. Called before the
        /// verb runs, so a mistyped option costs nothing — no credential resolved, no browser
        /// opened, no half-understood write.
        /// </summary>
        public static void Validate(CommandLine command)
        {
            // An unknown verb is reported here rather than by the dispatch switch, because there
            // is no accepted list to check its options against. Same message, same exit code.
            if (!Accepted.TryGetValue(command.Verb, out var accepted))
                throw new UsageException($"Unknown command '{command.Verb}'. Run 'backlog help'.");

            var known = new HashSet<string>(accepted, StringComparer.OrdinalIgnoreCase);
            known.UnionWith(Modifiers);

            foreach (var name in command.Names)
            {
                if (known.Contains(name))
                    continue;

                var detail = Guidance.TryGetValue($"{command.Verb}:{name}", out var hint)
                    ? hint
                    : $"It accepts: {string.Join(", ", accepted.Concat(Modifiers).Select(option => "--" + option))}.";

                throw new UsageException($"'{command.Verb}' does not accept --{name}. {detail}");
            }

            var allowed = TakesTicketId.Contains(command.Verb) ? 1 : 0;

            if (command.Positionals.Count > allowed)
                throw new UsageException(Unexpected(command, accepted, command.Positionals[allowed]));
        }

        /// <summary>
        /// Names the argument, then names the reason it is almost always there. A bare "unexpected
        /// argument" is loud enough to stop the corruption, but the fragment it names looks like
        /// nonsense until you know a quote in the middle of a value is what produced it — and the
        /// person reading this has just had a description silently truncated.
        /// </summary>
        static string Unexpected(CommandLine command, string[] accepted, string extra)
        {
            var shape = TakesTicketId.Contains(command.Verb)
                ? "takes a ticket id and nothing else positional"
                : "takes no positional arguments";

            var advice = accepted.Contains("description", StringComparer.OrdinalIgnoreCase)
                ? " Prose is safest given as --description-file <path>, or --description - to read it from stdin; "
                    + "neither goes through the command line."
                : string.Empty;

            return $"'{command.Verb}' {shape}, and got '{extra}'. "
                + "If that is a fragment of something you passed, the shell split the value: a double quote "
                + "inside an argument is not escaped by PowerShell, so everything after it arrives as separate "
                + "arguments."
                + advice;
        }
    }
}

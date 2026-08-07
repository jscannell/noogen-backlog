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
            ["new"] = ["title", "type", "area", "owner", "description", .. ScoreFlags],
            ["edit"] = ["title", "area", "owner", "type", "description"],
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
        }
    }
}

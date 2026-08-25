namespace Noogen.Backlog.Cli
{
    /// <summary>
    /// What a command line may say, checked against <see cref="VerbCatalog"/>.
    ///
    /// The catalog holds the surface; this holds the rules that are about a shell. An option that
    /// no verb reads is a usage error rather than a silent no-op — <c>edit --description</c> once
    /// parsed cleanly, matched nothing, and still printed "Updated NG-12." A typo did the same.
    ///
    /// Positional arguments have the same hole and a worse failure. A verb reads
    /// <c>Positionals[0]</c> and nothing looked at the rest, so a description that the shell had
    /// torn into fragments arrived as extra positionals, was dropped, and the command exited 0 with
    /// a truncated ticket. <see cref="Validate"/> rejects the ones no verb reads.
    /// </summary>
    public static class Verbs
    {
        /// <summary>What <paramref name="verb"/>'s positional argument is, or null if it takes none.</summary>
        public static string? PositionalOf(string verb) => VerbCatalog.Find(verb)?.Positional;

        /// <summary>
        /// Whether <paramref name="verb"/> reads an option called <paramref name="name"/> at all.
        /// False for a name nobody declared, which is what makes an unknown option inert at parse
        /// time: it consumes nothing, so the argument behind it is still there for
        /// <see cref="Validate"/> to see and report.
        /// </summary>
        public static bool Accepts(string verb, string name) =>
            Modifier(name) is not null
            || VerbCatalog.CommandLineNames(verb).Contains(name, StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether <c>--name</c> is followed by its value on this verb.</summary>
        public static bool TakesValue(string verb, string name) =>
            Accepts(verb, name) && (Shape(verb, name)?.TakesValue ?? true);

        /// <summary>Whether <c>--name</c> is one of the valueless flags this verb reads.</summary>
        public static bool IsFlag(string verb, string name) => Accepts(verb, name) && !TakesValue(verb, name);

        /// <summary>
        /// The option behind a spelling. Shape is a property of the name, not of the verb:
        /// <c>--force</c> means the same thing on <c>start</c> and on <c>install-skill</c>, and a
        /// name that took a value on one verb and none on another would be a trap for anyone
        /// reading a command line.
        /// </summary>
        static VerbOption? Shape(string verb, string name) =>
            Modifier(name) ?? VerbCatalog.Find(verb)?.Option(name);

        static VerbOption? Modifier(string name) =>
            VerbCatalog.Modifiers.FirstOrDefault(option =>
                option.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Rejects anything this verb does not read, in the order it was typed. Called before the
        /// verb runs, so a mistyped option costs nothing — no credential resolved, no browser
        /// opened, no half-understood write.
        /// </summary>
        public static void Validate(CommandLine command)
        {
            // An unknown verb is reported here rather than by the dispatch switch, because there
            // is no accepted list to check its options against. Same message, same exit code.
            var definition = VerbCatalog.Require(command.Verb);

            var accepted = VerbCatalog.CommandLineNames(command.Verb);
            var known = new HashSet<string>(accepted, StringComparer.OrdinalIgnoreCase);
            known.UnionWith(VerbCatalog.Modifiers.Select(option => option.Name));

            foreach (var name in command.Names)
            {
                if (known.Contains(name))
                    continue;

                var detail = VerbCatalog.GuidanceFor(command.Verb, name)
                    ?? $"It accepts: {string.Join(", ", known.Select(option => "--" + option))}.";

                throw new UsageException($"'{command.Verb}' does not accept --{name}. {detail}");
            }

            var allowed = definition.Positional is null ? 0 : 1;

            if (command.Positionals.Count > allowed)
                throw new UsageException(Unexpected(definition, accepted, command.Positionals[allowed]));
        }

        /// <summary>
        /// Names the argument, then names the reason it is almost always there. A bare "unexpected
        /// argument" is loud enough to stop the corruption, but the fragment it names looks like
        /// nonsense until you know a quote in the middle of a value is what produced it — and the
        /// person reading this has just had a description silently truncated.
        /// </summary>
        static string Unexpected(VerbDefinition definition, IReadOnlyList<string> accepted, string extra)
        {
            var shape = definition.Positional is not null
                ? $"takes {definition.Positional} and nothing else positional"
                : "takes no positional arguments";

            var advice = definition.Name switch
            {
                _ when accepted.Contains("description", StringComparer.OrdinalIgnoreCase) =>
                    " Prose is safest given as --description-file <path>, or --description - to read it from stdin; "
                    + "neither goes through the command line.",

                // The verb most likely to be handed a quoted phrase, and the one where a split is
                // least visible: the surviving fragment is still a legal search and still finds
                // something, so the answer looks fine and is to a different question.
                "find" => " The search text has to arrive as one argument. A single term usually "
                    + "finds more than a phrase does, because Drive matches whole words.",

                _ => string.Empty
            };

            return $"'{definition.Name}' {shape}, and got '{extra}'. "
                + "If that is a fragment of something you passed, the shell split the value: a double quote "
                + "inside an argument is not escaped by PowerShell, so everything after it arrives as separate "
                + "arguments."
                + advice;
        }
    }
}

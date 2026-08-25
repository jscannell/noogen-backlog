using System.Text;

namespace Noogen.Backlog
{
    /// <summary>
    /// The surface, written out for whoever asked.
    ///
    /// It is generated from <see cref="VerbCatalog"/> rather than typed out, because a help text
    /// that is written by hand describes what somebody believed the code accepted. This one cannot
    /// name a flag no verb reads, and cannot miss one that was added — which is the same promise
    /// the catalog makes to the parser, extended to the reader.
    ///
    /// Both front ends ask. The CLI prints this for `backlog help`; the MCP server hands it back
    /// from its own `help` verb, which is how a caller learns the surface without it being loaded
    /// into every conversation up front.
    /// </summary>
    public static class VerbHelp
    {
        const string Preamble =
            "backlog — a WSJF-prioritized Kanban backlog stored in Google Drive.\n" +
            "\n" +
            "Work moves Backlog -> In Progress -> Archive. The tab a ticket lives on is its state,\n" +
            "so the verbs below are the transitions; there is no free-form status flag. Only\n" +
            "unstarted work is WSJF-ranked.";

        const string CliTrailer =
            "Every command accepts --json for machine-readable output, which is always UTC. On\n" +
            "list, next, wip and find, --fields id,wsjf,title narrows it to the keys you asked for.\n" +
            "Human output uses the backlog's configured timezone; --utc shows UTC.\n" +
            "\n" +
            "Prose given inline goes through the shell, which on Windows splits the value at an\n" +
            "embedded double quote. For anything longer than a line use --<name>-file <path>, or\n" +
            "--<name> - to read standard input; only one option per command may read stdin.\n" +
            "\n" +
            "WSJF scores are modified Fibonacci: 1, 2, 3, 5, 8, 13, 20.";

        const string McpTrailer =
            "Results are always structured and always UTC — there is no json or utc option here.\n" +
            "Prose arrives as an ordinary string, newlines and quotes included, so none of the\n" +
            "command line's file and stdin spellings are needed.\n" +
            "\n" +
            "Ask for one verb by name to see what it takes. WSJF scores are modified Fibonacci:\n" +
            "1, 2, 3, 5, 8, 13, 20.";

        static string Prefix(VerbSurface surface) => surface == VerbSurface.Cli ? "backlog " : string.Empty;

        /// <summary>Every verb this surface offers, grouped, with the prose each group needs.</summary>
        public static string Write(VerbSurface surface = VerbSurface.Cli)
        {
            var text = new StringBuilder(Preamble).Append("\n\n");

            foreach (var group in VerbCatalog.Groups)
            {
                var verbs = VerbCatalog.On(surface).Where(verb => verb.Group == group.Title).ToList();

                if (verbs.Count == 0)
                    continue;

                text.Append(group.Title).Append('\n');

                foreach (var verb in verbs)
                {
                    text.Append(Usage(verb, surface, "  ")).Append('\n');
                    text.Append("      ").Append(verb.Summary).Append('\n');
                }

                if (group.Note is not null)
                    text.Append('\n').Append(Indent(Wrap(group.Note), "  ")).Append('\n');

                text.Append('\n');
            }

            var refused = VerbCatalog.All
                .Where(verb => !verb.OfferedOn(surface) && verb.McpRefusal is not null)
                .ToList();

            if (surface == VerbSurface.Mcp && refused.Count > 0)
            {
                text.Append("NOT OFFERED HERE\n");

                foreach (var verb in refused)
                    text.Append("  ").Append(verb.Name).Append("\n      ").Append(verb.McpRefusal).Append('\n');

                text.Append('\n');
            }

            return text.Append(surface == VerbSurface.Cli ? CliTrailer : McpTrailer).ToString();
        }

        /// <summary>
        /// One verb: what it does, what it takes, and what each option is for.
        ///
        /// This is the level a caller reaches for after choosing a verb, which is why it is a
        /// separate answer rather than a section of the last one — reading the whole surface to
        /// learn one verb is what makes a self-describing tool expensive.
        /// </summary>
        public static string Write(string verb, VerbSurface surface = VerbSurface.Cli)
        {
            var definition = VerbCatalog.Require(verb);

            if (!definition.OfferedOn(surface))
            {
                return $"'{definition.Name}' is not available here. {definition.McpRefusal}".TrimEnd();
            }

            var text = new StringBuilder()
                .Append(definition.Name).Append(" — ").Append(definition.Summary).Append("\n\n")
                .Append("usage:\n").Append(Usage(definition, surface, "  ")).Append('\n');

            if (definition.Positional is not null)
                text.Append("\n  <").Append(definition.PositionalName).Append(">  ").Append(definition.Positional).Append('\n');

            var options = definition.OptionsOn(surface).ToList();

            if (options.Count > 0)
            {
                var width = options.Max(option => option.Name.Length) + 2;

                text.Append('\n');

                foreach (var option in options)
                {
                    text.Append("  ").Append(Dash(surface)).Append(option.Name.PadRight(width))
                        .Append(option.Description);

                    if (option.Required)
                        text.Append("  (required)");

                    if (option.Alias is not null)
                        text.Append("  (also ").Append(Dash(surface)).Append(option.Alias).Append(')');

                    text.Append('\n');
                }
            }

            var prose = options.Where(option => option.IsProse).Select(option => option.Name).ToList();

            if (prose.Count > 0 && surface == VerbSurface.Cli)
            {
                text.Append('\n')
                    .Append(string.Join(" and ", prose))
                    .Append(prose.Count == 1 ? " is prose, so it also takes " : " are prose, so each also takes ")
                    .Append("--<name>-file <path>\nand --<name> - for standard input. ")
                    .Append("Only one option per command may read stdin.\n");
            }

            return text.ToString().TrimEnd();
        }

        /// <summary>
        /// The width a note is hard-wrapped to. The catalog holds prose as prose — one paragraph
        /// per idea, no embedded layout — because more than one surface renders the same words and
        /// only one of them is a terminal with a column count.
        /// </summary>
        const int Width = 76;

        /// <summary>
        /// One verb's usage, broken across lines that fit and hung under the verb name so the
        /// continuation reads as more options rather than as another command. `new` takes ten
        /// options; on one line it is two hundred characters and nobody reads it.
        /// </summary>
        static string Usage(VerbDefinition verb, VerbSurface surface, string indent)
        {
            var parts = verb.UsageParts(surface);
            var head = indent + Prefix(surface) + parts[0];

            // Up to and including the verb name, so the space the loop appends puts the next
            // option in the same column as the first one.
            var hanging = new string(' ', indent.Length + Prefix(surface).Length + verb.Name.Length);

            var lines = new List<string>();
            var line = new StringBuilder(head);

            foreach (var part in parts.Skip(1))
            {
                if (line.Length + 1 + part.Length > Width)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(hanging);
                }

                line.Append(' ').Append(part);
            }

            lines.Add(line.ToString());
            return string.Join("\n", lines);
        }

        static string Wrap(string text) => string.Join("\n", text.Split('\n').Select(WrapParagraph));

        static string WrapParagraph(string paragraph)
        {
            if (paragraph.Length <= Width)
                return paragraph;

            var lines = new List<string>();
            var line = new StringBuilder();

            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > Width)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }

                if (line.Length > 0)
                    line.Append(' ');

                line.Append(word);
            }

            if (line.Length > 0)
                lines.Add(line.ToString());

            return string.Join("\n", lines);
        }

        static string Indent(string text, string prefix) =>
            string.Join('\n', text.Split('\n').Select(line => line.Length == 0 ? line : prefix + line));

        static string Dash(VerbSurface surface) => surface == VerbSurface.Cli ? "--" : string.Empty;
    }
}

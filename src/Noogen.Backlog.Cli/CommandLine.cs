using Noogen.Backlog.Verbs;

namespace Noogen.Backlog.Cli
{
    /// <summary>
    /// Deliberately tiny argument parsing: `backlog &lt;verb&gt; [positionals] [--option value] [--flag]`.
    /// A dependency-free parser keeps the tool a single fast binary, which matters when an agent
    /// shells out to it several times in a turn.
    ///
    /// <b>The shape of every name is declared, never inferred.</b> This used to read <c>--name</c>
    /// and then look at what followed: a non-<c>--</c> argument became its value, anything else
    /// made it a valueless flag. Nothing said which of the two a name was meant to be, so every
    /// wrong guess failed silently — <c>--json</c> swallowed the argument after it and stopped
    /// being set, a value beginning with two dashes could not be passed, and an option typed with
    /// its value missing became a flag nobody reads. <see cref="CommandLineRules.TakesValue"/> answers from
    /// the table instead, which makes all three decidable: a flag never consumes what follows it,
    /// an option with nothing to take is a usage error naming itself, and the argument after an
    /// option is its value even when it begins with two dashes.
    ///
    /// The one thing an option will not take as its value is another option <em>this verb reads</em>.
    /// <c>note NG-1 --text --json</c> is a person who forgot the text, not a note that says
    /// "--json", and silently binding it there would put the machine contract back exactly where
    /// this change found it. <c>--text=--json</c> says it on purpose.
    /// </summary>
    public class CommandLine
    {
        readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _positionals = [];
        readonly List<string> _names = [];

        CommandLine()
        {
        }

        public string Verb { get; private set; } = "help";

        public IReadOnlyList<string> Positionals => _positionals;

        /// <summary>
        /// Every option and flag name given, in the order typed. <see cref="CommandLineRules.Validate"/>
        /// checks these against what the verb reads — nothing else can see a name that no command
        /// asks for. Order is kept so the error names the first mistake rather than an arbitrary
        /// one.
        /// </summary>
        public IReadOnlyList<string> Names => _names;

        public bool Json => HasFlag("json");

        /// <summary>
        /// Whether JSON was asked for, read straight off the raw arguments. A command line that
        /// <see cref="Parse"/> refuses never becomes a <see cref="CommandLine"/> to ask, and the
        /// machine contract has to hold for that failure too — it is the one an agent shelling out
        /// is most likely to meet.
        /// </summary>
        public static bool WantsJson(string[] args) =>
            args.Any(argument =>
                argument.Equals("--json", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--json=", StringComparison.OrdinalIgnoreCase));

        /// <summary>Everything after this is positional, whatever it looks like.</summary>
        public const string EndOfOptions = "--";

        public static CommandLine Parse(string[] args)
        {
            var command = new CommandLine();

            if (args.Length == 0)
                return command;

            command.Verb = args[0].TrimStart('-').ToLowerInvariant();

            var positionalsOnly = false;

            for (var i = 1; i < args.Length; i++)
            {
                var argument = args[i];

                if (positionalsOnly || !argument.StartsWith("--", StringComparison.Ordinal))
                {
                    command._positionals.Add(argument);
                    continue;
                }

                if (argument == EndOfOptions)
                {
                    positionalsOnly = true;
                    continue;
                }

                var name = argument[2..];
                var equals = name.IndexOf('=');

                if (equals >= 0)
                {
                    var value = name[(equals + 1)..];
                    name = name[..equals];
                    command._names.Add(name);

                    // A flag given a value is the same silent no-op from the other direction:
                    // `--force=true` would land in _options, HasFlag would stay false, and the
                    // command would run without the thing that was asked for.
                    if (CommandLineRules.IsFlag(command.Verb, name))
                    {
                        throw new UsageException(
                            $"--{name} carries no value, so '{argument}' asks for something that does not exist. "
                            + $"Write --{name} on its own.");
                    }

                    command._options[name] = value;
                    continue;
                }

                command._names.Add(name);

                // An undeclared name is left as a flag on purpose. It cannot be read anyway —
                // CommandLineRules.Validate is about to refuse it — and consuming the next argument would
                // hide a positional that the error should be naming instead.
                if (!CommandLineRules.TakesValue(command.Verb, name))
                {
                    command._flags.Add(name);
                    continue;
                }

                var next = i + 1 < args.Length ? args[i + 1] : null;

                if (next is null || next == EndOfOptions || IsOptionOf(command.Verb, next))
                    throw new UsageException(MissingValue(command.Verb, name, next));

                command._options[name] = next;
                i++;
            }

            return command;
        }

        /// <summary>Whether <paramref name="argument"/> spells an option <paramref name="verb"/> reads.</summary>
        static bool IsOptionOf(string verb, string argument)
        {
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                return false;

            var name = argument[2..];
            var equals = name.IndexOf('=');

            return CommandLineRules.Accepts(verb, equals >= 0 ? name[..equals] : name);
        }

        /// <summary>
        /// Names the option, what was found instead of its value, and the one spelling that passes
        /// a value the parser would otherwise read as an option.
        /// </summary>
        static string MissingValue(string verb, string name, string? next)
        {
            var found = next switch
            {
                null => "nothing follows it",
                EndOfOptions => $"'{EndOfOptions}' ends the options",
                _ => $"'{next}' is another option '{verb}' reads"
            };

            return $"--{name} takes a value, and {found}. "
                + $"If the value really does begin with two dashes, write --{name}=<value>.";
        }

        public bool HasFlag(string name) => _flags.Contains(name);

        public bool Has(string name) => _options.ContainsKey(name) || _flags.Contains(name);

        public string? Option(string name) => _options.TryGetValue(name, out var value) ? value : null;

        public string RequireOption(string name) =>
            Option(name) ?? throw new UsageException($"--{name} is required.");

        public string RequirePositional(int index, string description) =>
            index < _positionals.Count ? _positionals[index] : throw new UsageException($"Expected {description}.");

        public int? IntOption(string name) => OptionValue.WholeNumber(Option(name), "--" + name);

        /// <summary>
        /// The short flag or its spelled-out form. The abbreviations are what anyone scoring a
        /// stack of tickets actually types, but they are also the ones nobody remembers, so both
        /// are accepted. The name given first is the one an error message names.
        /// </summary>
        public int? IntOption(string name, string alias) =>
            Has(name) || !Has(alias) ? IntOption(name) : IntOption(alias);

        /// <summary>Parses a duration like `90d`, `12w`, or a bare day count.</summary>
        public DateTimeOffset? SinceOption(string name, DateTimeOffset now) =>
            OptionValue.Since(Option(name), now, "--" + name);
    }

}

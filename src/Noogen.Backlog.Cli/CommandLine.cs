using System.Globalization;

namespace Noogen.Backlog.Cli
{
    /// <summary>
    /// Deliberately tiny argument parsing: `backlog &lt;verb&gt; [positionals] [--option value] [--flag]`.
    /// A dependency-free parser keeps the tool a single fast binary, which matters when an agent
    /// shells out to it several times in a turn.
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
        /// Every option and flag name given, in the order typed. <see cref="Verbs.Validate"/>
        /// checks these against what the verb reads — nothing else can see a name that no command
        /// asks for. Order is kept so the error names the first mistake rather than an arbitrary
        /// one.
        /// </summary>
        public IReadOnlyList<string> Names => _names;

        public bool Json => HasFlag("json");

        public static CommandLine Parse(string[] args)
        {
            var command = new CommandLine();

            if (args.Length == 0)
                return command;

            command.Verb = args[0].TrimStart('-').ToLowerInvariant();

            for (var i = 1; i < args.Length; i++)
            {
                var argument = args[i];

                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    command._positionals.Add(argument);
                    continue;
                }

                var name = argument[2..];
                var equals = name.IndexOf('=');

                if (equals >= 0)
                {
                    var value = name[(equals + 1)..];
                    name = name[..equals];
                    command._options[name] = value;
                    command._names.Add(name);
                    continue;
                }

                command._names.Add(name);

                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                if (hasValue)
                {
                    command._options[name] = args[i + 1];
                    i++;
                }
                else
                {
                    command._flags.Add(name);
                }
            }

            return command;
        }

        public bool HasFlag(string name) => _flags.Contains(name);

        public bool Has(string name) => _options.ContainsKey(name) || _flags.Contains(name);

        public string? Option(string name) => _options.TryGetValue(name, out var value) ? value : null;

        public string RequireOption(string name) =>
            Option(name) ?? throw new UsageException($"--{name} is required.");

        public string RequirePositional(int index, string description) =>
            index < _positionals.Count ? _positionals[index] : throw new UsageException($"Expected {description}.");

        public int? IntOption(string name)
        {
            var raw = Option(name);
            if (raw is null)
                return null;

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                throw new UsageException($"--{name} must be a whole number, got '{raw}'.");

            return parsed;
        }

        /// <summary>
        /// The short flag or its spelled-out form. The abbreviations are what anyone scoring a
        /// stack of tickets actually types, but they are also the ones nobody remembers, so both
        /// are accepted. The name given first is the one an error message names.
        /// </summary>
        public int? IntOption(string name, string alias) =>
            Has(name) || !Has(alias) ? IntOption(name) : IntOption(alias);

        /// <summary>Parses a duration like `90d`, `12w`, or a bare day count.</summary>
        public DateTimeOffset? SinceOption(string name, DateTimeOffset now)
        {
            var raw = Option(name);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var trimmed = raw.Trim().ToLowerInvariant();
            var multiplier = 1.0;

            if (trimmed.EndsWith('d'))
            {
                trimmed = trimmed[..^1];
            }
            else if (trimmed.EndsWith('w'))
            {
                multiplier = 7;
                trimmed = trimmed[..^1];
            }
            else if (trimmed.EndsWith('m'))
            {
                multiplier = 30;
                trimmed = trimmed[..^1];
            }

            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                throw new UsageException($"--{name} must look like 90d, 12w, or 6m — got '{raw}'.");

            return now.AddDays(-amount * multiplier);
        }
    }

    public class UsageException : Exception
    {
        public UsageException(string message) : base(message)
        {
        }
    }
}

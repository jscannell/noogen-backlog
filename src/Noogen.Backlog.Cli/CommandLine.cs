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

        CommandLine()
        {
        }

        public string Verb { get; private set; } = "help";

        public IReadOnlyList<string> Positionals => _positionals;

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
                    command._options[name[..equals]] = name[(equals + 1)..];
                    continue;
                }

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

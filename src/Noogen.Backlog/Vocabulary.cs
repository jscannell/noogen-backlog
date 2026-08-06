using System.Text;

namespace Noogen.Backlog
{
    public enum TicketType
    {
        Feature,
        Bug,
        Chore,
        Spike
    }

    /// <summary>
    /// Sub-states of the In Progress column. Blocked is a condition of started work, not a
    /// lifecycle phase of its own — which is why it lives here rather than as a fourth tab.
    /// </summary>
    public enum WorkState
    {
        InProgress,
        InReview,
        Blocked
    }

    public enum Outcome
    {
        Done,
        Cancelled,
        Duplicate
    }

    /// <summary>
    /// One kebab-case wire form shared by the Sheet's data-validation dropdowns, the ticket
    /// frontmatter, and the CLI, so humans and agents cannot drift apart on vocabulary.
    /// </summary>
    public static class Vocabulary
    {
        public static string ToWire<TEnum>(TEnum value) where TEnum : struct, Enum =>
            ToKebabCase(value.ToString() ?? string.Empty);

        public static TEnum Parse<TEnum>(string wire, string field) where TEnum : struct, Enum
        {
            foreach (var candidate in Enum.GetValues<TEnum>())
            {
                if (string.Equals(ToWire(candidate), wire, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            throw new FormatException($"'{wire}' is not a valid {field}. Expected one of: {string.Join(", ", WireValues<TEnum>())}.");
        }

        public static TEnum? ParseOptional<TEnum>(string? wire, string field) where TEnum : struct, Enum =>
            string.IsNullOrWhiteSpace(wire) ? null : Parse<TEnum>(wire.Trim(), field);

        public static IReadOnlyList<string> WireValues<TEnum>() where TEnum : struct, Enum =>
            Enum.GetValues<TEnum>().Select(value => ToWire(value)).ToList();

        static string ToKebabCase(string pascalCase)
        {
            var builder = new StringBuilder(pascalCase.Length + 4);

            for (var i = 0; i < pascalCase.Length; i++)
            {
                var character = pascalCase[i];
                if (char.IsUpper(character) && i > 0)
                    builder.Append('-');

                builder.Append(char.ToLowerInvariant(character));
            }

            return builder.ToString();
        }
    }
}

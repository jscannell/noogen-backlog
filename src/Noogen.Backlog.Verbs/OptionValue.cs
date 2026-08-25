using System.Globalization;

namespace Noogen.Backlog.Verbs
{
    /// <summary>
    /// The shapes an option's value may take, read the same way wherever the words arrived from.
    ///
    /// `90d` is vocabulary this surface teaches: `backlog flow --since 90d` and
    /// `{"verb": "flow", "options": {"since": "90d"}}` are the same request, so the reading of it
    /// belongs beside the table that describes it rather than in each front end. Two copies would
    /// answer differently for `6m` the day one of them learned a new suffix, and the help — written
    /// from the same table — would be right about only one of them.
    ///
    /// <c>name</c> is the option spelled the way the front end spells it, so a refusal quotes back
    /// what the caller actually wrote.
    /// </summary>
    public static class OptionValue
    {
        /// <summary>A whole number, or null when the option was not given.</summary>
        public static int? WholeNumber(string? raw, string name)
        {
            if (raw is null)
                return null;

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                throw new UsageException($"{name} must be a whole number, got '{raw}'.");

            return parsed;
        }

        /// <summary>
        /// An instant that far back — `90d`, `12w`, `6m`, or a bare day count — or null when the
        /// option was not given.
        /// </summary>
        public static DateTimeOffset? Since(string? raw, DateTimeOffset now, string name)
        {
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
                throw new UsageException($"{name} must look like 90d, 12w, or 6m — got '{raw}'.");

            return now.AddDays(-amount * multiplier);
        }
    }
}

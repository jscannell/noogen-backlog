using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Cli
{
    /// <summary>
    /// How this front end puts an answer on a terminal: a compact table for a person, and the
    /// machine contract verbatim under `--json`.
    ///
    /// The shapes themselves are not here — they live in <see cref="BacklogJson"/> and
    /// <see cref="IBacklogView"/>, because the MCP server emits the same ones and a caller reading
    /// either has to see the same keys.
    /// </summary>
    public static class Output
    {
        public static void WriteJson(object payload) => Console.WriteLine(BacklogJson.Serialize(payload));

        public static void WriteJson(JsonNode node) => Console.WriteLine(BacklogJson.Serialize(node));

        /// <summary>Writes a result under the machine contract, narrowed to <c>--fields</c>.</summary>
        public static void WriteJson(IBacklogView view, IReadOnlySet<string>? fields = null) =>
            Console.WriteLine(BacklogJson.Serialize(view.ToNode(fields)));

        public static void WriteLine(string text = "") => Console.WriteLine(text);

        public static void WriteError(string text) => Console.Error.WriteLine(text);

        public static void WriteTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
        {
            if (rows.Count == 0)
            {
                Console.WriteLine("(nothing)");
                return;
            }

            var widths = new int[headers.Count];
            for (var i = 0; i < headers.Count; i++)
            {
                widths[i] = headers[i].Length;
                foreach (var row in rows)
                {
                    if (i < row.Count)
                        widths[i] = Math.Max(widths[i], row[i].Length);
                }
            }

            Console.WriteLine(Render(headers, widths));
            Console.WriteLine(string.Join("  ", widths.Select(width => new string('-', width))));

            foreach (var row in rows)
                Console.WriteLine(Render(row, widths));
        }

        static string Render(IReadOnlyList<string> cells, int[] widths)
        {
            var builder = new StringBuilder();

            for (var i = 0; i < widths.Length; i++)
            {
                if (i > 0)
                    builder.Append("  ");

                var value = i < cells.Count ? cells[i] : string.Empty;
                builder.Append(value.PadRight(widths[i]));
            }

            return builder.ToString().TrimEnd();
        }

        public static string Number(double? value) =>
            value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : "-";

        public static string Number(int? value) =>
            value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";

        public static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    /// <summary>
    /// Says out loud that a command is waiting on Google rather than hung. Stderr, always: stdout
    /// under `--json` is a single document and an agent parses it, so nothing else may land there.
    /// </summary>
    public class ConsoleRetryListener : IRetryListener
    {
        public void RateLimited(int attempt, int maxAttempts, TimeSpan delay) =>
            Output.WriteError(string.Format(
                CultureInfo.InvariantCulture,
                "Google is rate limiting requests; waiting {0:0.#}s before retry {1} of {2}.",
                delay.TotalSeconds,
                attempt,
                maxAttempts - 1));
    }
}

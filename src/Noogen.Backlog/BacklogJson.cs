using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Noogen.Backlog
{
    /// <summary>
    /// How a result is spelled on the wire. One set of options, because more than one front end
    /// emits these shapes — the CLI under `--json` and the MCP server as a tool result — and a
    /// caller reading either has to see the same keys.
    /// </summary>
    public static class BacklogJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            // Not indented, deliberately. The agent reading this pays for every byte, and
            // indentation is a quarter of a list response — 22,598 characters of `list --json`
            // over a 44-ticket backlog, of which 5,284 were spaces and newlines. Whitespace was
            // never part of the contract the shapes promise, so compacting costs nothing; pipe
            // through a formatter when reading one of these by hand.
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // Console output and tool results, not HTML. The default encoder escapes quotes,
            // angle brackets, and ampersands into \uXXXX noise, which makes messages containing
            // paths and examples hard to read for both people and models.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static JsonNode ToNode(object payload) => JsonSerializer.SerializeToNode(payload, Options)!;

        public static string Serialize(JsonNode node) => node.ToJsonString(Options);

        public static string Serialize(object payload) => JsonSerializer.Serialize(payload, Options);

        /// <summary>
        /// The names <c>fields</c> accepts, taken from <see cref="TicketView"/> itself rather than
        /// listed here, so a property added there is selectable the same day. They are the names as
        /// they appear on the wire, which is what the caller has in front of them.
        /// </summary>
        static readonly HashSet<string> TicketFieldNames = new(
            typeof(TicketView)
                .GetProperties()
                .Where(property => property.CanRead)
                .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)),
            StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reads a <c>fields</c> value into the set <see cref="TicketView.ToNode"/> keeps, or null
        /// for "everything". An unrecognised name is a usage error naming the alternatives: the
        /// whole point of the option is to ask for less, and silently ignoring a typo would answer
        /// with a column the caller did not get and cannot see is missing.
        /// </summary>
        public static IReadOnlySet<string>? ParseFields(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var names = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (names.Length == 0)
                throw new UsageException("--fields needs at least one name. It accepts: " + KnownFields + ".");

            var unknown = names.Where(name => !TicketFieldNames.Contains(name)).ToList();

            if (unknown.Count > 0)
                throw new UsageException(
                    $"--fields does not know {string.Join(", ", unknown.Select(name => "'" + name + "'"))}. "
                    + "It accepts: " + KnownFields + ".");

            return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        public static string KnownFields =>
            string.Join(", ", typeof(TicketView)
                .GetProperties()
                .Where(property => property.CanRead)
                .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)));
    }

    /// <summary>
    /// A result that can narrow the tickets inside it to the fields a caller asked for.
    ///
    /// Narrowing belongs to the shape rather than to a walker over the serialised tree, because
    /// only the shape knows where its tickets are. A `wip` result holds them under one key, a
    /// queue is an array of them, and `show` holds exactly one beside a body — a single rule that
    /// tried to find them all would be guessing.
    /// </summary>
    public interface IBacklogView
    {
        JsonNode ToNode(IReadOnlySet<string>? fields);
    }
}

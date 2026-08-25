using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Noogen.Backlog.Mcp
{
    /// <summary>
    /// How an answer leaves this front end.
    ///
    /// The structured half is <see cref="IBacklogView.ToNode"/> and nothing else — the same node
    /// the CLI prints under <c>--json</c>, so a caller reading either sees the same keys. Inventing
    /// a shape here is the thing that would make "both front ends answer" stop meaning "both answer
    /// the same way".
    ///
    /// The text half is a sentence, not a second copy of the result. The specification says a
    /// structured result SHOULD also carry its JSON serialized as text, for a client that reads
    /// only <c>content</c>; done literally that doubles every response for a reader that, here, is
    /// a model paying by the byte. What that SHOULD is protecting is a client which would otherwise
    /// get nothing usable, and a sentence saying what happened serves that better than a wall of
    /// JSON. The README says so out loud, because it is a deviation.
    ///
    /// A refusal is <c>isError</c> in the result, never a JSON-RPC error: the call was well formed
    /// and the backlog said no, which is an answer. Its shape is the CLI's failure shape, so the
    /// name of a fault does not change with the front end that reports it.
    /// </summary>
    public static class ToolResults
    {
        public static CallToolResult Answer(IBacklogView view, IReadOnlySet<string>? fields, string text) => new()
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = Element(view.ToNode(fields))
        };

        /// <summary>Prose that is the whole answer: help, and the guides.</summary>
        public static CallToolResult Prose(string text) => new()
        {
            Content = [new TextContentBlock { Text = text }]
        };

        public static CallToolResult Failure(string kind, string message) => new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"error ({kind}): {message}" }],
            StructuredContent = Element(new JsonObject
            {
                ["kind"] = kind,
                ["error"] = message
            })
        };

        static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node, BacklogJson.Options);
    }
}

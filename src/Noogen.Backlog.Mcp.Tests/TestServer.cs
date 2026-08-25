using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Noogen.Backlog.Tests;
using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Mcp.Tests
{
    /// <summary>
    /// The tool over the same in-memory backlog every other test uses, and the small amount of
    /// reading-a-result these tests do in common.
    ///
    /// Driving <see cref="BacklogTool.InvokeAsync"/> directly rather than over HTTP is deliberate:
    /// what is worth pinning is what this front end decides — which verb, which options, what a
    /// refusal is called and what the answer's keys are. Whether the SDK can put a
    /// <see cref="CallToolResult"/> on a socket is the SDK's business and it has its own tests.
    /// </summary>
    public static class TestServer
    {
        public const string Owner = "server@noogen.ai";

        public static BacklogTool ToolFor(TestBacklog backlog, string? owner = Owner) =>
            new(backlog.Api, new ServerIdentity(
                CredentialSource.ServiceAccountKey,
                "service account key at /etc/noogen/backlog.json",
                owner,
                TestBacklog.SpreadsheetId));

        public static Task<CallToolResult> CallAsync(this BacklogTool tool, string verb, JsonObject? options = null) =>
            tool.InvokeAsync(verb, options);

        /// <summary>One option, for the many calls that pass exactly one.</summary>
        public static JsonObject With(string name, JsonNode? value) => new() { [name] = value };

        public static bool Failed(this CallToolResult result) => result.IsError ?? false;

        public static string Text(this CallToolResult result) =>
            string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        public static JsonElement Structured(this CallToolResult result) =>
            result.StructuredContent ?? throw new InvalidOperationException("The result carried no structured content.");

        /// <summary>The one word a caller reacts to. Null on a result that is not a refusal.</summary>
        public static string? Kind(this CallToolResult result) =>
            result.Failed() ? result.Structured().GetProperty("kind").GetString() : null;

        public static string Error(this CallToolResult result) =>
            result.Structured().GetProperty("error").GetString() ?? string.Empty;

        /// <summary>The structured half as text, for comparing against the contract's own spelling.</summary>
        public static string Json(this CallToolResult result) =>
            JsonSerializer.Serialize(result.Structured(), BacklogJson.Options);

        /// <summary>What the CLI would print for the same view under <c>--json</c>.</summary>
        public static string Json(IBacklogView view, IReadOnlySet<string>? fields = null) =>
            BacklogJson.Serialize(view.ToNode(fields));

        public static IReadOnlyList<string> KeysOf(JsonElement element) =>
            [.. element.EnumerateObject().Select(property => property.Name)];
    }
}

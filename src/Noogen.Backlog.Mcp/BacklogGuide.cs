using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Noogen.Backlog.Verbs;

namespace Noogen.Backlog.Mcp
{
    /// <summary>
    /// The guidance behind the verbs — how to write a ticket, how WSJF is scored, how prose gets
    /// in — served as MCP resources.
    ///
    /// These are the skill's own files, read out of the assembly that carries them. There is
    /// exactly one embedded copy and this is not a second: a guide describing verbs the tool does
    /// not have is worse than no guide, so the bytes an agent reads here are byte-for-byte the ones
    /// `backlog install-skill` writes to disk.
    ///
    /// Resources are application-controlled, and a model cannot always fetch one for itself, so the
    /// same bytes are also reachable through the tool as `help` with a topic. One copy, two front
    /// doors — the second exists because the first is not always openable from where the model is.
    /// </summary>
    [McpServerResourceType]
    public static class BacklogGuide
    {
        public const string UriPrefix = "backlog://guide/";

        [McpServerResource(UriTemplate = UriPrefix + "overview", Name = "backlog-overview", MimeType = "text/markdown")]
        [Description("How the backlog works and how to drive it: the three columns, what to read before filing, and the shape of a ticket.")]
        public static TextResourceContents Overview() => Guide("overview");

        [McpServerResource(UriTemplate = UriPrefix + "writing-style", Name = "backlog-writing-style", MimeType = "text/markdown")]
        [Description("How to word a ticket — titles, descriptions and acceptance criteria that say what is true rather than what sounds finished.")]
        public static TextResourceContents WritingStyle() => Guide("writing-style");

        [McpServerResource(UriTemplate = UriPrefix + "wsjf", Name = "backlog-wsjf", MimeType = "text/markdown")]
        [Description("What the four WSJF numbers mean and how to choose one, so scores from different sessions can be compared.")]
        public static TextResourceContents Wsjf() => Guide("wsjf");

        [McpServerResource(UriTemplate = UriPrefix + "prose-input", Name = "backlog-prose-input", MimeType = "text/markdown")]
        [Description("How the prose options behave, and what a section replacement does to a document a person has been editing.")]
        public static TextResourceContents ProseInput() => Guide("prose-input");

        static TextResourceContents Guide(string topic) => new()
        {
            Uri = UriPrefix + topic,
            MimeType = "text/markdown",
            Text = Read(topic)
        };

        /// <summary>
        /// The bytes of one guide. <paramref name="topic"/> is what a caller asks for; which file
        /// carries it is derived rather than tabulated, so a reference added to the skill is
        /// readable the day it lands.
        /// </summary>
        public static string Read(string topic)
        {
            var path = FileNameOf(topic);

            var file = EmbeddedSkill.Files.FirstOrDefault(candidate =>
                string.Equals(candidate.RelativePath, path, StringComparison.OrdinalIgnoreCase));

            if (file is null)
            {
                throw new UsageException(
                    $"There is no '{topic}' guide. This server carries: {string.Join(", ", VerbCatalog.Guides)}.");
            }

            // A leading byte-order mark would land in the middle of a tool result as a stray
            // character; the file on disk is what it is, and this is a reader, not a rewriter.
            return Encoding.UTF8.GetString(file.Content).TrimStart('﻿');
        }

        /// <summary>
        /// Where a topic lives in the skill. `overview` is the skill itself; everything else is one
        /// of the references it points at, named after the topic.
        /// </summary>
        static string FileNameOf(string topic) =>
            string.Equals(topic, "overview", StringComparison.OrdinalIgnoreCase)
                ? EmbeddedSkill.EntryFileName
                : $"references/{topic}.md";
    }
}

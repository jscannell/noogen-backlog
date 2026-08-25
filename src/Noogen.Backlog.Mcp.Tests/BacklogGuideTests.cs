using System.Text;
using ModelContextProtocol.Server;
using Noogen.Backlog.Verbs;

namespace Noogen.Backlog.Mcp.Tests
{
    /// <summary>
    /// The guidance served as resources.
    ///
    /// What matters is that these are the skill's own bytes and not a second copy of them
    /// (invariant 16): a guide describing verbs the tool does not have is worse than no guide, and
    /// the only way to keep that impossible is for there to be one embedded copy that both
    /// `install-skill` and this server read.
    /// </summary>
    public class BacklogGuideTests
    {
        static IReadOnlyList<McpServerResource> Resources =>
            [.. typeof(BacklogGuide)
                .GetMethods()
                .Where(method => method.GetCustomAttributes(typeof(McpServerResourceAttribute), false).Length > 0)
                .Select(method => McpServerResource.Create(method, target: null, new McpServerResourceCreateOptions()))];

        [Fact]
        public void Resources_TheOnesDeclared_AreExactlyTheTopicsTheCatalogNames()
        {
            var uris = Resources.Select(resource => resource.ProtocolResource!.Uri).Order().ToList();

            Assert.Equal(
                VerbCatalog.Guides.Select(topic => BacklogGuide.UriPrefix + topic).Order().ToList(),
                uris);
        }

        [Fact]
        public void Read_EveryTopic_IsTheSkillFileByteForByte()
        {
            foreach (var topic in VerbCatalog.Guides)
            {
                var path = topic == "overview" ? EmbeddedSkill.EntryFileName : $"references/{topic}.md";

                var file = EmbeddedSkill.Files.Single(candidate =>
                    string.Equals(candidate.RelativePath, path, StringComparison.Ordinal));

                Assert.Equal(Encoding.UTF8.GetString(file.Content).TrimStart('﻿'), BacklogGuide.Read(topic));
            }
        }

        [Fact]
        public void Read_TopicThatIsNotAGuide_NamesTheOnesThatAre()
        {
            var exception = Assert.Throws<UsageException>(() => BacklogGuide.Read("kanban"));

            foreach (var topic in VerbCatalog.Guides)
                Assert.Contains(topic, exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The skill teaches these verbs, so the entry file has to be the one the CLI installs —
        /// not a rewrite of it for this surface.
        /// </summary>
        [Fact]
        public void Read_Overview_IsTheSkillItself()
        {
            Assert.Contains("backlog", BacklogGuide.Read("overview"), StringComparison.OrdinalIgnoreCase);
            Assert.True(EmbeddedSkill.IsEmbedded);
        }
    }
}

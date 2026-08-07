namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// Replacing the Description is the only prose the store rewrites, so what these pin is mostly
    /// what it must *not* touch. Eating a human's edit is the one unrecoverable failure here
    /// (invariant 9), and a section rewrite is the operation with the appetite for it.
    /// </summary>
    public class TicketSectionTests
    {
        const string Body =
            "## Description\n\nThe original description.\n\n" +
            "## Acceptance Criteria\n\n- [ ] *TODO*\n\n" +
            "## Notes\n\nSomething a person wrote.\n\n" +
            "## Activity Log\n\n- 2026-08-07 09:00 — created\n";

        static string Replace(string body, string text) =>
            TicketDocument.ReplaceSection(body, TicketDocument.DescriptionHeading, text);

        [Fact]
        public void ReplaceSection_SectionExists_ReplacesOnlyItsText()
        {
            var rewritten = Replace(Body, "A better description.");

            Assert.Contains("## Description\n\nA better description.\n", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("The original description.", rewritten, StringComparison.Ordinal);
        }

        [Fact]
        public void ReplaceSection_SectionExists_LeavesEveryOtherSectionByteIdentical()
        {
            var rewritten = Replace(Body, "A better description.");
            var tail = rewritten[rewritten.IndexOf("## Acceptance Criteria", StringComparison.Ordinal)..];

            Assert.Equal(Body[Body.IndexOf("## Acceptance Criteria", StringComparison.Ordinal)..], tail);
        }

        [Fact]
        public void ReplaceSection_DescriptionSpansLines_KeepsEveryLine()
        {
            var rewritten = Replace(Body, "First paragraph.\n\n- a bullet\n- another\n\nLast paragraph.");

            Assert.Contains("First paragraph.\n\n- a bullet\n- another\n\nLast paragraph.\n\n## Acceptance Criteria",
                rewritten, StringComparison.Ordinal);
        }

        /// <summary>
        /// A `###` inside the description is part of it. Ending the section at any heading would
        /// let the next rewrite swallow the subsections underneath.
        /// </summary>
        [Fact]
        public void ReplaceSection_SectionHasASubheading_TreatsItAsPartOfTheSection()
        {
            var body = "## Description\n\nIntro.\n\n### Background\n\nMore.\n\n## Notes\n\nKeep me.\n";

            var rewritten = Replace(body, "Replaced.");

            Assert.DoesNotContain("### Background", rewritten, StringComparison.Ordinal);
            Assert.Contains("## Notes\n\nKeep me.", rewritten, StringComparison.Ordinal);
        }

        [Fact]
        public void ReplaceSection_SectionIsLast_DoesNotInventATrailingSection()
        {
            var rewritten = Replace("## Notes\n\nKeep me.\n\n## Description\n\nOld.\n", "New.");

            Assert.Equal("## Notes\n\nKeep me.\n\n## Description\n\nNew.\n", rewritten);
        }

        [Fact]
        public void ReplaceSection_HeadingIsMissing_InsertsOneAndKeepsTheWholeBody()
        {
            var body = "## Notes\n\nSomething a person wrote.\n";

            var rewritten = Replace(body, "A description.");

            Assert.Equal("## Description\n\nA description.\n\n## Notes\n\nSomething a person wrote.\n", rewritten);
        }

        [Fact]
        public void ReplaceSection_BodyIsProseWithNoHeadings_KeepsTheProse()
        {
            var rewritten = Replace("Just some prose someone typed.\n", "A description.");

            Assert.Contains("Just some prose someone typed.", rewritten, StringComparison.Ordinal);
            Assert.StartsWith("## Description", rewritten, StringComparison.Ordinal);
        }

        [Fact]
        public void ReplaceSection_BodyIsEmpty_WritesTheSection()
        {
            Assert.Equal("## Description\n\nA description.\n", Replace(string.Empty, "A description."));
        }

        [Fact]
        public void ReplaceSection_HeadingDiffersInCase_StillMatchesIt()
        {
            // The heading a person typed is theirs — matched, not relabelled, and not duplicated.
            var rewritten = Replace("## DESCRIPTION\n\nOld.\n\n## Notes\n\nKeep me.\n", "New.");

            Assert.Equal("## DESCRIPTION\n\nNew.\n\n## Notes\n\nKeep me.\n", rewritten);
        }

        /// <summary>
        /// `#hashtag` opening a paragraph is not a heading in markdown, and treating it as one
        /// would end the section in the middle of a sentence.
        /// </summary>
        [Fact]
        public void ReplaceSection_ProseStartsWithAHash_DoesNotTreatItAsAHeading()
        {
            var body = "## Description\n\nOld.\n\n#notaheading still the description\n\n## Notes\n\nKeep me.\n";

            var rewritten = Replace(body, "New.");

            Assert.DoesNotContain("#notaheading", rewritten, StringComparison.Ordinal);
            Assert.Contains("## Notes\n\nKeep me.", rewritten, StringComparison.Ordinal);
        }

        [Fact]
        public void ReplaceSection_BodyUsesWindowsLineEndings_NormalisesThemLikeSerializeDoes()
        {
            var rewritten = Replace("## Description\r\n\r\nOld.\r\n\r\n## Notes\r\n\r\nKeep me.\r\n", "New.");

            Assert.DoesNotContain("\r", rewritten, StringComparison.Ordinal);
            Assert.Equal("## Description\n\nNew.\n\n## Notes\n\nKeep me.\n", rewritten);
        }

        /// <summary>
        /// Docs' export hard-wraps and escapes prose (invariant 18). None of that is ours to
        /// normalise, so a section we are not replacing must survive it verbatim.
        /// </summary>
        [Fact]
        public void ReplaceSection_OtherSectionsCarryDocsEscaping_LeavesItAlone()
        {
            var body = "## Description\n\nOld.\n\n## Notes\n\nThe sign\\-in flow, and a \\-\\- dash.\n";

            var rewritten = Replace(body, "New.");

            Assert.Contains("The sign\\-in flow, and a \\-\\- dash.", rewritten, StringComparison.Ordinal);
        }

        [Fact]
        public void ReplaceSection_AppliedRepeatedly_Settles()
        {
            var once = Replace(Body, "New.");

            Assert.Equal(once, Replace(once, "New."));
        }
    }
}

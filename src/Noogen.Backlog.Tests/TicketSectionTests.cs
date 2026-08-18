namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// Description and Acceptance Criteria are the only prose the store rewrites, so what these
    /// pin is mostly what it must *not* touch. Eating a human's edit is the one unrecoverable
    /// failure here (invariant 9), and a section rewrite is the operation with the appetite for it.
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

        static string ReplaceCriteria(string body, string text) =>
            TicketDocument.ReplaceSection(body, TicketDocument.AcceptanceCriteriaHeading, text);

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

        // --- the second heading it is used for ---
        //
        // A section in the middle of the body, unlike the description: it has a neighbour on both
        // sides, so a rewrite that ran to the wrong boundary would eat one of them.

        [Fact]
        public void ReplaceSection_AcceptanceCriteria_ReplacesOnlyThatSection()
        {
            var rewritten = ReplaceCriteria(Body, "- [x] the gateway round-trips\n- [ ] doctor reports it");

            Assert.Contains("## Acceptance Criteria\n\n- [x] the gateway round-trips\n- [ ] doctor reports it\n",
                rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("*TODO*", rewritten, StringComparison.Ordinal);
        }

        [Fact]
        public void ReplaceSection_AcceptanceCriteria_LeavesTheSectionsOnBothSidesByteIdentical()
        {
            var rewritten = ReplaceCriteria(Body, "- [ ] something measurable");

            Assert.Equal(
                Body[..Body.IndexOf("## Acceptance Criteria", StringComparison.Ordinal)],
                rewritten[..rewritten.IndexOf("## Acceptance Criteria", StringComparison.Ordinal)]);

            Assert.Equal(
                Body[Body.IndexOf("## Notes", StringComparison.Ordinal)..],
                rewritten[rewritten.IndexOf("## Notes", StringComparison.Ordinal)..]);
        }

        /// <summary>
        /// Both sections rewritten in one pass, which is what `new`-then-fill-in and a two-flag
        /// `edit` both come down to. Replacing one must leave the other's heading findable.
        /// </summary>
        [Fact]
        public void ReplaceSection_BothEditableSections_KeepsTheDocumentsShape()
        {
            var rewritten = ReplaceCriteria(Replace(Body, "A better description."), "- [ ] something measurable");

            Assert.Equal(
                "## Description\n\nA better description.\n\n" +
                "## Acceptance Criteria\n\n- [ ] something measurable\n\n" +
                "## Notes\n\nSomething a person wrote.\n\n" +
                "## Activity Log\n\n- 2026-08-07 09:00 — created\n",
                rewritten);
        }

        /// <summary>
        /// A document written before the section existed — or one whose heading a person renamed —
        /// gains one rather than having a section guessed at. Insertion cannot destroy anything,
        /// which is the property that earns the rewrite its exception in the first place.
        /// </summary>
        [Fact]
        public void ReplaceSection_AcceptanceCriteriaHeadingIsMissing_InsertsOneAndKeepsTheWholeBody()
        {
            var body = "## Description\n\nWhy this matters.\n\n## Notes\n\nSomething a person wrote.\n";

            var rewritten = ReplaceCriteria(body, "- [ ] something measurable");

            Assert.Contains("Why this matters.", rewritten, StringComparison.Ordinal);
            Assert.Contains("Something a person wrote.", rewritten, StringComparison.Ordinal);
            Assert.StartsWith("## Acceptance Criteria\n\n- [ ] something measurable", rewritten, StringComparison.Ordinal);
        }

        /// <summary>
        /// The fault this rule exists to prevent, written the way it happened: a body whose first
        /// line is a level-2 heading puts that heading immediately after `## Description`, so the
        /// section a later write can reach is empty, the write inserts instead of replacing, and
        /// the document ends up holding one description for every edit.
        /// </summary>
        [Fact]
        public void ReplaceSection_TextHoldsASiblingLevelHeading_IsRefused()
        {
            var exception = Assert.Throws<ArgumentException>(
                () => Replace(Body, "## Problem\n\nThe thing that is wrong."));

            Assert.Contains("## Problem", exception.Message, StringComparison.Ordinal);
            Assert.Contains("### Problem", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Criteria are a flat checklist today, which is the only reason they were never damaged.
        /// They go through the same function, so they get the same refusal.
        /// </summary>
        [Fact]
        public void ReplaceSection_AcceptanceCriteriaTextHoldsASiblingLevelHeading_IsRefused()
        {
            Assert.Throws<ArgumentException>(
                () => ReplaceCriteria(Body, "## Must have\n\n- [ ] something measurable"));
        }

        [Fact]
        public void ReplaceSection_TextHoldsAShallowerHeading_IsRefused()
        {
            Assert.Throws<ArgumentException>(() => Replace(Body, "# Problem\n\nThe thing that is wrong."));
        }

        /// <summary>
        /// The workaround the rule points at, and the shape every surviving description already
        /// uses. A deeper heading is inside the section, so it replaces cleanly however often it
        /// is written.
        /// </summary>
        [Fact]
        public void ReplaceSection_TextHoldsADeeperHeading_ReplacesTheSection()
        {
            var text = "### Problem\n\nThe thing that is wrong.\n\n#### Detail\n\nMore.";

            var rewritten = Replace(Body, text);

            Assert.Contains("## Description\n\n" + text + "\n", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("The original description.", rewritten, StringComparison.Ordinal);
        }

        /// <summary>
        /// The acceptance criterion the ticket asked for: the same body written three times leaves
        /// the document identical after the second write and the third. Under the fault the third
        /// write held three copies of the description.
        /// </summary>
        [Fact]
        public void ReplaceSection_SameTextWrittenThreeTimes_IsUnchangedAfterTheSecondWrite()
        {
            var text = "### Problem\n\nThe thing that is wrong.";

            var once = Replace(Body, text);
            var twice = Replace(once, text);
            var thrice = Replace(twice, text);

            Assert.Equal(twice, thrice);
            Assert.Equal(once, twice);
        }

        /// <summary>
        /// Reading is the counterpart to writing and shares one definition of where a section
        /// ends, so a description that opens with a subheading comes back whole rather than empty.
        /// An empty section here is what made the fault read as "this ticket has no description".
        /// </summary>
        [Fact]
        public void SectionOf_DescriptionOpensWithASubheading_ReturnsTheWholeSection()
        {
            var rewritten = Replace(Body, "### Problem\n\nThe thing that is wrong.");

            var section = TicketDocument.SectionOf(rewritten, TicketDocument.DescriptionHeading);

            Assert.NotNull(section);
            Assert.Contains("### Problem", section, StringComparison.Ordinal);
            Assert.Contains("The thing that is wrong.", section, StringComparison.Ordinal);
            Assert.DoesNotContain("## Acceptance Criteria", section, StringComparison.Ordinal);
        }

        [Fact]
        public void RequireSectionBody_TextHoldsNoHeading_IsAccepted()
        {
            TicketDocument.RequireSectionBody("Just prose, and a # that is not a heading.", TicketDocument.DescriptionHeading);
        }

        /// <summary>
        /// A document damaged before the rule shipped: two Description sections, the stale one out
        /// of reach of every write. The index is correct throughout, so this is the only thing
        /// that can report it.
        /// </summary>
        [Fact]
        public void RepeatedSections_BodyHoldsTwoDescriptions_ReportsTheHeadingAndTheCount()
        {
            var damaged =
                "## Description\n\nThe current text.\n\n" +
                "## Description\n\nThe stale text.\n\n" +
                "## Activity Log\n\n- 2026-08-07 09:00 — created\n";

            var repeated = TicketDocument.RepeatedSections(damaged);

            Assert.Equal(2, repeated[TicketDocument.DescriptionHeading]);
        }

        [Fact]
        public void RepeatedSections_EverySectionAppearsOnce_ReportsNothing()
        {
            Assert.Empty(TicketDocument.RepeatedSections(Body));
        }

        /// <summary>
        /// Subheadings repeat legitimately — two sections may each have a `### Problem` — so only
        /// the level the document's sections are written at is counted.
        /// </summary>
        [Fact]
        public void RepeatedSections_SubheadingRepeatsUnderDifferentSections_ReportsNothing()
        {
            var body =
                "## Description\n\n### Problem\n\nOne.\n\n" +
                "## Notes\n\n### Problem\n\nAnother.\n";

            Assert.Empty(TicketDocument.RepeatedSections(body));
        }
    }
}

using Noogen.Backlog.Verbs;

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// Help written by hand describes what somebody believed the code accepted. Generated from the
    /// catalog it cannot name a flag no verb reads, and cannot miss one that was added — so what
    /// these pin is that property, not the wording.
    ///
    /// It also has a second job now. Over MCP the help *is* the disclosure: a caller learns the
    /// surface by asking for it rather than by carrying it, so an omission there is not a cosmetic
    /// problem, it is a verb nobody can find.
    /// </summary>
    public class VerbHelpTests
    {
        [Fact]
        public void Write_CommandLineSurface_NamesEveryVerbItOffers()
        {
            var help = VerbHelp.Write(VerbSurface.Cli);

            foreach (var verb in VerbCatalog.On(VerbSurface.Cli))
                Assert.Contains(verb.Name, help, StringComparison.Ordinal);
        }

        [Fact]
        public void Write_CommandLineSurface_NamesEveryOptionEveryVerbReads()
        {
            foreach (var verb in VerbCatalog.On(VerbSurface.Cli))
            {
                var help = VerbHelp.Write(verb.Name, VerbSurface.Cli);

                foreach (var option in verb.OptionsOn(VerbSurface.Cli))
                    Assert.Contains("--" + option.Name, help, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Write_McpSurface_LeavesOutTheVerbsThatNeedThisMachine()
        {
            var help = VerbHelp.Write(VerbSurface.Mcp);

            Assert.DoesNotContain("  login ", help, StringComparison.Ordinal);
            Assert.DoesNotContain("--json", help, StringComparison.Ordinal);
        }

        /// <summary>
        /// A verb that is simply absent reads as a gap in the tool. Naming it and saying why turns
        /// "this cannot be done" into "this is done somewhere else", which is the true answer.
        /// </summary>
        [Fact]
        public void Write_McpSurface_SaysWhyTheWithheldVerbsAreWithheld()
        {
            var help = VerbHelp.Write(VerbSurface.Mcp);

            Assert.Contains("NOT OFFERED HERE", help, StringComparison.Ordinal);
            Assert.Contains(VerbCatalog.Require("login").McpRefusal!, help, StringComparison.Ordinal);
        }

        [Fact]
        public void Write_WithheldVerbAskedForByName_AnswersWithTheReasonRatherThanUsage()
        {
            var help = VerbHelp.Write("login", VerbSurface.Mcp);

            Assert.Contains("not available here", help, StringComparison.Ordinal);
            Assert.DoesNotContain("usage:", help, StringComparison.Ordinal);
        }

        [Fact]
        public void Write_OneVerb_CarriesItsSummaryUsageAndEveryOptionDescription()
        {
            var help = VerbHelp.Write("new", VerbSurface.Cli);

            Assert.Contains(VerbCatalog.Require("new").Summary, help, StringComparison.Ordinal);
            Assert.Contains("backlog new --title <value>", help, StringComparison.Ordinal);

            foreach (var option in VerbCatalog.Require("new").Options)
                Assert.Contains(option.Description, help, StringComparison.Ordinal);
        }

        [Fact]
        public void Write_OneVerb_MarksTheRequiredOptions()
        {
            Assert.Contains("(required)", VerbHelp.Write("block", VerbSurface.Cli), StringComparison.Ordinal);
        }

        [Fact]
        public void Write_ScoreVerb_NamesBothSpellingsOfEachDimension()
        {
            var help = VerbHelp.Write("score", VerbSurface.Cli);

            Assert.Contains("--bv", help, StringComparison.Ordinal);
            Assert.Contains("--business-value", help, StringComparison.Ordinal);
        }

        /// <summary>
        /// The file and stdin spellings exist because a shell damages a quoted value. Over MCP
        /// prose is an ordinary JSON string, so offering them there would be describing a path that
        /// does not exist.
        /// </summary>
        [Fact]
        public void Write_ProseVerb_OffersTheFileSpellingOnlyOnTheCommandLine()
        {
            Assert.Contains("--<name>-file", VerbHelp.Write("new", VerbSurface.Cli), StringComparison.Ordinal);
            Assert.DoesNotContain("-file", VerbHelp.Write("new", VerbSurface.Mcp), StringComparison.Ordinal);
        }

        /// <summary>
        /// Usage and the option list are two halves of one answer, so they have to spell an option
        /// the same way. Over MCP a name is a key of one object: two dashes would teach a spelling
        /// this surface refuses, and bare names in a row would read as positional arguments.
        /// </summary>
        [Fact]
        public void Write_McpSurface_SpellsAVerbAsTheObjectItIsCalledWith()
        {
            Assert.Equal(
                "new {title, type?, area?, owner?, description?, acceptance-criteria?, bv?, tc?, rroe?, size?}",
                VerbCatalog.Require("new").Usage(VerbSurface.Mcp));

            Assert.Equal("block {id, reason}", VerbCatalog.Require("block").Usage(VerbSurface.Mcp));
            Assert.Equal("show {id, section?, full?}", VerbCatalog.Require("show").Usage(VerbSurface.Mcp));

            Assert.DoesNotContain("--", VerbHelp.Write("new", VerbSurface.Mcp), StringComparison.Ordinal);
        }

        /// <summary>A verb that reads nothing is its own call; `whoami {}` would invite an argument.</summary>
        [Fact]
        public void Write_McpVerbThatReadsNothing_IsJustItsName() =>
            Assert.Equal("whoami", VerbCatalog.Require("whoami").Usage(VerbSurface.Mcp));

        /// <summary>
        /// On a command line a valueless option is present or absent. In an object it is a value,
        /// and nothing else on the page says which one to write.
        /// </summary>
        [Fact]
        public void Write_McpValuelessOption_SaysItIsABoolean()
        {
            Assert.Contains("(true or false)", VerbHelp.Write("show", VerbSurface.Mcp), StringComparison.Ordinal);
            Assert.DoesNotContain("(true or false)", VerbHelp.Write("show", VerbSurface.Cli), StringComparison.Ordinal);
        }

        /// <summary>
        /// There is no argument position over MCP: a positional arrives under `options` with
        /// everything else, so it is described as one rather than shown apart as `&lt;id&gt;`.
        /// </summary>
        [Fact]
        public void Write_McpVerbWithAPositional_DescribesItAsAnOptionOfItsOwnName()
        {
            var help = VerbHelp.Write("show", VerbSurface.Mcp);

            Assert.Contains("  id       a ticket id  (required)", help, StringComparison.Ordinal);
            Assert.DoesNotContain("  <id>", help, StringComparison.Ordinal);
        }

        /// <summary>`help` answers about the whole surface when it is given nothing, and usage says so.</summary>
        [Fact]
        public void Write_HelpItself_ShowsItsVerbAsOptional()
        {
            Assert.StartsWith("help [<verb>]", VerbCatalog.Require("help").Usage(VerbSurface.Cli), StringComparison.Ordinal);
        }

        [Fact]
        public void Write_VerbThatDoesNotExist_RefusesAndPointsAtHelp()
        {
            var exception = Assert.Throws<UsageException>(() => VerbHelp.Write("frobnicate"));

            Assert.Contains("frobnicate", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The catalog holds prose as prose, so whatever renders it decides the width. A terminal
        /// has one; the note is unreadable without it.
        /// </summary>
        [Fact]
        public void Write_GroupWithALongNote_WrapsItRatherThanEmittingOneLine()
        {
            var longest = VerbHelp.Write(VerbSurface.Cli).Split('\n').Max(line => line.TrimEnd().Length);

            Assert.True(longest <= 120, $"A help line was {longest} characters.");
        }
    }
}

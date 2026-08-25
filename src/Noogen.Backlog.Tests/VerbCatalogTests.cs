using Noogen.Backlog.Cli;
using Noogen.Backlog.Verbs;

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// The catalog is now the only description of the surface: the parser reads it, validation
    /// reads it, the help is written from it, and the MCP server will hand it back to a caller who
    /// asked how to use a verb. That concentration is the point — but it also means a gap here is
    /// a gap in all four, so what is pinned below is the agreement between them.
    /// </summary>
    public class VerbCatalogTests
    {
        [Fact]
        public void All_EveryVerb_HasASummaryAndAGroup()
        {
            foreach (var verb in VerbCatalog.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(verb.Summary), $"'{verb.Name}' has no summary.");
                Assert.False(string.IsNullOrWhiteSpace(verb.Group), $"'{verb.Name}' has no group.");
            }
        }

        /// <summary>
        /// An option with no description would print a blank line in the help, which reads as
        /// "this flag does nothing" rather than "nobody wrote this down".
        /// </summary>
        [Fact]
        public void All_EveryOption_HasADescription()
        {
            foreach (var verb in VerbCatalog.All)
            {
                foreach (var option in verb.Options)
                    Assert.False(string.IsNullOrWhiteSpace(option.Description), $"'{verb.Name} --{option.Name}' has no description.");
            }
        }

        [Fact]
        public void All_EveryGroupNamed_HasAHeadingToPrintUnder()
        {
            var headings = VerbCatalog.Groups.Select(group => group.Title).ToHashSet(StringComparer.Ordinal);

            foreach (var verb in VerbCatalog.All)
                Assert.Contains(verb.Group, headings, StringComparer.Ordinal);
        }

        /// <summary>
        /// Every verb the catalog declares has to survive the parser, or the surface describes
        /// something the tool refuses to run.
        /// </summary>
        [Fact]
        public void Validate_EveryVerbTheCatalogOffers_IsAcceptedOnACommandLine()
        {
            foreach (var verb in VerbCatalog.On(VerbSurface.Cli))
                CommandLineRules.Validate(CommandLine.Parse([verb.Name]));
        }

        [Fact]
        public void Validate_VerbTheCatalogDoesNotHave_IsRefused()
        {
            var exception = Assert.Throws<UsageException>(() => CommandLineRules.Validate(CommandLine.Parse(["frobnicate"])));

            Assert.Contains("frobnicate", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The `-file` spelling is derived from the option's own name rather than listed beside it,
        /// because <see cref="TextInput"/> derives it the same way. A name spelled separately in
        /// the two places could declare a flag the reader never looks at — the silent no-op the
        /// table exists to prevent.
        /// </summary>
        [Fact]
        public void Accepts_EveryProseOption_ReadsBothItsSpellings()
        {
            foreach (var verb in VerbCatalog.All)
            {
                foreach (var option in verb.Options.Where(option => option.IsProse))
                {
                    Assert.True(CommandLineRules.Accepts(verb.Name, option.Name), $"'{verb.Name}' does not read --{option.Name}.");
                    Assert.True(CommandLineRules.Accepts(verb.Name, option.FileName), $"'{verb.Name}' does not read --{option.FileName}.");
                }
            }
        }

        [Fact]
        public void Accepts_EveryScoreAlias_IsReadWhereTheShortNameIs()
        {
            foreach (var verb in VerbCatalog.All)
            {
                foreach (var option in verb.Options.Where(option => option.Alias is not null))
                    Assert.True(CommandLineRules.Accepts(verb.Name, option.Alias!), $"'{verb.Name}' does not read --{option.Alias}.");
            }
        }

        /// <summary>
        /// Shape is a property of the name, not of the verb: a name that took a value on one verb
        /// and none on another would be a trap for anyone reading a command line.
        /// </summary>
        [Fact]
        public void TakesValue_NameUsedByMoreThanOneVerb_HasTheSameShapeEverywhere()
        {
            var shapes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var verb in VerbCatalog.All)
            {
                foreach (var option in verb.Options)
                {
                    if (shapes.TryGetValue(option.Name, out var takesValue))
                        Assert.True(takesValue == option.TakesValue, $"--{option.Name} has two shapes.");
                    else
                        shapes[option.Name] = option.TakesValue;
                }
            }
        }

        // --- surfaces ---

        [Theory]
        [InlineData("login")]
        [InlineData("logout")]
        [InlineData("init")]
        [InlineData("install-skill")]
        public void On_VerbThatNeedsThisMachine_IsNotOfferedOverMcpAndSaysWhy(string name)
        {
            var verb = VerbCatalog.Require(name);

            Assert.False(verb.OfferedOn(VerbSurface.Mcp));
            Assert.False(string.IsNullOrWhiteSpace(verb.McpRefusal), $"'{name}' is withheld without saying why.");
        }

        [Fact]
        public void On_EveryVerbWithheldFromASurface_SaysWhy()
        {
            foreach (var verb in VerbCatalog.All.Where(verb => !verb.OfferedOn(VerbSurface.Mcp)))
                Assert.NotNull(verb.McpRefusal);
        }

        /// <summary>
        /// Invariant 13: the machine contract is always UTC and does not move with a display
        /// setting. Over MCP there is no terminal to render for and no second representation to
        /// offer, so neither modifier exists there.
        /// </summary>
        [Fact]
        public void Modifiers_JsonAndUtc_AreOfferedOnlyOnTheCommandLine()
        {
            foreach (var modifier in VerbCatalog.Modifiers)
                Assert.Equal(VerbSurface.Cli, modifier.Surfaces);
        }

        [Fact]
        public void On_TheLifecycleVerbs_AreAllOfferedOverMcp()
        {
            var transitions = VerbCatalog.On(VerbSurface.Mcp).Select(verb => verb.Name).ToList();

            Assert.Equal(
                ["start", "block", "unblock", "review", "archive", "restore"],
                transitions.Where(name => name is "start" or "block" or "unblock" or "review" or "archive" or "restore").ToList());
        }

        // --- usage ---

        [Fact]
        public void Usage_VerbWithARequiredOption_PutsItBeforeTheOptionalOnes()
        {
            var usage = VerbCatalog.Require("new").Usage(VerbSurface.Cli);

            Assert.StartsWith("new --title <value>", usage, StringComparison.Ordinal);
        }

        /// <summary>
        /// The positional is named rather than cut out of its description: `find` reads "some text
        /// to search for", whose last word is "for".
        /// </summary>
        [Fact]
        public void Usage_VerbWithAPositional_NamesItInOneWord()
        {
            Assert.StartsWith("find <text>", VerbCatalog.Require("find").Usage(VerbSurface.Cli), StringComparison.Ordinal);
            Assert.StartsWith("show <id>", VerbCatalog.Require("show").Usage(VerbSurface.Cli), StringComparison.Ordinal);
        }

        [Fact]
        public void Usage_ValuelessFlag_TakesNoPlaceholder()
        {
            Assert.Contains("[--full]", VerbCatalog.Require("show").Usage(VerbSurface.Cli), StringComparison.Ordinal);
        }
    }
}

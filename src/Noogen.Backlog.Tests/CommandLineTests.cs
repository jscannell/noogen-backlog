using Noogen.Backlog.Cli;
using Noogen.Backlog.Verbs;

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// NG-0058: the parser used to decide whether `--name` took a value by looking at the argument
    /// after it. Nothing declared which of the two a name was meant to be, so every wrong guess
    /// failed quietly — the same class of defect as NG-0045, and the reason that one survived
    /// eleven tickets. <see cref="VerbCatalog"/> declares the shape now, and these pin what follows from
    /// declaring it.
    /// </summary>
    public class CommandLineTests
    {
        static UsageException Reject(params string[] args) =>
            Assert.Throws<UsageException>(() => CommandLine.Parse(args));

        // --- a valueless flag never consumes what follows it ---

        /// <summary>
        /// The headline case. `--json` used to bind `extra` as its value, so the flag went unset:
        /// human output was printed under the machine contract, and the stray positional that
        /// NG-0045's check exists to refuse was never seen as a positional at all.
        /// </summary>
        [Fact]
        public void Parse_FlagFollowedByAnotherArgument_KeepsTheFlagAndTheArgument()
        {
            var command = CommandLine.Parse(["doctor", "--json", "extra"]);

            Assert.True(command.Json);
            Assert.Equal(["extra"], command.Positionals);
        }

        [Fact]
        public void Parse_FlagBeforeAPositionalTheVerbReads_LeavesTheTicketIdInPlace()
        {
            var command = CommandLine.Parse(["start", "--force", "NG-12"]);

            Assert.True(command.HasFlag("force"));
            Assert.Equal("NG-12", command.RequirePositional(0, "a ticket id"));
        }

        [Fact]
        public void Parse_UtcFlag_DoesNotSwallowTheFollowingArgument()
        {
            var command = CommandLine.Parse(["show", "--utc", "NG-12"]);

            Assert.True(command.HasFlag("utc"));
            Assert.Equal(["NG-12"], command.Positionals);
        }

        /// <summary>
        /// An undeclared name consumes nothing either, so the argument behind it survives for
        /// <see cref="CommandLineRules.Validate"/> to report. Both mistakes are then visible at once.
        /// </summary>
        [Fact]
        public void Parse_UndeclaredOption_DoesNotConsumeTheNextArgument()
        {
            var command = CommandLine.Parse(["edit", "NG-12", "--titel", "Typo"]);

            Assert.Equal(["NG-12", "Typo"], command.Positionals);
            Assert.Equal(["titel"], command.Names);
        }

        // --- an option declared to take a value must be given one ---

        [Fact]
        public void Parse_OptionWithNothingAfterIt_RefusesAndNamesTheOption()
        {
            var exception = Reject("edit", "NG-12", "--title");

            Assert.Contains("--title takes a value", exception.Message, StringComparison.Ordinal);
            Assert.Contains("nothing follows it", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// `edit --description` was special-cased into an error for exactly this reason. Every
        /// other value-taking option had the same hole and no such guard; now the parser answers
        /// for all of them and <c>TextInput</c> carries no special case.
        /// </summary>
        [Theory]
        [InlineData("edit", "NG-12", "--description")]
        [InlineData("edit", "NG-12", "--area")]
        [InlineData("note", "NG-12", "--text")]
        [InlineData("block", "NG-12", "--reason")]
        [InlineData("archive", "NG-12", "--as")]
        [InlineData("score", "NG-12", "--bv")]
        [InlineData("init", "--drive")]
        public void Parse_ValueTakingOptionWithNoValue_Refuses(params string[] args) => Reject(args);

        /// <summary>
        /// The one thing an option will not take as a value is another option this verb reads.
        /// Binding it would put `--json` back where this change found it: silently unset, this
        /// time as the text of a note.
        /// </summary>
        [Fact]
        public void Parse_OptionFollowedByAnotherOptionOfTheSameVerb_RefusesRatherThanBindingIt()
        {
            var exception = Reject("note", "NG-12", "--text", "--json");

            Assert.Contains("--text takes a value", exception.Message, StringComparison.Ordinal);
            Assert.Contains("--text=<value>", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_OptionFollowedByTheEndOfOptionsMarker_Refuses()
        {
            var exception = Reject("edit", "NG-12", "--title", "--");

            Assert.Contains("ends the options", exception.Message, StringComparison.Ordinal);
        }

        // --- a value may begin with two dashes ---

        /// <summary>
        /// `--title "--draft"` used to parse as a flag `title` and an unknown option `draft`, and
        /// the equals form was the only way through with nothing saying so.
        /// </summary>
        [Fact]
        public void Parse_ValueBeginningWithTwoDashes_IsTakenAsTheValue()
        {
            var command = CommandLine.Parse(["edit", "NG-12", "--title", "--draft"]);

            Assert.Equal("--draft", command.Option("title"));
            Assert.Equal(["title"], command.Names);
        }

        [Fact]
        public void Parse_EqualsForm_PassesAValueThatLooksLikeAnOptionOfThisVerb()
        {
            var command = CommandLine.Parse(["note", "NG-12", "--text=--json"]);

            Assert.Equal("--json", command.Option("text"));
            Assert.False(command.Json);
        }

        [Fact]
        public void Parse_EqualsFormOnAValuelessFlag_RefusesInsteadOfSettingSomethingNothingReads()
        {
            var exception = Reject("start", "NG-12", "--force=true");

            Assert.Contains("--force carries no value", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_EndOfOptionsMarker_MakesEverythingAfterItPositional()
        {
            var command = CommandLine.Parse(["show", "--json", "--", "--NG-12"]);

            Assert.True(command.Json);
            Assert.Equal(["--NG-12"], command.Positionals);
            Assert.Equal(["json"], command.Names);
        }

        // --- what did not change ---

        [Fact]
        public void Parse_OrdinaryOptionsAndPositionals_AreUnchanged()
        {
            var command = CommandLine.Parse(["edit", "NG-12", "--title", "Renamed", "--area=cli", "--JSON"]);

            Assert.Equal("edit", command.Verb);
            Assert.Equal(["NG-12"], command.Positionals);
            Assert.Equal("Renamed", command.Option("title"));
            Assert.Equal("cli", command.Option("area"));
            Assert.True(command.Json);
        }

        [Fact]
        public void Parse_NoArguments_IsHelp()
        {
            var command = CommandLine.Parse([]);

            Assert.Equal("help", command.Verb);
            Assert.Empty(command.Positionals);
        }

        /// <summary>
        /// An unknown verb declares nothing, so nothing consumes anything and the line survives
        /// intact for <see cref="CommandLineRules.Validate"/> to report as an unknown command.
        /// </summary>
        [Fact]
        public void Parse_UnknownVerb_ParsesWithoutRefusingTheOptions()
        {
            var command = CommandLine.Parse(["frobnicate", "--title", "x"]);

            Assert.Equal("frobnicate", command.Verb);
            Assert.Contains("title", command.Names);
        }

        // --- the JSON contract survives a line too broken to parse ---

        [Theory]
        [InlineData("edit", "NG-12", "--title", "--json")]
        [InlineData("doctor", "--json=yes")]
        [InlineData("note", "NG-12", "--JSON", "--text")]
        public void WantsJson_ALineParseRefuses_StillAsksForJson(params string[] args)
        {
            Reject(args);

            Assert.True(CommandLine.WantsJson(args));
        }

        [Fact]
        public void WantsJson_WithoutTheFlag_IsFalse() =>
            Assert.False(CommandLine.WantsJson(["edit", "NG-12", "--title", "json"]));
    }
}

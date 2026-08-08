using Noogen.Backlog.Cli;

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// The parser accepts any `--name`, so until a verb declares what it reads, an option nothing
    /// reads is silently dropped and the command still reports success. That is how
    /// `edit --description "..."` printed "Updated NG-12." without changing anything.
    /// </summary>
    public class VerbsTests
    {
        static UsageException Reject(params string[] args) =>
            Assert.Throws<UsageException>(() => Verbs.Validate(CommandLine.Parse(args)));

        static void Accept(params string[] args) => Verbs.Validate(CommandLine.Parse(args));

        [Fact]
        public void Validate_EditWithDescription_IsAccepted() =>
            Accept("edit", "NG-12", "--description", "a new description");

        [Fact]
        public void Validate_DescriptionOnAVerbThatDoesNotTakeIt_Refuses()
        {
            var exception = Reject("score", "NG-12", "--description", "x");

            Assert.Contains("'score' does not accept --description", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_MistypedOption_RefusesAndListsWhatTheVerbAccepts()
        {
            var exception = Reject("edit", "NG-12", "--titel", "Typo");

            Assert.Contains("'edit' does not accept --titel", exception.Message, StringComparison.Ordinal);
            Assert.Contains("--title", exception.Message, StringComparison.Ordinal);
            Assert.Contains("--json", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>An option with no value parses as a flag, and must be checked the same way.</summary>
        [Fact]
        public void Validate_ValuelessFlagFromAnotherVerb_Refuses()
        {
            var exception = Reject("edit", "NG-12", "--force");

            Assert.Contains("does not accept --force", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_SeveralUnknownOptions_NamesTheOneTypedFirst()
        {
            var exception = Reject("edit", "NG-12", "--aera", "x", "--titel", "y");

            Assert.Contains("--aera", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("--titel", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("edit", "NG-12", "--status", "done")]
        [InlineData("edit", "NG-12", "--phase", "archive")]
        [InlineData("new", "--title", "x", "--status", "done")]
        public void Validate_StatusOrPhase_PointsAtTheLifecycleVerbs(params string[] args)
        {
            var exception = Reject(args);

            Assert.Contains("the tab a ticket lives on is its state", exception.Message, StringComparison.Ordinal);
            Assert.Contains("backlog start", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_OptionThisVerbDoesNotRead_RefusesEvenThoughAnotherVerbReadsIt()
        {
            // `whoami` deliberately reports the *configured* account, so --account there was a
            // silent no-op in the same way --description was.
            Assert.Throws<UsageException>(() => Verbs.Validate(CommandLine.Parse(["whoami", "--account", "someone@noogen.ai"])));
            Accept("login", "--account", "someone@noogen.ai");
        }

        [Fact]
        public void Validate_UnknownVerb_NamesTheVerbAndPointsAtHelp()
        {
            var exception = Reject("frobnicate", "--json");

            Assert.Contains("Unknown command 'frobnicate'", exception.Message, StringComparison.Ordinal);
            Assert.Contains("backlog help", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The invocations the README, the help text and the skill teach. If one of these ever
        /// fails, the tool and its own documentation have parted company.
        /// </summary>
        [Theory]
        [InlineData("list", "--area", "cli", "--owner", "me", "--top", "5")]
        [InlineData("next", "--owner", "me")]
        [InlineData("wip", "--owner", "me")]
        [InlineData("show", "NG-12")]
        [InlineData("flow", "--since", "90d")]
        [InlineData("new", "--title", "x", "--type", "bug", "--area", "cli", "--owner", "me", "--description", "why")]
        [InlineData("new", "--title", "x", "--bv", "5", "--tc", "3", "--rroe", "2", "--size", "1")]
        [InlineData("edit", "NG-12", "--title", "x", "--area", "cli", "--owner", "me", "--type", "chore", "--description", "why")]
        [InlineData("score", "NG-12", "--bv", "5", "--tc", "3", "--rroe", "2", "--size", "1")]
        [InlineData("score", "NG-12", "--business-value", "5", "--time-criticality", "3", "--risk-opportunity", "2", "--job-size", "1")]
        [InlineData("note", "NG-12", "--text", "a note")]
        [InlineData("start", "NG-12", "--owner", "me", "--force")]
        [InlineData("block", "NG-12", "--reason", "waiting")]
        [InlineData("unblock", "NG-12")]
        [InlineData("review", "NG-12")]
        [InlineData("archive", "NG-12", "--as", "done", "--note", "shipped")]
        [InlineData("restore", "NG-12")]
        [InlineData("init", "--drive", "abc", "--timezone", "America/New_York")]
        [InlineData("install-skill", "--path", "somewhere", "--force")]
        [InlineData("login", "--account", "someone@noogen.ai")]
        [InlineData("logout", "--account", "someone@noogen.ai")]
        [InlineData("whoami")]
        [InlineData("doctor")]
        [InlineData("reindex")]
        public void Validate_DocumentedInvocation_IsAccepted(params string[] args) => Accept(args);

        /// <summary>
        /// "Every command accepts --json" is the agent contract, and --utc is its human-output
        /// counterpart. Doubling as the census that every dispatched verb is declared at all.
        /// </summary>
        [Theory]
        [InlineData("login")]
        [InlineData("logout")]
        [InlineData("whoami")]
        [InlineData("init")]
        [InlineData("install-skill")]
        [InlineData("list")]
        [InlineData("next")]
        [InlineData("wip")]
        [InlineData("flow")]
        [InlineData("show")]
        [InlineData("new")]
        [InlineData("edit")]
        [InlineData("score")]
        [InlineData("note")]
        [InlineData("start")]
        [InlineData("block")]
        [InlineData("unblock")]
        [InlineData("review")]
        [InlineData("archive")]
        [InlineData("restore")]
        [InlineData("reindex")]
        [InlineData("doctor")]
        public void Validate_JsonAndUtc_AreAcceptedOnEveryVerb(string verb) => Accept(verb, "--json", "--utc");

        [Theory]
        [InlineData("edit", "NG-12", "--TITLE", "x")]
        [InlineData("edit", "NG-12", "--title=x")]
        [InlineData("score", "NG-12", "--bv=5")]
        public void Validate_CasingAndEqualsForm_AreAccepted(params string[] args) => Accept(args);

        [Fact]
        public void Validate_EqualsFormWithUnknownName_Refuses()
        {
            var exception = Reject("edit", "NG-12", "--descriptoin=new text");

            Assert.Contains("does not accept --descriptoin", exception.Message, StringComparison.Ordinal);
        }

        // --- positionals ---
        //
        // The shape NG-0045 was filed for. PowerShell does not escape a double quote inside an
        // argument it quotes, so a description containing one is torn apart and its tail arrives
        // as extra positional arguments. They used to be dropped: the ticket was created with a
        // truncated description, and the command exited 0.

        [Fact]
        public void Validate_ArgumentsLeftOverFromASplitDescription_Refuses()
        {
            var exception = Reject("new", "--title", "probe", "--description", "L1 has ", "quoted", " words END");

            Assert.Contains("'new' takes no positional arguments", exception.Message, StringComparison.Ordinal);
            Assert.Contains("'quoted'", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>Naming the split is the whole value: the fragment alone reads as nonsense.</summary>
        [Fact]
        public void Validate_UnexpectedArgument_ExplainsTheQuotingAndTheSafeInputPaths()
        {
            var exception = Reject("new", "--title", "probe", "stray");

            Assert.Contains("double quote", exception.Message, StringComparison.Ordinal);
            Assert.Contains("--description-file", exception.Message, StringComparison.Ordinal);
            Assert.Contains("--description -", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>A verb with no prose to give has no reason to be told where to put prose.</summary>
        [Fact]
        public void Validate_UnexpectedArgumentOnAVerbWithoutDescription_OmitsTheProseAdvice()
        {
            var exception = Reject("start", "NG-12", "stray");

            Assert.Contains("takes a ticket id and nothing else positional", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("--description", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("show", "NG-12")]
        [InlineData("edit", "NG-12")]
        [InlineData("archive", "NG-12")]
        public void Validate_TheOneTicketIdAVerbReads_IsAccepted(params string[] args) => Accept(args);

        [Theory]
        [InlineData("show", "NG-12", "NG-13")]
        [InlineData("score", "NG-12", "5")]
        [InlineData("list", "backlog")]
        [InlineData("doctor", "extra", "--json")]
        public void Validate_PositionalNoVerbReads_Refuses(params string[] args) => Reject(args);
    }
}

using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// A refusal has to arrive under the same name whichever front end the caller reached. The CLI
    /// prints the kind under `--json` and turns it into an exit code; the MCP server reports the
    /// same kind on a failed tool call. Two ladders would drift, and the drift would be invisible:
    /// both would still report *a* failure, just not the same one.
    /// </summary>
    public class BacklogFaultTests
    {
        [Fact]
        public void KindOf_UsageException_IsUsage() =>
            Assert.Equal("usage", BacklogFault.KindOf(new UsageException("bad flag")));

        [Fact]
        public void KindOf_WipLimitExceeded_IsWipLimit() =>
            Assert.Equal("wip-limit", BacklogFault.KindOf(new WipLimitExceededException("too many", 3, [])));

        [Fact]
        public void KindOf_TransitionRefused_IsIllegalTransition() =>
            Assert.Equal("illegal-transition", BacklogFault.KindOf(new BacklogTransitionException("no")));

        [Fact]
        public void KindOf_TicketNotFound_IsNotFound() =>
            Assert.Equal("not-found", BacklogFault.KindOf(new KeyNotFoundException("No ticket 'NG-1'.")));

        [Fact]
        public void KindOf_NotSignedIn_IsNotSignedIn() =>
            Assert.Equal("not-signed-in", BacklogFault.KindOf(new NotSignedInException()));

        [Fact]
        public void KindOf_OAuthClientMissing_IsNamedApartFromAnInvalidOne()
        {
            Assert.Equal("oauth-client-missing", BacklogFault.KindOf(new OAuthClientNotConfiguredException("path")));
            Assert.Equal("oauth-client-invalid", BacklogFault.KindOf(new OAuthClientInvalidException("oauth.json", "no client_id")));
        }

        /// <summary>
        /// A document that will not parse is a bad file, not a bad request — `doctor` catches this
        /// kind to report the file and carry on with the sweep.
        /// </summary>
        [Fact]
        public void KindOf_MalformedDocument_IsMalformed() =>
            Assert.Equal("malformed", BacklogFault.KindOf(new FormatException("not a heading")));

        [Fact]
        public void KindOf_ArgumentRejected_IsInvalidArgument() =>
            Assert.Equal("invalid-argument", BacklogFault.KindOf(new ArgumentException("out of range")));

        /// <summary>
        /// Anything unrecognised is still named. A caller that cannot be told the cause still has
        /// to be told there was one.
        /// </summary>
        [Fact]
        public void KindOf_SomethingElse_IsError() =>
            Assert.Equal("error", BacklogFault.KindOf(new InvalidOperationException("who knows")));

        /// <summary>
        /// A usage error is refused before the command runs, and a usage error thrown as an
        /// <see cref="ArgumentException"/> would be reported as one — so the more specific type has
        /// to be tested first.
        /// </summary>
        [Fact]
        public void KindOf_UsageException_IsNotMistakenForAnArgumentException() =>
            Assert.NotEqual(BacklogFault.KindOf(new ArgumentException("x")), BacklogFault.KindOf(new UsageException("x")));

        /// <summary>
        /// The useful half of a rate limit is not "it failed" but "nothing was half-written" — a
        /// 429 is a rejection, so there is nothing to check and nothing to undo. Google's own
        /// message says none of that, which is why this one is replaced rather than passed through.
        /// </summary>
        [Fact]
        public void MessageOf_OrdinaryFailure_IsTheExceptionsOwnMessage() =>
            Assert.Equal("No ticket 'NG-1'.", BacklogFault.MessageOf(new KeyNotFoundException("No ticket 'NG-1'.")));

        [Fact]
        public void MessageOf_RateLimited_ExplainsThatNothingWasWritten() =>
            Assert.Contains("Nothing was half-written", BacklogFault.RateLimitedMessage, StringComparison.Ordinal);
    }
}

using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog
{
    /// <summary>
    /// Names the cause of a failure in one word, for a caller that has to react to it rather than
    /// read it.
    ///
    /// It lives here rather than in a front end because there is more than one front end and a
    /// refusal has to arrive under the same name at each. Each maps the kind into its own
    /// vocabulary — the CLI to an exit code, an MCP server to a failed tool call, an HTTP API to a
    /// status code — but the kind itself is the same word, and it is decided once.
    ///
    /// Two ladders would drift, and the drift would be invisible: both would still report *a*
    /// failure, just not the same one.
    /// </summary>
    public static class BacklogFault
    {
        public const string Usage = "usage";
        public const string WipLimit = "wip-limit";
        public const string IllegalTransition = "illegal-transition";
        public const string NotFound = "not-found";
        public const string NotSignedIn = "not-signed-in";
        public const string OAuthClientMissing = "oauth-client-missing";
        public const string OAuthClientInvalid = "oauth-client-invalid";
        public const string RateLimited = "rate-limited";
        public const string InvalidArgument = "invalid-argument";
        public const string Malformed = "malformed";
        public const string Error = "error";

        /// <summary>
        /// What a rate-limited caller is told. Said in full because the useful half is not "it
        /// failed" but "nothing was half-written" — see invariant 19: a 429 is a rejection, so
        /// there is nothing to check and nothing to undo.
        /// </summary>
        public const string RateLimitedMessage =
            "Google is rate limiting requests to this backlog, and the command kept being refused after " +
            "several waits. Nothing was half-written — a rate-limited request is rejected, not applied. " +
            "Wait a minute and run it again; if it persists, someone may be running a large 'reindex' or " +
            "'doctor' against the same backlog.";

        /// <summary>
        /// The kind for <paramref name="exception"/>, in the order the causes are distinguished.
        /// Anything unrecognised is <see cref="Error"/> rather than an escape: a caller that cannot
        /// name the cause still has to be told there was one.
        /// </summary>
        public static string KindOf(Exception exception) => exception switch
        {
            UsageException => Usage,
            WipLimitExceededException => WipLimit,
            BacklogTransitionException => IllegalTransition,
            KeyNotFoundException => NotFound,
            NotSignedInException => NotSignedIn,
            OAuthClientNotConfiguredException => OAuthClientMissing,
            OAuthClientInvalidException => OAuthClientInvalid,
            _ when GoogleRateLimit.IsRateLimited(exception) => RateLimited,
            ArgumentException => InvalidArgument,
            FormatException => Malformed,
            _ => Error
        };

        /// <summary>
        /// What to tell the caller. Identical to the exception's own message except for a rate
        /// limit, where the exception says what Google said and the caller needs to be told what
        /// it means for the write they just attempted.
        /// </summary>
        public static string MessageOf(Exception exception) =>
            KindOf(exception) == RateLimited ? RateLimitedMessage : exception.Message;
    }

    /// <summary>
    /// Raised when a caller asked for something the surface does not offer — an option a verb does
    /// not read, a required value left out, a verb that does not exist.
    ///
    /// Distinct from <see cref="ArgumentException"/> on purpose: this one is answerable by reading
    /// the message and asking again, which is what makes it worth a name of its own on the wire.
    /// </summary>
    public class UsageException : Exception
    {
        public UsageException(string message) : base(message)
        {
        }
    }
}

using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Mcp
{
    /// <summary>
    /// Says out loud that the server is waiting on Google rather than hung.
    ///
    /// The CLI writes the same thing to stderr, where the person who typed the command is looking.
    /// Nobody is looking at a server, so it goes to the log — and it goes somewhere, because a
    /// silent eight-second pause is the one thing that makes a working system look broken. What it
    /// must not do is reach the caller: a retry is not part of the answer, and a rate limit that is
    /// eventually served is not a failure to report.
    /// </summary>
    public class LoggingRetryListener : IRetryListener
    {
        readonly ILogger _log;

        public LoggingRetryListener(ILogger log)
        {
            _log = log;
        }

        public void RateLimited(int attempt, int maxAttempts, TimeSpan delay) =>
            _log.LogWarning(
                "Google is rate limiting requests to this backlog; waiting {Seconds:0.#}s before retry {Attempt} of {Retries}.",
                delay.TotalSeconds,
                attempt,
                maxAttempts - 1);
    }
}

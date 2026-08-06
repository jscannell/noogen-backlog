using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;

namespace Noogen.Providers.GoogleWorkspace
{
    public enum CredentialSource
    {
        None,
        ServiceAccountKey,
        UserOAuth,
        ApplicationDefault
    }

    public class ResolvedCredential
    {
        public IConfigurableHttpClientInitializer Initializer { get; set; } = null!;

        public CredentialSource Source { get; set; }

        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Decides which credential the CLI runs as.
    ///
    /// The ordering reflects who each source is for. An explicitly configured service-account key
    /// is deliberate and unambiguous, so it wins — that is the CI and automation path. A signed-in
    /// user comes next, which is the case for a person at a keyboard. Application Default
    /// Credentials come last and are never volunteered: on a workstation ADC is machine-global and
    /// usually belongs to something else entirely, so silently borrowing it would be surprising.
    /// It exists in the chain for Workload Identity inside GKE, where it is the correct and only
    /// answer, and where the platform agent will eventually consume this library.
    /// </summary>
    public class GoogleCredentialResolver
    {
        readonly UserCredentialStore _users;
        readonly string? _serviceAccountKeyPath;
        readonly bool _allowApplicationDefault;

        public GoogleCredentialResolver(UserCredentialStore users, string? serviceAccountKeyPath, bool allowApplicationDefault = true)
        {
            _users = users;
            _serviceAccountKeyPath = serviceAccountKeyPath;
            _allowApplicationDefault = allowApplicationDefault;
        }

        /// <summary>The decision, isolated from any I/O so it can be tested directly.</summary>
        public static CredentialSource Choose(bool hasServiceAccountKey, bool hasUserToken, bool allowApplicationDefault)
        {
            if (hasServiceAccountKey)
                return CredentialSource.ServiceAccountKey;

            if (hasUserToken)
                return CredentialSource.UserOAuth;

            return allowApplicationDefault ? CredentialSource.ApplicationDefault : CredentialSource.None;
        }

        public async Task<ResolvedCredential> ResolveAsync(string account, IEnumerable<string> scopes, CancellationToken cancellationToken = default)
        {
            var scopeList = scopes.ToList();

            var hasKey = !string.IsNullOrWhiteSpace(_serviceAccountKeyPath) && File.Exists(_serviceAccountKeyPath);
            var user = hasKey ? null : await _users.TryLoadAsync(account, scopeList, cancellationToken);

            switch (Choose(hasKey, user is not null, _allowApplicationDefault))
            {
                case CredentialSource.ServiceAccountKey:
                    // Pinned to ServiceAccountCredential rather than GoogleCredential.FromFile,
                    // which accepts any credential type in the file — including external-account
                    // configs that can name an arbitrary executable to run for token exchange.
                    // The path comes from an environment variable, so an attacker who can set it
                    // or swap the file would otherwise get code execution. Anything that is not a
                    // service-account key is now rejected.
                    return new ResolvedCredential
                    {
                        Initializer = CredentialFactory
                            .FromFile<ServiceAccountCredential>(_serviceAccountKeyPath!)
                            .ToGoogleCredential()
                            .CreateScoped(scopeList),
                        Source = CredentialSource.ServiceAccountKey,
                        Description = $"service account key at {_serviceAccountKeyPath}"
                    };

                case CredentialSource.UserOAuth:
                    return new ResolvedCredential
                    {
                        Initializer = user!,
                        Source = CredentialSource.UserOAuth,
                        Description = $"signed-in user '{account}' (token protected by {_users.Protector.Description})"
                    };

                case CredentialSource.ApplicationDefault:
                    try
                    {
                        return new ResolvedCredential
                        {
                            Initializer = GoogleCredential.GetApplicationDefault().CreateScoped(scopeList),
                            Source = CredentialSource.ApplicationDefault,
                            Description = "application default credentials"
                        };
                    }
                    catch (Exception)
                    {
                        // ADC being absent is the ordinary case on a workstation, not an error
                        // worth reporting in Google's words. The actionable answer is to sign in.
                        throw new NotSignedInException();
                    }

                default:
                    throw new NotSignedInException();
            }
        }
    }

    public class NotSignedInException : InvalidOperationException
    {
        public NotSignedInException()
            : base(
                "Not signed in. Run 'backlog login' to authenticate with your own Google account.\n" +
                "For CI or a headless machine, point NOOGEN_BACKLOG_CREDENTIALS at a service-account key instead.")
        {
        }
    }
}

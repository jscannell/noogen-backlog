namespace Noogen.Providers.GoogleWorkspace
{
    public static class GoogleWorkspaceScopes
    {
        /// <summary>
        /// Full Drive, deliberately. The narrower <c>drive.file</c> scope only grants access to
        /// files the app itself created, which works for one person and breaks the moment a
        /// second one arrives: they could not read the index or the ticket documents someone
        /// else's install created. A shared backlog needs shared visibility.
        ///
        /// This is a restricted scope, so the OAuth client must be an <b>Internal</b> app in the
        /// Workspace org — internal apps skip Google's verification process entirely.
        /// </summary>
        public const string Drive = "https://www.googleapis.com/auth/drive";

        public const string Spreadsheets = "https://www.googleapis.com/auth/spreadsheets";

        /// <summary>Only so the CLI can tell you which account you are signed in as.</summary>
        public const string OpenId = "openid";

        public const string Email = "email";

        public static readonly IReadOnlyList<string> All = [Drive, Spreadsheets, OpenId, Email];
    }
}

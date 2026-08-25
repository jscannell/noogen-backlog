using System.Text.Json.Nodes;
using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Mcp
{
    /// <summary>
    /// Whose name a write lands under, and whether it is the caller's.
    ///
    /// It answers `whoami`, and its shape is not the CLI's. The CLI reports a person's own setup —
    /// which account is signed in, where the token store is, what protects it, which OAuth client
    /// was found — because the answer to "who am I?" there is about the machine in front of you.
    /// None of that exists on this path: the caller has no credential here, and the one this server
    /// holds is not theirs.
    ///
    /// So it answers the only part a caller can act on, which is who a ticket will be attributed to
    /// if they do not say. <see cref="Source"/> and <see cref="Description"/> are deliberately not
    /// on the wire, and neither is <see cref="SpreadsheetId"/>: the description names a key file's
    /// path on the server's filesystem, or the operator's own address and keystore, and the
    /// spreadsheet id is the handle to somebody's whole backlog. They are logged once at startup
    /// instead, where the person who deployed this is looking.
    ///
    /// The two keys it does emit are what should still be here once callers have identities of
    /// their own: <c>owner</c> becomes theirs and <c>sharedIdentity</c> becomes false. Nothing has
    /// to change shape for that, and nothing here hands one caller another's configuration.
    /// </summary>
    public class ServerIdentity : IBacklogView
    {
        public ServerIdentity(CredentialSource source, string description, string? owner, string spreadsheetId)
        {
            Source = source;
            Description = description;
            Owner = owner;
            SpreadsheetId = spreadsheetId;
        }

        /// <summary>How the credential was found. For the startup log, never for a caller.</summary>
        public CredentialSource Source { get; }

        /// <summary>The resolver's own words, which name a path or an address. Never for a caller.</summary>
        public string Description { get; }

        /// <summary>The owner a write is attributed to when the caller names nobody.</summary>
        public string? Owner { get; }

        /// <summary>Which backlog this server serves. For the startup log, never for a caller.</summary>
        public string SpreadsheetId { get; }

        public JsonNode ToNode(IReadOnlySet<string>? fields) => new JsonObject
        {
            ["owner"] = Owner,

            // Not decoration: it is the reason `owner` is a property of this server rather than a
            // fact about whoever asked, and the reason two agents' work is indistinguishable here.
            ["sharedIdentity"] = true
        };

        /// <summary>
        /// The owner a write lands under. <c>me</c> resolves to this server's own identity, which
        /// is the only "me" there is here — a caller has no identity of their own on this path, so
        /// anyone who wants their name on a ticket has to say it.
        /// </summary>
        public string? ResolveOwner(string? requested)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return Owner;

            return string.Equals(requested, "me", StringComparison.OrdinalIgnoreCase) ? Owner : requested.Trim();
        }

        public string Describe() =>
            $"Writes are attributed to {(string.IsNullOrWhiteSpace(Owner) ? "nobody" : "'" + Owner + "'")} "
            + "unless the call passes an owner of its own. Every caller of this server shares one "
            + "identity, so a ticket does not carry your name unless you put it there.";
    }
}

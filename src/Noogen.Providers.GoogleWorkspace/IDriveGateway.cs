namespace Noogen.Providers.GoogleWorkspace
{
    /// <summary>
    /// The narrow slice of Drive the backlog needs. Exists as an interface so the store can be
    /// tested without a network, and so the surface stays small enough to reason about.
    /// </summary>
    public interface IDriveGateway
    {
        Task<string?> FindChildAsync(string parentId, string name, string? mimeType, CancellationToken cancellationToken = default);

        /// <summary>Immediate children only. Used by doctor to spot documents with no index row.</summary>
        Task<IReadOnlyList<DriveEntry>> ListChildrenAsync(string parentId, string? mimeType, CancellationToken cancellationToken = default);

        /// <summary>
        /// Files whose *indexed content* matches <paramref name="text"/> — the only way to reach
        /// the prose inside a ticket, which the Sheet does not hold.
        ///
        /// Scoped by <paramref name="driveId"/> rather than by parent folder, and that is not a
        /// shortcut: <c>in parents</c> matches immediate children only, and archived documents sit
        /// two levels down under year and quarter, so a parent-scoped query would quietly answer
        /// for active tickets alone. A shared drive is the smallest enclosure that contains the
        /// whole backlog. Passing null searches everything the credential can see, which is what a
        /// backlog rooted in My Drive gets; the caller is expected to intersect the results with
        /// something authoritative either way.
        ///
        /// Three properties of Drive's index that callers have to design around: it matches whole
        /// terms rather than substrings, it is eventually consistent so a just-written document may
        /// not be found, and it indexes the entire document including anything appended to it.
        /// </summary>
        Task<IReadOnlyList<DriveEntry>> SearchTextAsync(string text, string? mimeType, string? driveId, CancellationToken cancellationToken = default);

        /// <summary>
        /// The shared drive a file lives on, or null if it is in My Drive. Used to confine a
        /// <see cref="SearchTextAsync"/> sweep to the backlog's own drive.
        /// </summary>
        Task<string?> GetDriveIdAsync(string fileId, CancellationToken cancellationToken = default);

        Task<string> CreateFolderAsync(string parentId, string name, CancellationToken cancellationToken = default);

        Task<string> CreateSpreadsheetAsync(string parentId, string name, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a native Google Doc from markdown. Drive converts on the way in, so the file
        /// that lands is a Doc — which is what makes its <c>webViewLink</c> a docs.google.com URL
        /// that opens in the editor with the markdown rendered, rather than a Drive preview of
        /// raw text. Markdown stays our wire format; the Doc is how a person reads it.
        /// </summary>
        Task<string> CreateDocAsync(string parentId, string name, string markdown, CancellationToken cancellationToken = default);

        /// <summary>
        /// Exports a Google Doc back to markdown. The counterpart to <see cref="CreateDocAsync"/>:
        /// a Doc has no bytes to download, so this is an export rather than a media read.
        /// </summary>
        Task<string> ReadDocAsync(string fileId, CancellationToken cancellationToken = default);

        /// <summary>Replaces a Google Doc's content from markdown, converting on the way in.</summary>
        Task UpdateDocAsync(string fileId, string markdown, CancellationToken cancellationToken = default);

        /// <summary>Re-parents a file. This is how archiving moves a ticket; nothing is ever trashed.</summary>
        Task MoveAsync(string fileId, string addParentId, string removeParentId, CancellationToken cancellationToken = default);

        Task<string> GetWebViewLinkAsync(string fileId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Drive's own createdTime/modifiedTime. Used instead of duplicating those two into the
        /// ticket document, where a human would have to hand-maintain them. Drive's
        /// modifiedTime is also strictly better than a field we write, because it catches a
        /// person editing the document directly.
        /// </summary>
        Task<DriveFileTimes> GetTimestampsAsync(string fileId, CancellationToken cancellationToken = default);
    }

    public class DriveEntry
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }

    public class DriveFileTimes
    {
        public DateTimeOffset? CreatedTime { get; set; }

        public DateTimeOffset? ModifiedTime { get; set; }
    }
}

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

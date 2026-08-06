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

        Task<string> CreateTextFileAsync(string parentId, string name, string content, string mimeType, CancellationToken cancellationToken = default);

        Task<string> ReadTextFileAsync(string fileId, CancellationToken cancellationToken = default);

        Task UpdateTextFileAsync(string fileId, string content, string mimeType, CancellationToken cancellationToken = default);

        /// <summary>Re-parents a file. This is how archiving moves a ticket; nothing is ever trashed.</summary>
        Task MoveAsync(string fileId, string addParentId, string removeParentId, CancellationToken cancellationToken = default);

        Task<string> GetWebViewLinkAsync(string fileId, CancellationToken cancellationToken = default);
    }

    public class DriveEntry
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }
}

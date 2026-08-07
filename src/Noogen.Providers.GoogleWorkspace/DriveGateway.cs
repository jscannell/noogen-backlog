using System.Text;
using Google.Apis.Drive.v3;

// Drive's metadata type collides with System.IO.File, which ImplicitUsings brings in.
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace Noogen.Providers.GoogleWorkspace
{
    public class DriveGateway : IDriveGateway
    {
        public const string FolderMimeType = "application/vnd.google-apps.folder";
        public const string SpreadsheetMimeType = "application/vnd.google-apps.spreadsheet";

        /// <summary>
        /// The type a ticket document *is* in Drive. Markdown is what we send and what we ask for
        /// back; the stored file is a Doc so that opening it renders.
        /// </summary>
        public const string DocumentMimeType = "application/vnd.google-apps.document";

        /// <summary>The type we upload and export as. Never the type of a stored file.</summary>
        public const string MarkdownMimeType = "text/markdown";

        readonly IDriveClientFactory _factory;

        public DriveGateway(IDriveClientFactory factory)
        {
            _factory = factory;
        }

        DriveService Service => _factory.Create();

        public async Task<string?> FindChildAsync(string parentId, string name, string? mimeType, CancellationToken cancellationToken = default)
        {
            var escaped = name.Replace("\\", "\\\\").Replace("'", "\\'");
            var query = $"'{parentId}' in parents and name = '{escaped}' and trashed = false";
            if (!string.IsNullOrEmpty(mimeType))
                query += $" and mimeType = '{mimeType}'";

            var request = Service.Files.List();
            request.Q = query;
            request.Fields = "files(id, name)";
            request.PageSize = 2;
            ApplySharedDriveSupport(request);

            var response = await request.ExecuteAsync(cancellationToken);
            return response.Files.Count > 0 ? response.Files[0].Id : null;
        }

        public async Task<IReadOnlyList<DriveEntry>> ListChildrenAsync(string parentId, string? mimeType, CancellationToken cancellationToken = default)
        {
            var query = $"'{parentId}' in parents and trashed = false";
            if (!string.IsNullOrEmpty(mimeType))
                query += $" and mimeType = '{mimeType}'";

            var entries = new List<DriveEntry>();
            string? pageToken = null;

            do
            {
                var request = Service.Files.List();
                request.Q = query;
                request.Fields = "nextPageToken, files(id, name)";
                request.PageSize = 200;
                request.PageToken = pageToken;
                ApplySharedDriveSupport(request);

                var response = await request.ExecuteAsync(cancellationToken);

                foreach (var file in response.Files)
                    entries.Add(new DriveEntry { Id = file.Id, Name = file.Name });

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return entries;
        }

        public Task<string> CreateFolderAsync(string parentId, string name, CancellationToken cancellationToken = default) =>
            CreateEmptyAsync(parentId, name, FolderMimeType, cancellationToken);

        public Task<string> CreateSpreadsheetAsync(string parentId, string name, CancellationToken cancellationToken = default) =>
            CreateEmptyAsync(parentId, name, SpreadsheetMimeType, cancellationToken);

        async Task<string> CreateEmptyAsync(string parentId, string name, string mimeType, CancellationToken cancellationToken)
        {
            var metadata = new DriveFile
            {
                Name = name,
                MimeType = mimeType,
                Parents = [parentId]
            };

            var request = Service.Files.Create(metadata);
            request.Fields = "id";
            request.SupportsAllDrives = true;

            var created = await request.ExecuteAsync(cancellationToken);
            return created.Id;
        }

        /// <summary>
        /// Two mime types, and they are not the same thing: the metadata one is what the file
        /// should *become*, the upload one is what we are *sending*. Drive converts between them.
        /// Send them equal and you get a markdown file, which is the behaviour this replaced.
        /// </summary>
        public async Task<string> CreateDocAsync(string parentId, string name, string markdown, CancellationToken cancellationToken = default)
        {
            var metadata = new DriveFile
            {
                Name = name,
                MimeType = DocumentMimeType,
                Parents = [parentId]
            };

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));

            var request = Service.Files.Create(metadata, stream, MarkdownMimeType);
            request.Fields = "id";
            request.SupportsAllDrives = true;

            var progress = await request.UploadAsync(cancellationToken);
            if (progress.Exception is not null)
                throw progress.Exception;

            return request.ResponseBody.Id;
        }

        /// <summary>
        /// Export, not download. A Google Doc has no stored bytes to fetch — <c>alt=media</c> on
        /// one fails — so the content only exists in whichever format we ask Drive to render.
        /// No SupportsAllDrives here: export takes no such parameter and needs none.
        /// </summary>
        public async Task<string> ReadDocAsync(string fileId, CancellationToken cancellationToken = default)
        {
            var request = Service.Files.Export(fileId, MarkdownMimeType);

            using var stream = new MemoryStream();
            await request.DownloadAsync(stream, cancellationToken);

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public async Task UpdateDocAsync(string fileId, string markdown, CancellationToken cancellationToken = default)
        {
            // Restating the target type is what asks Drive to convert the upload rather than
            // replace the Doc with a markdown file.
            var metadata = new DriveFile { MimeType = DocumentMimeType };

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));

            var request = Service.Files.Update(metadata, fileId, stream, MarkdownMimeType);
            request.SupportsAllDrives = true;

            var progress = await request.UploadAsync(cancellationToken);
            if (progress.Exception is not null)
                throw progress.Exception;
        }

        public async Task MoveAsync(string fileId, string addParentId, string removeParentId, CancellationToken cancellationToken = default)
        {
            var request = Service.Files.Update(new DriveFile(), fileId);
            request.AddParents = addParentId;
            request.RemoveParents = removeParentId;
            request.Fields = "id, parents";
            request.SupportsAllDrives = true;

            await request.ExecuteAsync(cancellationToken);
        }

        public async Task<string> GetWebViewLinkAsync(string fileId, CancellationToken cancellationToken = default)
        {
            var request = Service.Files.Get(fileId);
            request.Fields = "webViewLink";
            request.SupportsAllDrives = true;

            var file = await request.ExecuteAsync(cancellationToken);
            return file.WebViewLink;
        }

        public async Task<DriveFileTimes> GetTimestampsAsync(string fileId, CancellationToken cancellationToken = default)
        {
            var request = Service.Files.Get(fileId);
            request.Fields = "createdTime, modifiedTime";
            request.SupportsAllDrives = true;

            var file = await request.ExecuteAsync(cancellationToken);

            return new DriveFileTimes
            {
                CreatedTime = file.CreatedTimeDateTimeOffset,
                ModifiedTime = file.ModifiedTimeDateTimeOffset
            };
        }

        static void ApplySharedDriveSupport(FilesResource.ListRequest request)
        {
            request.SupportsAllDrives = true;
            request.IncludeItemsFromAllDrives = true;
        }
    }
}

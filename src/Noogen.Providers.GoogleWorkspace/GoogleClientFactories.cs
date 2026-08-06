using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;

namespace Noogen.Providers.GoogleWorkspace
{
    public interface IDriveClientFactory
    {
        DriveService Create();
    }

    public interface ISheetsClientFactory
    {
        SheetsService Create();
    }

    /// <summary>
    /// Resolves credentials through the Application Default Credentials chain. That single
    /// choice is what lets one code path serve a service-account key file today
    /// (GOOGLE_APPLICATION_CREDENTIALS), a `gcloud auth application-default login` as a
    /// fallback when org policy forbids key creation, and Workload Identity inside GKE when
    /// the Noogen agent eventually consumes this library.
    ///
    /// Note there is deliberately no domain-wide delegation here: shared drives accept a
    /// service account as a direct member, so the DWD signJwt dance the Gmail integration
    /// needs does not apply.
    /// </summary>
    public abstract class GoogleClientFactory<TService> where TService : BaseClientService
    {
        readonly string _applicationName;
        readonly Lazy<TService> _service;

        protected GoogleClientFactory(string applicationName)
        {
            _applicationName = applicationName;
            _service = new Lazy<TService>(Build);
        }

        protected abstract IReadOnlyList<string> Scopes { get; }

        protected abstract TService Create(BaseClientService.Initializer initializer);

        public TService GetService() => _service.Value;

        TService Build()
        {
            var credential = GoogleCredential.GetApplicationDefault().CreateScoped(Scopes);

            return Create(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = _applicationName
            });
        }
    }

    public class DriveClientFactory : GoogleClientFactory<DriveService>, IDriveClientFactory
    {
        public DriveClientFactory(string applicationName = "Noogen.Backlog")
            : base(applicationName)
        {
        }

        protected override IReadOnlyList<string> Scopes => [DriveService.Scope.Drive];

        protected override DriveService Create(BaseClientService.Initializer initializer) => new(initializer);

        DriveService IDriveClientFactory.Create() => GetService();
    }

    public class SheetsClientFactory : GoogleClientFactory<SheetsService>, ISheetsClientFactory
    {
        public SheetsClientFactory(string applicationName = "Noogen.Backlog")
            : base(applicationName)
        {
        }

        protected override IReadOnlyList<string> Scopes => [SheetsService.Scope.Spreadsheets];

        protected override SheetsService Create(BaseClientService.Initializer initializer) => new(initializer);

        SheetsService ISheetsClientFactory.Create() => GetService();
    }
}

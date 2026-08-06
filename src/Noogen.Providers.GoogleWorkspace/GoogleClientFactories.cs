using Google.Apis.Drive.v3;
using Google.Apis.Http;
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
    /// Takes an already-resolved credential rather than resolving one itself.
    ///
    /// Resolution can require I/O and, for a first-time user, a browser round trip — none of which
    /// belongs behind a lazily-evaluated property invoked from the middle of a request. Deciding
    /// who we are happens once, explicitly, at startup; see <see cref="GoogleCredentialResolver"/>.
    /// </summary>
    public abstract class GoogleClientFactory<TService> where TService : BaseClientService
    {
        readonly Lazy<TService> _service;

        protected GoogleClientFactory(IConfigurableHttpClientInitializer credential, string applicationName)
        {
            _service = new Lazy<TService>(() => Create(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = applicationName
            }));
        }

        protected abstract TService Create(BaseClientService.Initializer initializer);

        public TService GetService() => _service.Value;
    }

    public class DriveClientFactory : GoogleClientFactory<DriveService>, IDriveClientFactory
    {
        public DriveClientFactory(IConfigurableHttpClientInitializer credential, string applicationName = "Noogen.Backlog")
            : base(credential, applicationName)
        {
        }

        protected override DriveService Create(BaseClientService.Initializer initializer) => new(initializer);

        DriveService IDriveClientFactory.Create() => GetService();
    }

    public class SheetsClientFactory : GoogleClientFactory<SheetsService>, ISheetsClientFactory
    {
        public SheetsClientFactory(IConfigurableHttpClientInitializer credential, string applicationName = "Noogen.Backlog")
            : base(credential, applicationName)
        {
        }

        protected override SheetsService Create(BaseClientService.Initializer initializer) => new(initializer);

        SheetsService ISheetsClientFactory.Create() => GetService();
    }
}

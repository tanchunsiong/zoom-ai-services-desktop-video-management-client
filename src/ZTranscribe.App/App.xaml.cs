using System.Net;
using System.Net.Http;
using System.Windows;
using ZTranscribe.App.Services;
using ZTranscribe.App.ViewModels;
using ZTranscribe.Core.Services;
using ZTranscribe.Infrastructure.Media;
using ZTranscribe.Infrastructure.Persistence;
using ZTranscribe.Infrastructure.Queue;
using ZTranscribe.Infrastructure.Zoom;

namespace ZTranscribe.App;

public partial class App : Application
{
    private VlcPlaybackService? _player;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = new AppPaths();
        ISettingsStore settings = new JsonSettingsStore(paths);
        IQueueStore queueStore = new JsonQueueStore(paths);
        ICredentialVault vault = new WindowsCredentialVault();
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
        var zoom = new ZoomAiClient(http);
        var queue = new JobQueueService(queueStore, settings, vault, new FfmpegAudioExtractor(), zoom, paths);
        _player = new VlcPlaybackService();
        var viewModel = new MainWindowViewModel(queue, vault, settings, _player);

        try
        {
            await viewModel.InitializeAsync();
            new MainWindow(viewModel).Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Z Scribe could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _player?.Dispose();
        base.OnExit(e);
    }
}


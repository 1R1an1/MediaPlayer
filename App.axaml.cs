using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SharpUtils.Linux;

namespace MediaPlayer;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            File.Delete("/tmp/mediaplayer.log");
            var main = new MainWindow();
            desktop.MainWindow = main;

            // Lanzar MPRIS cuando la ventana esté abierta (mpv ya inicializó).
            ThreadPool.QueueUserWorkItem(_ => _ = StartMprisAsync(main));
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartMprisAsync(MainWindow main)
    {
        // Esperar a que PlayerView haya inicializado mpv
        while (main.Mpv == null)
            await Task.Delay(100);

        await MprisService.StartAsync(
            main.Mpv,
            new MprisCapabilities
            {
                CanQuit = true,
                CanRaise = true,
                CanPlay = true,
                CanPause = true,
                CanSeek = true,
                CanGoNext = true,
                CanGoPrevious = true,
                CanStop = true,
                SupportsLoop = true,
                SupportsVolume = true,
                SupportsRate = false,
                SupportedUriSchemes = ["file", "http", "https"],
                SupportedMimeTypes = ["video/mp4", "video/x-matroska", "video/webm"]
            },
            new MprisOptions
            {
                Name = "mediaplayer",
                DisplayName = "Media Player"
            });
    }
}

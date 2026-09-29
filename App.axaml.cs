/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MediaPlayer.Services;
using SharpUtils.Linux;

namespace MediaPlayer;

public partial class App : Application
{
    public static MpvPlayer Mpv { get; private set; }
    public static PlaylistService Playlist { get; private set; }
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Mpv = new MpvPlayer();
            Mpv.InitForRenderApi();
            Mpv.ErrorOccurred += Console.WriteLine;
            Playlist = new PlaylistService();
            var mprisInit = StartMprisAsync(Mpv);
            if (!mprisInit)
                Environment.Exit(1);

            var main = new MainWindow();
            desktop.MainWindow = main;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static bool StartMprisAsync(MpvPlayer mpv)
    {
        try
        {
            MprisService.StartAsync(mpv, new MprisCapabilities
            {
                CanQuit = true,
                CanRaise = true,
                CanPlay = false,
                CanPause = false,
                CanSeek = false,
                CanGoNext = false,
                CanGoPrevious = false,
                CanStop = true,
                SupportsLoop = true,
                SupportsShuffle = true,
                SupportsVolume = true,
                SupportedUriSchemes = ["file", "http", "https"],
                SupportedMimeTypes = ["video/mp4", "video/x-matroska", "video/webm"]
            }, new MprisOptions
            {
                Name = "mediaplayer",
                DisplayName = "Media Player"
            }).ConfigureAwait(false).GetAwaiter().GetResult();

            MprisService.SeekThresholdUs = 200_000;
            return true;
        }
        catch (Exception e) { Console.WriteLine($"Error: \"{e.Message}\" StackTrace: {e.StackTrace}"); return false; }
    }
}

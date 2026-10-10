/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MediaPlayer.Services;

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
            Init(desktop.Args);

            var main = new MainWindow();
            desktop.MainWindow = main;
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void Init(string[] args)
    {
        Mpv = new MpvPlayer(out bool isMprisSuccess);
        if (!isMprisSuccess)
            Environment.Exit(1);

        Mpv.Init();
        Mpv.Pause();
        Mpv.ErrorOccurred += Console.WriteLine;

        Playlist = new PlaylistService(args);
    }
}

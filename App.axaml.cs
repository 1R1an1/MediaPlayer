/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MediaPlayer.Models;
using MediaPlayer.Services;
using SharpUtils;

namespace MediaPlayer;

public partial class App : Application
{
    public static MpvPlayer Mpv { get; private set; }
    public static PlaylistService Playlist { get; private set; }
    public static Player Player { get; private set; }
    public static AppSettings Settings { get; private set; }

    public static readonly string BASEPATH = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaPlayer");
    public static readonly string SETTINGSPATH = Path.Combine(BASEPATH, "settings.json");
    public static readonly string LASTSTATEPATH = Path.Combine(BASEPATH, "last-state.json");
    private static LastState lastState;

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
        if (!Directory.Exists(BASEPATH))
            Directory.CreateDirectory(BASEPATH);
        Settings = JsonUtils.LoadJson<AppSettings>(SETTINGSPATH);

        lastState = Settings.loadLastState ? JsonUtils.LoadJson<LastState>(LASTSTATEPATH) : new();

        ThreadPool.QueueUserWorkItem(async _ =>
        {
            while (Settings.saveLastState)
            {
                await Task.Delay(60_000);
                lastState.player = Player.PlayerState;
                lastState.playlist = Playlist.PlaylistState;
                JsonUtils.SaveJson(lastState, LASTSTATEPATH);
            }
        });

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (!Settings.saveLastState) return;

            lastState.player = Player.PlayerState;
            lastState.playlist = Playlist.PlaylistState;
            JsonUtils.SaveJson(lastState, LASTSTATEPATH);
        };

        Mpv = new MpvPlayer(out bool isMprisSuccess);
        if (!isMprisSuccess)
            Environment.Exit(1);

        Mpv.Init();
        Mpv.Pause();
        Mpv.ErrorOccurred += Console.WriteLine;

        Playlist = new PlaylistService() { PlaylistState = lastState?.playlist };

        if (args != null && args.Length > 0)
            MediaLoader.LoadPaths(args);

        Player = new Player() { PlayerState = lastState?.player };
    }
}

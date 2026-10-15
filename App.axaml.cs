/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Buffers.Binary;
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
        bool haveArgs = args != null && args.Length > 0;
        StartDiskThings(haveArgs);

        Mpv = new MpvPlayer(out bool isMprisSuccess);
        if (!isMprisSuccess)
            Environment.Exit(1);

        Mpv.Init();
        Mpv.Pause();
        Mpv.ErrorOccurred += Console.WriteLine;

        Playlist = new PlaylistService() { PlaylistState = lastState.Playlist };

        if (haveArgs)
            MediaLoader.LoadPaths(args);

        Player = new Player() { PlayerState = lastState.Player };
    }

    private static void StartDiskThings(bool haveArgs)
    {
        if (!Directory.Exists(BASEPATH))
            Directory.CreateDirectory(BASEPATH);
        Settings = JsonUtils.LoadJson<AppSettings>(SETTINGSPATH);
        Settings.saveLastState = CheckLockFile() ? Settings.saveLastState : false;

        lastState = Settings.loadLastState ? JsonUtils.LoadJson<LastState>(LASTSTATEPATH) : new();

        ThreadPool.QueueUserWorkItem(async _ =>
        {
            while (Settings.saveLastState)
            {
                await Task.Delay(60_000);
                lastState.Player = Player.PlayerState;
                lastState.Playlist = Playlist.PlaylistState;
                JsonUtils.SaveJson(lastState, LASTSTATEPATH);
            }
        });

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            DeleteLockFile();
            if (!Settings.saveLastState) return;

            lastState.Player = Player.PlayerState;
            lastState.Playlist = Playlist.PlaylistState;
            JsonUtils.SaveJson(lastState, LASTSTATEPATH);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => DeleteLockFile();
        Console.CancelKeyPress += (_, _) => DeleteLockFile();

        if (haveArgs)
        {
            //pM = playerModified
            var pM = lastState.Player;
            pM.PositionSec = 0;

            lastState.Playlist = new();
            lastState.Player = pM;
        }
    }

    /// <summary>
    /// Verifica si el archivo lock esta creado, de lo contrario lo crea
    /// </summary>
    /// <returns>Devuelve true si el archivo fue creado, de lo contrario devuelve false</returns>
    private static bool CheckLockFile()
    {
        if (File.Exists("/tmp/mediaplayer/mp.lock")) return false;

        if (!Directory.Exists("/tmp/mediaplayer"))
            Directory.CreateDirectory("/tmp/mediaplayer");

        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, Environment.ProcessId);
        File.WriteAllBytes("/tmp/mediaplayer/mp.lock", bytes);
        return true;
    }

    private static void DeleteLockFile()
    {
        if (!Directory.Exists("/tmp/mediaplayer") || !File.Exists("/tmp/mediaplayer/mp.lock")) return;

        byte[] bytes = File.ReadAllBytes("/tmp/mediaplayer/mp.lock");
        int prosId = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (prosId == Environment.ProcessId)
            File.Delete("/tmp/mediaplayer/mp.lock");
    }
}

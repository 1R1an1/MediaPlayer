using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MediaPlayer.Models;
using MediaPlayer.Services;
using SharpUtils.Linux;

namespace MediaPlayer;

/// <summary>
/// Shell principal. Conecta PlayerView + PlaylistView + MpvPlayer + PlaylistService
/// + KeyBindings. Es la única que sabe cómo se relacionan todos.
/// App.axaml.cs llama a StartMprisAsync() después de mostrar esta ventana.
/// </summary>
public partial class MainWindow : Window
{
    private readonly PlaylistService _playlist = new PlaylistService();
    private readonly KeyBindings _keys = new KeyBindings();

    // PlayerControl es el control PlayerView del XAML. Mpv es el MpvPlayer
    // que vive adentro (puede ser null hasta que mpv inicializa).
    public MpvPlayer Mpv => PlayerControl?.Player;

    public MainWindow()
    {
        InitializeComponent();


        // Capturar KeyDown en fase Tunnel y aunque los controles hijos ya lo hayan
        // manejado (ej: Space activa un botón). Sin esto, Space no llega a nuestro
        // handler cuando un botón tiene foco.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnKeyDownTunnel(object sender, KeyEventArgs e)
    {
        // Ctrl+O = abrir archivo (atajo global)
        if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control)
        {
            OpenFile();
            e.Handled = true;
            return;
        }
        if (_keys.Handle(e))
        {
            e.Handled = true;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WireEverything();
    }

    private void WireEverything()
    {
        // PlayerView → acciones de UI
        PlayerControl.PlayPauseClicked += OnPlayPause;
        PlayerControl.PrevClicked += OnPrev;
        PlayerControl.NextClicked += OnNext;
        PlayerControl.LoopClicked += OnCycleLoop;
        PlayerControl.AudioButtonClicked += OnShowAudioMenu;
        PlayerControl.VolumeChanged01 += OnVolumeChanged;
        PlayerControl.MuteToggled += OnMuteToggled;
        PlayerControl.SeekRequested += OnSeekRequested;
        PlayerControl.VideoSingleClicked += OnPlayPause;
        PlayerControl.VideoDoubleClicked += ToggleFullscreen;

        // Drag & drop
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // MpvPlayer puede ser null hasta que PlayerView inicializa mpv.
        WireMpvWhenReady();
    }

    private async void WireMpvWhenReady()
    {
        while (Mpv == null)
            await Task.Delay(100);

        Mpv.EndReached += OnEndReached;
        Mpv.NextRequested += OnNext;
        Mpv.PrevRequested += OnPrev;
        Mpv.QuitRequested += Close;
        Mpv.RaiseRequested += () => { WindowState = WindowState.Normal; Activate(); };

        // PlaylistService → refrescar UI
        _playlist.Changed += RefreshPlaylist;
        _playlist.CurrentChanged += _ => RefreshPlaylist();
        _playlist.LoopModeChanged += mode =>
        {
            PlayerControl.SetLoopModeLabel(mode);
            UpdateMprisLoopStatus(mode);
        };

        // PlaylistView
        Playlist.ItemDoubleClicked += OnPlaylistItemDoubleClicked;
        Playlist.CloseRequested += () => Playlist.IsVisible = false;
        Playlist.ClearRequested += () => _playlist.Clear();

        // KeyBindings
        _keys.TogglePlayPause = OnPlayPause;
        _keys.SeekRelative = sec => Mpv?.SeekRelative(sec);
        _keys.VolumeDelta = delta =>
        {
            if (Mpv == null) return;
            Mpv.SetVolume01(Math.Clamp(Mpv.Volume + delta, 0, 1));
        };
        _keys.ToggleMute = () => Mpv?.SetMute(!Mpv.IsMuted);
        _keys.Next = OnNext;
        _keys.Prev = OnPrev;
        _keys.CycleLoopMode = OnCycleLoop;
        _keys.ToggleFullscreen = ToggleFullscreen;
        _keys.ExitFullscreen = () =>
        {
            if (WindowState == WindowState.FullScreen)
                WindowState = WindowState.Normal;
        };
        _keys.NextAudioTrack = CycleAudioTrack;
        _keys.TogglePlaylist = () => Playlist.IsVisible = !Playlist.IsVisible;
        _keys.Quit = () => Close();

        PlayerControl.SetLoopModeLabel(_playlist.LoopMode);
    }

    // ============================================================
    // ===================  Apertura de archivos  ================
    // ============================================================
    public async void OpenFile() => await OpenFilePickerAsync();

    private async Task OpenFilePickerAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Abrir video",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Videos")
                {
                    Patterns = new[]
                    {
                        "*.mp4", "*.mkv", "*.webm", "*.avi", "*.mov",
                        "*.flv", "*.wmv", "*.mpg", "*.mpeg", "*.m4v", "*.ts"
                    }
                },
                new FilePickerFileType("Todos") { Patterns = new[] { "*.*" } }
            }
        });

        if (files == null || files.Count == 0) return;

        var paths = files.Select(f => f.Path.LocalPath).ToList();
        _playlist.Clear();
        _playlist.AddRange(paths);
        LoadCurrentFromPlaylist();
    }

    // ============================================================
    // ============  Player actions (botones + keys)  =============
    // ============================================================
    private void OnPlayPause()
    {
        if (Mpv == null || !Mpv.IsInitialized) return;
        if (Mpv.CurrentPath == null)
        {
            LoadCurrentFromPlaylist();
            return;
        }
        Mpv.PlayPause();
    }

    private void OnNext()
    {
        int idx = _playlist.Advance();
        if (idx >= 0) LoadCurrentFromPlaylist();
    }

    private void OnPrev()
    {
        if (Mpv != null && Mpv.PositionSec > 5)
        {
            Mpv.SeekAbsolute(0);
            return;
        }
        int idx = _playlist.GoPrev();
        if (idx >= 0) LoadCurrentFromPlaylist();
    }

    private void OnCycleLoop() => _playlist.CycleLoopMode();

    private void OnShowAudioMenu()
    {
        if (Mpv == null || Mpv.AudioTracks.Count == 0) return;

        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        foreach (var t in Mpv.AudioTracks)
        {
            var mi = new MenuItem
            {
                Header = t.Display,
                IsChecked = t.IsSelected,
                ToggleType = MenuItemToggleType.Radio,
            };
            int id = t.Id;
            mi.Click += (s, e) => Mpv.SetAudioTrack(id);
            items.Add(mi);
        }
        menu.ItemsSource = items;
        menu.Open(PlayerControl);
    }

    private void CycleAudioTrack()
    {
        if (Mpv == null || Mpv.AudioTracks.Count == 0) return;
        var list = Mpv.AudioTracks;
        int idx = list.ToList().FindIndex(t => t.IsSelected);
        int next = (idx + 1) % list.Count;
        Mpv.SetAudioTrack(list[next].Id);
    }

    private void OnVolumeChanged(double v01) => Mpv?.SetVolume01(v01);
    private void OnMuteToggled(bool muted) => Mpv?.SetMute(muted);
    private void OnSeekRequested(double seconds) => Mpv?.SeekAbsolute(seconds);

    private void OnEndReached()
    {
        // Loop track = mismo archivo de nuevo; si no, avanzar
        if (_playlist.LoopMode == LoopMode.Track)
        {
            LoadCurrentFromPlaylist();
            return;
        }
        OnNext();
    }

    // ============================================================
    // ==============  Playlist → Player sync  ====================
    // ============================================================
    private void LoadCurrentFromPlaylist()
    {
        var current = _playlist.Current;
        if (current == null) return;
        if (Mpv == null || !Mpv.IsInitialized) return;
        Mpv.LoadFile(current.Path);
    }

    private void RefreshPlaylist()
        => Playlist.SetItems(_playlist.Items, _playlist.CurrentIndex);

    private void OnPlaylistItemDoubleClicked(int idx)
    {
        _playlist.SetCurrent(idx);
        LoadCurrentFromPlaylist();
    }

    // ============================================================
    // ===================  Fullscreen  ==========================
    // ============================================================
    private void ToggleFullscreen()
    {
        WindowState = (WindowState == WindowState.FullScreen)
            ? WindowState.Normal
            : WindowState.FullScreen;
    }

    // ============================================================
    // =============  MPRIS helpers (loop status)  ===============
    // ============================================================
    private void UpdateMprisLoopStatus(LoopMode mode)
    {
        if (Mpv == null) return;
        Mpv.LoopStatus = mode switch
        {
            LoopMode.None => "None",
            LoopMode.Track => "Track",
            LoopMode.Playlist => "Playlist",
            _ => "None"
        };
        Mpv.Shuffle = _playlist.Shuffle;
        MprisService.Update();
    }

    // ============================================================
    // ===========  Drag & drop de archivos (Avalonia 12)  ========
    // ============================================================
    // En Avalonia 12, DragEventArgs.DataTransfer (IDataTransfer) reemplazó
    // al viejo Data. TryGetFiles() devuelve IStorageItem[]? (null si no hay).
    private void OnDragOver(object sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        e.DragEffects = (files != null && files.Length > 0)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files == null || files.Length == 0) return;

        var paths = files
            .Where(f => IsMediaFile(f.Path.LocalPath))
            .Select(f => f.Path.LocalPath)
            .ToList();
        if (paths.Count == 0) return;

        bool wasEmpty = _playlist.Count == 0;
        _playlist.AddRange(paths);
        if (wasEmpty) LoadCurrentFromPlaylist();
    }

    private static bool IsMediaFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        string[] exts = { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".wmv", ".mpg", ".mpeg", ".m4v", ".ts" };
        return Array.IndexOf(exts, ext) >= 0;
    }
}

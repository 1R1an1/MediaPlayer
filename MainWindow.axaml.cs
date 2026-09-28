using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MediaPlayer.Services;
using SharpUtils.Linux;

namespace MediaPlayer;

/// <summary>
/// Shell principal. Crea MpvPlayer, se lo pasa a PlayerView, y orquesta
/// playlist, MPRIS, atajos de teclado y drag&drop.
/// </summary>
public partial class MainWindow : Window
{
    private PlaylistService _playlist => App.Playlist;
    private MpvPlayer _mpv => App.Mpv;

    private readonly KeyBindings _keys = new KeyBindings();

    public MainWindow()
    {
        InitializeComponent();

        // --- MPVPLAYER-MPRIS EVENTS --- //
        _mpv.MPRISNextRequested += PlayerView.Next;
        _mpv.MPRISPrevRequested += PlayerView.Preview;
        _mpv.MPRISQuitRequested += Close;
        _mpv.MPRISRaiseRequested += () => { WindowState = WindowState.Normal; Activate(); };
        _mpv.MPRISVolumeChanged += _mpv.SetVolume01;

        // --- PLAYLIST EVENTS --- //
        _playlist.LoopModeChanged += _ => UpdateMprisState();
        _playlist.CurrentChanged += _ => UpdateMprisState();
        PlaylistView.CloseRequested += async () => await HidePlaylist();
        PlayerView.ToggleFullScreen += ToggleFullscreen;

        // --- KEYBINDINGS --- //
        _keys.TogglePlayPause = PlayerView.PlayPause;
        _keys.SeekRelative = sec => _mpv?.SeekRelative(sec);
        _keys.VolumeDelta = delta => _mpv?.SetVolume01(Math.Clamp(_mpv.Volume + delta, 0, 1));
        _keys.ToggleMute = () => _mpv?.SetMute(!_mpv.IsMuted);
        _keys.Next = PlayerView.Next;
        _keys.Prev = PlayerView.Preview;
        _keys.CycleLoopMode = _playlist.CycleLoopMode;
        _keys.ToggleFullscreen = ToggleFullscreen;
        _keys.ExitFullscreen = () => { if (WindowState == WindowState.FullScreen) WindowState = WindowState.Normal; };
        _keys.NextAudioTrack = () =>
        {
            if (_mpv.AudioTracks.Count == 0) return;
            var list = _mpv.AudioTracks;
            int idx = list.ToList().FindIndex(t => t.IsSelected);
            _mpv.SetAudioTrack(list[(idx + 1) % list.Count].Id);
        };
        _keys.TogglePlaylist = async () =>
        {
            if (PlaylistGrid.IsVisible)
                await HidePlaylist();
            else
                ShowPlaylist();
        };
        _keys.Quit = Close;


        // --- DRAG AND DROP --- //
        DragDrop.SetAllowDrop(this, true);

        // --- TUNNEL BINDINGS --- //
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void UpdateMprisState()
    {
        // Actualizar capabilities de MPRIS según el estado de la Playlist.
        bool hasNext = _playlist.Current != null && _playlist.PeekNext() is var nt && nt >= 0 && nt != _playlist.CurrentIndex;
        bool hasPrev = _playlist.Current != null && _playlist.PeekPrev() is var pv && pv >= 0 && pv != _playlist.CurrentIndex;

        MprisService.capabilities.CanGoNext = hasNext;
        MprisService.capabilities.CanGoPrevious = hasPrev;
        MprisService.capabilities.CanPlay = _playlist.Current != null;
        MprisService.capabilities.CanPause = _playlist.Current != null;
        MprisService.capabilities.CanSeek = _playlist.Current != null;
        MprisService.Update();
    }

    private async Task OpenFilePickerAsync()
    {
        string[] filter = ["*.mp4", "*.mkv", "*.webm", "*.avi", "*.mov", "*.flv", "*.wmv", "*.mpg", "*.mpeg", "*.m4v", "*.ts"];
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Abrir video",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Videos"){ Patterns = filter },
                new FilePickerFileType("Todos") { Patterns = ["*.*"] }
            ]
        });

        if (files == null || files.Count == 0) return;
        var filePath = files.Select(f => f.Path.LocalPath).Where(IsMediaFile);
        if (files == null || filePath.Count() == 0) return;

        await _playlist.AddNew(filePath);
    }

    private void ToggleFullscreen()
    {
        WindowState = (WindowState == WindowState.FullScreen) ? WindowState.Normal : WindowState.FullScreen;
        PlayerView.UpdateFullScreenIcons(WindowState == WindowState.FullScreen);
    }

    private async Task OnKeyDownTunnel(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control)
        {
            await OpenFilePickerAsync();
            e.Handled = true;
            return;
        }
        if (_keys.Handle(e)) e.Handled = true;
    }

    // Drag & drop (Avalonia 12: DragEventArgs.DataTransfer.TryGetFiles())
    private void OnDragOver(object sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        e.DragEffects = (files != null && files.Length > 0) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async Task OnDrop(object sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files == null || files.Length == 0) return;

        var paths = files
            .Where(f => IsMediaFile(f.Path.LocalPath))
            .Select(f => f.Path.LocalPath)
            .ToList();
        if (paths.Count == 0) return;

        await _playlist.Add(paths);
    }

    private static bool IsMediaFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string[] exts = { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".wmv", ".mpg", ".mpeg", ".m4v", ".ts" };
        return Array.IndexOf(exts, ext) >= 0;
    }

    private void ShowPlaylist()
    {
        PlaylistGrid.IsVisible = true;
        PlaylistGrid.Opacity = 1;
    }

    private async Task HidePlaylist()
    {
        PlaylistGrid.Opacity = 0;
        await Task.Delay(250);
        PlaylistGrid.IsVisible = false;
    }
}

using System;
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
/// Shell principal. Crea MpvPlayer, se lo pasa a PlayerView, y orquesta
/// playlist, MPRIS, atajos de teclado y drag&drop.
/// </summary>
public partial class MainWindow : Window
{
    private readonly PlaylistService _playlist = new PlaylistService();
    private readonly KeyBindings _keys = new KeyBindings();

    public MpvPlayer Mpv => PlayerControl?.Player;

    public MainWindow()
    {
        InitializeComponent();
        // Tunnel + handledEventsToo para capturar teclas aunque un control hijo las maneje.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WirePlayerControl();
        WireDragDrop();
        WireMpvWhenReady();
    }

    // PlayerView → acciones que necesitan MainWindow (playlist + fullscreen)
    private void WirePlayerControl()
    {
        PlayerControl.NextRequested += OnNext;
        PlayerControl.PrevRequested += OnPrev;
        PlayerControl.LoopClicked += _playlist.CycleLoopMode;
        PlayerControl.ToggleFullScreen += ToggleFullscreen;
    }

    private void WireDragDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void WireMpvWhenReady()
    {
        while (Mpv == null) await Task.Delay(100);

        // MPRIS nos puede mandar acciones desde el escritorio.
        Mpv.EndReached += OnEndReached;
        Mpv.NextRequested += OnNext;
        Mpv.PrevRequested += OnPrev;
        Mpv.QuitRequested += Close;
        Mpv.RaiseRequested += () => { WindowState = WindowState.Normal; Activate(); };

        // Playlist → UI
        _playlist.Changed += () => Playlist.SetItems(_playlist.Items, _playlist.CurrentIndex);
        _playlist.CurrentChanged += _ => Playlist.SetItems(_playlist.Items, _playlist.CurrentIndex);
        _playlist.LoopModeChanged += mode =>
        {
            PlayerControl.SetLoopModeLabel(mode);
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
        };

        // PlaylistView → acciones
        Playlist.ItemDoubleClicked += idx =>
        {
            _playlist.SetCurrent(idx);
            LoadCurrentFromPlaylist();
        };
        Playlist.CloseRequested += () => PlaylistGrid.IsVisible = false;
        Playlist.ClearRequested += _playlist.Clear;

        // KeyBindings → acciones directas
        _keys.TogglePlayPause = () =>
        {
            if (Mpv == null || !Mpv.IsInitialized) return;
            if (Mpv.CurrentPath == null && _playlist.Current != null) LoadCurrentFromPlaylist();
            else if (Mpv.EofReached) { Mpv.SeekAbsolute(0); Mpv.Play(); }
            else Mpv.PlayPause();
        };
        _keys.SeekRelative = sec => Mpv?.SeekRelative(sec);
        _keys.VolumeDelta = delta => Mpv?.SetVolume01(Math.Clamp(Mpv.Volume + delta, 0, 1));
        _keys.ToggleMute = () => Mpv?.SetMute(!Mpv.IsMuted);
        _keys.Next = OnNext;
        _keys.Prev = OnPrev;
        _keys.CycleLoopMode = _playlist.CycleLoopMode;
        _keys.ToggleFullscreen = ToggleFullscreen;
        _keys.ExitFullscreen = () => { if (WindowState == WindowState.FullScreen) WindowState = WindowState.Normal; };
        _keys.NextAudioTrack = () =>
        {
            if (Mpv == null || Mpv.AudioTracks.Count == 0) return;
            var list = Mpv.AudioTracks;
            int idx = list.ToList().FindIndex(t => t.IsSelected);
            Mpv.SetAudioTrack(list[(idx + 1) % list.Count].Id);
        };
        _keys.TogglePlaylist = () => PlaylistGrid.IsVisible = !PlaylistGrid.IsVisible;
        _keys.Quit = Close;

        PlayerControl.SetLoopModeLabel(_playlist.LoopMode);
    }

    public async void OpenFile() => await OpenFilePickerAsync();

    private async Task OpenFilePickerAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Abrir video",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Videos")
                {
                    Patterns = [ "*.mp4", "*.mkv", "*.webm", "*.avi", "*.mov",
                                       "*.flv", "*.wmv", "*.mpg", "*.mpeg", "*.m4v", "*.ts" ]
                },
                new FilePickerFileType("Todos") { Patterns = new[] { "*.*" } }
            ]
        });

        if (files == null || files.Count == 0) return;

        _playlist.Clear();
        _playlist.AddRange(files.Select(f => f.Path.LocalPath));
        LoadCurrentFromPlaylist();
    }

    private void OnNext()
    {
        int idx = _playlist.Advance();
        if (idx >= 0) LoadCurrentFromPlaylist();
    }

    private void OnPrev()
    {
        // Si pasamos más de 5s, volver al principio del actual.
        if (Mpv != null && Mpv.PositionSec > 5) { Mpv.SeekAbsolute(0); return; }
        int idx = _playlist.GoPrev();
        if (idx >= 0) LoadCurrentFromPlaylist();
    }

    private void OnEndReached()
    {
        // Loop track: seek al principio + play (sin recargar el archivo).
        if (_playlist.LoopMode == LoopMode.Track)
        {
            Mpv?.SeekAbsolute(0);
            Mpv?.Play();
            return;
        }
        OnNext();
    }

    private void LoadCurrentFromPlaylist()
    {
        var current = _playlist.Current;
        if (current == null || Mpv == null || !Mpv.IsInitialized) return;
        Mpv.LoadFile(current.Path);
    }

    private void ToggleFullscreen()
    {
        WindowState = (WindowState == WindowState.FullScreen) ? WindowState.Normal : WindowState.FullScreen;
        PlayerControl.UpdateFullScreenIcons(WindowState == WindowState.FullScreen);
    }

    private void OnKeyDownTunnel(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control)
        {
            OpenFile();
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
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string[] exts = { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".wmv", ".mpg", ".mpeg", ".m4v", ".ts" };
        return Array.IndexOf(exts, ext) >= 0;
    }

    bool pressed = false;
    private void Border_PointerReleased(object sender, PointerReleasedEventArgs e) => pressed = false;
    private void Border_PointerPressed(object sender, PointerPressedEventArgs e) => pressed = true;

    private void Border_PointerMoved(object sender, PointerEventArgs e)
    {
        if (!pressed) return;
        var pos = e.GetPosition(this);
        if (pos.X < 100 || Width - pos.X < 100) return;
        PlaylistGrid.Width = Width - pos.X;
    }
}

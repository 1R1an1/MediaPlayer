/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MediaPlayer.Services;

namespace MediaPlayer;

/// <summary>
/// Shell principal. Crea MpvPlayer, se lo pasa a PlayerView, y orquesta
/// playlist, MPRIS, atajos de teclado y drag&drop.
/// </summary>
public partial class MainWindow : Window
{
    private PlaylistService _playlist => App.Playlist;
    private MpvPlayer _mpv => App.Mpv;
    private Player _player => App.Player;

    private readonly KeyBindings _keys = new KeyBindings();

    public MainWindow()
    {
        InitializeComponent();

        // --- MPVPLAYER-MPRIS EVENTS (UI) --- //
        _mpv.MPRISQuitRequested += Close;
        _mpv.MPRISRaiseRequested += () => { WindowState = WindowState.Normal; Activate(); };

        // --- PLAYLIST EVENTS (UI) --- //
        PlaylistView.CloseRequested += async () => await HidePlaylist();
        PlaylistView.ShowOverlay += PlayerView.ShowOverlay;
        PlayerView.ToggleFullScreen += ToggleFullscreen;

        // --- KEYBINDINGS --- //
        _keys.TogglePlayPause = _player.PlayPause;
        _keys.SeekRelative = _player.SeekRelative;
        _keys.FrameStep = _player.FrameStep;
        _keys.FrameBackStep = _player.FrameBackStep;
        _keys.VolumeDelta = _player.VolumeDelta;
        _keys.ToggleMute = _player.ToggleMute;
        _keys.Next = _player.Next;
        _keys.Prev = _player.Preview;
        _keys.CycleLoopMode = _player.CycleLoopMode;
        _keys.ToggleShuffle += _player.ToggleShuffle;
        _keys.ToggleFullscreen = ToggleFullscreen;
        _keys.ExitFullscreen = () => { if (WindowState == WindowState.FullScreen) WindowState = WindowState.Normal; };
        _keys.NextAudioTrack = _player.NextAudioTrack;
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

    private async Task OpenFilePickerAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Abrir video",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Videos"){ Patterns = Array.ConvertAll(MediaLoader.SupportedExtensions, f => $"*{f}") },
                new FilePickerFileType("Todos") { Patterns = ["*.*"] }
            ]
        });

        if (files == null || files.Count == 0) return;
        MediaLoader.LoadFiles(files.Select(f => f.Path.LocalPath));
    }

    private async Task OpenFolderPickerAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Abrir carpeta",
            AllowMultiple = false
        });

        if (folders == null || folders.Count == 0) return;

        var path = folders[0].Path.LocalPath;
        if (!Directory.Exists(path)) return;

        MediaLoader.LoadPaths(path);
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
        else if (e.Key == Key.O && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            await OpenFolderPickerAsync();
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
            .Where(f => MediaLoader.IsMediaFile(f.Path.LocalPath))
            .Select(f => f.Path.LocalPath)
            .ToList();
        if (paths.Count == 0) return;

        _playlist.Add(paths);
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

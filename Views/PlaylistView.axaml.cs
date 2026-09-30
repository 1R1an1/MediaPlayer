/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MediaPlayer.Models;
using MediaPlayer.Services;

namespace MediaPlayer.Views;

/// <summary>Panel lateral con la cola de reproducción.</summary>
public partial class PlaylistView : UserControl
{
    private PlaylistService _playlist => App.Playlist;
    public event Action CloseRequested;
    private Color? MutedColor = Application.Current.FindResource("MutedColor") as Color?;
    private Color? AccentColor = Application.Current.FindResource("AccentColor") as Color?;
    public event Func<Avalonia.Svg.Svg, Task> ShowOverlay;

    public PlaylistView()
    {
        InitializeComponent();
        CloseBtn.Click += (_, _) => CloseRequested?.Invoke();
        ClearBtn.Click += (_, _) => _playlist.Clear();
        ShuffleBtn.Click += (_, _) => ToggleShuffle();
    }

    public async void ToggleShuffle(bool? enable = null)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            _playlist.Shuffle = enable ?? !_playlist.Shuffle;
            ShuffleIcon.CurrentColor = _playlist.Shuffle ? AccentColor : MutedColor;
        });

        if (ShowOverlay != null)
            await ShowOverlay(ShuffleIcon);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedIndex < 0) return;
        _playlist.SetCurrent(List.SelectedIndex);
        List.SelectedIndex = -1;
    }

    private void RemoveBtn_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var index = _playlist.Items.IndexOf((sender as Button).Tag as PlaylistItem);
        _playlist.RemoveAt(index);
    }
}

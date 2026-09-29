/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Linq;
using Avalonia.Controls;
using MediaPlayer.Models;
using MediaPlayer.Services;

namespace MediaPlayer.Views;

/// <summary>Panel lateral con la cola de reproducción.</summary>
public partial class PlaylistView : UserControl
{
    private PlaylistService _playlist => App.Playlist;
    public event Action CloseRequested;

    public PlaylistView()
    {
        InitializeComponent();
        CloseBtn.Click += (_, _) => CloseRequested?.Invoke();
        ClearBtn.Click += (_, _) => _playlist.Clear();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedIndex >= 0)
            _playlist.SetCurrent(List.SelectedIndex);
    }

    private void RemoveBtn_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var index = _playlist.Items.IndexOf((sender as Button).Tag as PlaylistItem);
        _playlist.RemoveAt(index);
    }
}

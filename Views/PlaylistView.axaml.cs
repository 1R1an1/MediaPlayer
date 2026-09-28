using System;
using Avalonia.Controls;
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
}

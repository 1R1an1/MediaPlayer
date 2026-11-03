/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MediaPlayer.Models;
using MediaPlayer.Services;
using Sortable.Avalonia;

namespace MediaPlayer.Views;

/// <summary>Panel lateral con la cola de reproducción.</summary>
public partial class PlaylistView : UserControl
{
    private PlaylistService _playlist => App.Playlist;
    private Player _player => App.Player;
    public event Action CloseRequested;
    private Color? MutedColor = Application.Current.FindResource("MutedColor") as Color?;
    private Color? AccentColor = Application.Current.FindResource("AccentColor") as Color?;
    public event Func<Avalonia.Svg.Svg, Task> ShowOverlay;

    public PlaylistView()
    {
        InitializeComponent();
        if (Design.IsDesignMode)
        {
            List.ItemsSource = new PlaylistItem[] { new PlaylistItem("/test/123/video.mkv") { Artist = "123", Title = "Video test", IsCurrent = true, Duration = new TimeSpan(0, 2, 53) } };
            return;
        }

        CloseBtn.Click += (_, _) => CloseRequested?.Invoke();
        ClearBtn.Click += (_, _) => _playlist.Clear();
        ShuffleBtn.Click += (_, _) => { _player.ToggleShuffle(); UpdateShuffle(_playlist.Shuffle); };
        _playlist.ShuffleChanged += UpdateShuffle;
        Sortable.Avalonia.Sortable.SetUpdateCommand(List, new RelayCommand<SortableUpdateEventArgs>((e) => { _playlist.Move(e.OldIndex, e.NewIndex); }));

        if (_playlist.Shuffle == true)
            UpdateShuffle(_playlist.Shuffle);
    }

    private async void UpdateShuffle(bool enable)
    {
        Dispatcher.UIThread.Invoke(() => ShuffleIcon.CurrentColor = enable ? AccentColor : MutedColor);

        if (ShowOverlay != null)
            await ShowOverlay(ShuffleIcon);
    }

    private void Grid_PointerReleased(object sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left) return;
        _playlist.SetCurrent(_playlist.Items.IndexOf((sender as Grid).Tag as PlaylistItem));
    }

    private void RemoveBtn_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var index = _playlist.Items.IndexOf((sender as Button).Tag as PlaylistItem);
        _playlist.RemoveAt(index);
    }
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;

    public RelayCommand(Action<T> execute) => _execute = execute;
    public bool CanExecute(object parameter) => true;
    public void Execute(object parameter) => _execute((T)parameter);
#pragma warning disable CS0067
    public event EventHandler CanExecuteChanged;
#pragma warning restore CS0067
}

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using MediaPlayer.Models;

namespace MediaPlayer.Views;

/// <summary>Panel lateral con la cola de reproducción.</summary>
public partial class PlaylistView : UserControl
{
    public event Action<int> ItemDoubleClicked;
    public event Action CloseRequested;
    public event Action ClearRequested;

    public PlaylistView()
    {
        InitializeComponent();
        List.SelectionChanged += OnSelectionChanged;
        CloseBtn.Click += (s, e) => CloseRequested?.Invoke();
        ClearBtn.Click += (s, e) => ClearRequested?.Invoke();
    }

    public void SetItems(IReadOnlyList<PlaylistItem> items, int currentIndex)
    {
        List.ItemsSource = null;
        List.ItemsSource = items;
        if (currentIndex >= 0 && currentIndex < items.Count)
            List.SelectedIndex = currentIndex;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedIndex >= 0) ItemDoubleClicked?.Invoke(List.SelectedIndex);
    }
}

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
        List.DoubleTapped += OnListDoubleTapped;
        CloseBtn.Click += (s, e) => CloseRequested?.Invoke();
        ClearBtn.Click += (s, e) => ClearRequested?.Invoke();
    }

    public void SetItems(IReadOnlyList<PlaylistItem> items, int currentIndex)
    {
        var panels = new List<StackPanel>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, Spacing = 2 };

            panel.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontWeight = (i == currentIndex) ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = (i == currentIndex) ? Brush.Parse("#ffffff") : Brush.Parse("#e8e8e8"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            panel.Children.Add(new TextBlock
            {
                Text = item.DurationText,
                FontSize = 11,
                Foreground = Brush.Parse("#888888"),
            });
            panels.Add(panel);
        }

        List.ItemsSource = panels;
        if (currentIndex >= 0 && currentIndex < items.Count)
            List.SelectedIndex = currentIndex;
    }

    private void OnListDoubleTapped(object sender, TappedEventArgs e)
    {
        if (List.SelectedIndex >= 0) ItemDoubleClicked?.Invoke(List.SelectedIndex);
    }
}

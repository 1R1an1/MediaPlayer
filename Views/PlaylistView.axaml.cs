using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
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
        var accent = (IBrush)Application.Current.FindResource("AccentBrush");
        var foreground = (IBrush)Application.Current.FindResource("ForegroundBrush");
        var muted = (IBrush)Application.Current.FindResource("MutedBrush");

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool isCurrent = i == currentIndex;
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(2, 2, 0, 2),
                ClipToBounds = true
            };

            // Indicador de item actual (barra blanca a la izquierda)
            if (isCurrent)
            {
                panel.Children.Add(new Border
                {
                    Width = 3,
                    Background = accent,
                    CornerRadius = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Stretch,
                });
            }

            var textPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            textPanel.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = isCurrent ? accent : foreground,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = item.DurationText,
                FontSize = 11,
                Foreground = muted,
            });
            panel.Children.Add(textPanel);
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

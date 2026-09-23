using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MediaPlayer.Models;

namespace MediaPlayer.Views;

/// <summary>
/// Panel lateral con la cola de reproducción.
/// Recibe la lista desde MainWindow via SetItems(). Emite eventos cuando el
/// usuario hace doble click (reproducir) o arrastra (reordenar).
/// </summary>
public partial class PlaylistView : UserControl
{
    public event Action<int> ItemDoubleClicked;   // índice en la lista
    public event Action CloseRequested;
    public event Action ClearRequested;
    public event Action<int, int> ItemMoved;       // from, to

    public PlaylistView()
    {
        InitializeComponent();
        List.DoubleTapped += OnListDoubleTapped;
        CloseBtn.Click += (s, e) => CloseRequested?.Invoke();
        ClearBtn.Click += (s, e) => ClearRequested?.Invoke();

        // TODO: drag&drop para reordenar. En Avalonia ListBox requiere handlers
        // DragDrop + Adorner. Lo dejamos pendiente, el usuario puede usar botones
        // de "mover arriba/abajo" como alternativa simple.
    }

    /// <summary>
    /// Refresca la lista. items es la lista en vivo del PlaylistService;
    /// creamos wrappers visuales para mostrar título + duración + resaltado.
    /// </summary>
    public void SetItems(IReadOnlyList<PlaylistItem> items, int currentIndex)
    {
        // Avalonia 11: Items es read-only. Hay que asignar ItemsSource.
        var panels = new List<StackPanel>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, Spacing = 2 };

            var title = new TextBlock
            {
                Text = item.Title,
                FontWeight = (i == currentIndex) ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = (i == currentIndex)
                    ? Brush.Parse("#ffffff")
                    : Brush.Parse("#e8e8e8"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var dur = new TextBlock
            {
                Text = item.DurationText,
                FontSize = 11,
                Foreground = Brush.Parse("#888888"),
            };
            panel.Children.Add(title);
            panel.Children.Add(dur);
            panels.Add(panel);
        }

        List.ItemsSource = panels;

        if (currentIndex >= 0 && currentIndex < items.Count)
            List.SelectedIndex = currentIndex;
    }

    private void OnListDoubleTapped(object sender, TappedEventArgs e)
    {
        if (List.SelectedIndex >= 0)
            ItemDoubleClicked?.Invoke(List.SelectedIndex);
    }
}

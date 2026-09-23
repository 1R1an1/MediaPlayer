using System;

namespace MediaPlayer.Models;

public class PlaylistItem
{
    public string Path { get; set; }
    public string Title { get; set; }
    public TimeSpan Duration { get; set; }
    public bool IsCurrent { get; set; }

    public string DurationText => (Duration == default) ? "--:--" : FormatTime(Duration.TotalSeconds);

    public PlaylistItem(string path, string title = null, TimeSpan duration = default)
    {
        Path = path;
        Title = title ?? System.IO.Path.GetFileNameWithoutExtension(path);
        Duration = duration;
    }

    /// <summary>Formatea segundos como "m:ss" o "h:mm:ss".</summary>
    public static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? string.Format("{0:h\\:mm\\:ss}", ts) : string.Format("{0:m\\:ss}", ts);
    }
}

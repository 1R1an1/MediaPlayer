using System;

namespace MediaPlayer.Models;

public class PlaylistItem
{
    public string Path { get; set; }
    public string Title { get; set; }
    public TimeSpan Duration { get; set; }
    public bool IsCurrent { get; set; }

    public string DurationText
    {
        get
        {
            if (Duration == default) return "--:--";
            return FormatTime(Duration.TotalSeconds);
        }
    }

    public PlaylistItem(string path, string title = null, TimeSpan duration = default)
    {
        Path = path;
        Title = title ?? System.IO.Path.GetFileNameWithoutExtension(path);
        Duration = duration;
    }

    public static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return string.Format("{0:h\\:mm\\:ss}", ts);
        return string.Format("{0:m\\:ss}", ts);
    }
}

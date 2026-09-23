using System.Collections.Generic;

namespace MediaPlayer.Models;

public class AudioTrack
{
    public int Id { get; set; }
    public string Title { get; set; }
    public string Lang { get; set; }
    public int Channels { get; set; }
    public string Codec { get; set; }
    public bool IsSelected { get; set; }

    public string Display
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Title)) parts.Add(Title);
            else if (!string.IsNullOrEmpty(Lang)) parts.Add(Lang);
            else parts.Add("Track " + Id);

            if (!string.IsNullOrEmpty(Lang) && !string.IsNullOrEmpty(Title)) parts.Add("[" + Lang + "]");
            if (Channels > 0) parts.Add(Channels + "ch");
            return string.Join(" ", parts);
        }
    }

    public override string ToString() => Display;
}

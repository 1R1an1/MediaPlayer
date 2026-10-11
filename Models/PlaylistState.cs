using System.Collections.Generic;

namespace MediaPlayer.Models;

public class PlaylistState
{
    public bool Created = false;
    public IEnumerable<string> OriginalPaths;
    public IEnumerable<int> ItemsOrder;
    public int CurrentIndex;
    public LoopMode LoopMode;
    public bool Shuffle;
}

/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System.Collections.Generic;

namespace MediaPlayer.Models;

public record struct PlaylistState
{
    public bool Created;
    public IEnumerable<string> OriginalPaths;
    public IEnumerable<int> ItemsOrder;
    public int CurrentIndex;
    public LoopMode LoopMode;
    public bool Shuffle;
}

public class PlaylistPublicState
{
    public bool Created = false;
    public IEnumerable<string> OriginalPaths;
    public IEnumerable<int> ItemsOrder;
    public int CurrentIndex;
    public LoopMode LoopMode;
    public bool Shuffle;
    public double PositionSec;
}

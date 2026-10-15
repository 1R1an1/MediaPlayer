/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
namespace MediaPlayer.Models;

public record struct PlayerState
{
    public bool Created;
    public double Volume;
    public double PositionSec;
}


public class PlayerPublicState
{
    public bool Created = false;
    public double Volume;
}

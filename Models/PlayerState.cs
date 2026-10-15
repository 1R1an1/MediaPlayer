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

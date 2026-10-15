using System.Text.Json.Serialization;

namespace MediaPlayer.Models;

public class LastState
{
    [JsonInclude]
    [JsonPropertyName("Playlist")]
    private PlaylistPublicState _playlist;

    [JsonIgnore]
    public PlaylistState Playlist
    {
        get => new PlaylistState
        {
            Created = _playlist.Created,
            CurrentIndex = _playlist.CurrentIndex,
            ItemsOrder = _playlist.ItemsOrder,
            OriginalPaths = _playlist.OriginalPaths,
            LoopMode = _playlist.LoopMode,
            Shuffle = _playlist.Shuffle
        };

        set
        {
            _playlist.Created = value.Created;
            _playlist.CurrentIndex = value.CurrentIndex;
            _playlist.ItemsOrder = value.ItemsOrder;
            _playlist.OriginalPaths = value.OriginalPaths;
            _playlist.LoopMode = value.LoopMode;
            _playlist.Shuffle = value.Shuffle;
        }
    }

    [JsonInclude]
    [JsonPropertyName("Player")]
    private PlayerPublicState _player;

    [JsonIgnore]
    public PlayerState Player
    {
        get => new PlayerState { Created = _player.Created, PositionSec = _playlist.PositionSec, Volume = _player.Volume };
        set
        {
            _player.Created = value.Created;
            _player.Volume = value.Volume;
            _playlist.PositionSec = value.PositionSec;
        }
    }

    [JsonConstructor]
    public LastState(PlaylistPublicState _playlist, PlayerPublicState _player)
    {
        this._playlist = _playlist ?? new() { Created = false };
        this._player = _player ?? new() { Created = false };
    }

    public LastState()
    {
        _player = new() { Created = false };
        _playlist = new() { Created = false };
    }
}

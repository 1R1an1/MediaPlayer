/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System.Linq;
using SharpUtils.Linux;

namespace MediaPlayer.Services;

/// <summary>Lógica de reproducción independiente de la UI.</summary>
public class Player
{
    private MpvPlayer _mpv => App.Mpv;
    private PlaylistService _playlist => App.Playlist;

    public Player()
    {
        _mpv.MPRISNextRequested += Next;
        _mpv.MPRISPrevRequested += Preview;
        _mpv.MPRISVolumeChanged += _mpv.SetVolume01;
        _mpv.MPRISShuffleChanged += s => _playlist.Shuffle = s;

        _playlist.ShuffleChanged += _ => UpdateMprisState();
        _playlist.LoopModeChanged += _ => UpdateMprisState();
        _playlist.CurrentChanged += _ => UpdateMprisState();

        UpdateMprisState();
    }

    public void UpdateMprisState()
    {
        bool hasNext = _playlist.Current != null && _playlist.PeekNext() is var nt && nt >= 0 && nt != _playlist.CurrentIndex;
        bool hasPrev = _playlist.Current != null && _playlist.PeekPrev() is var pv && pv >= 0 && pv != _playlist.CurrentIndex;

        MprisService.capabilities.CanGoNext = hasNext;
        MprisService.capabilities.CanGoPrevious = hasPrev;
        MprisService.capabilities.CanPlay = _playlist.Current != null;
        MprisService.capabilities.CanPause = _playlist.Current != null;
        MprisService.capabilities.CanSeek = _playlist.Current != null;
        MprisService.Update();
    }

    public void PlayPause()
    {
        if (_mpv.CurrentPath == null) return;
        if (_mpv.EofReached) { _mpv.SeekAbsolute(0); _mpv.Play(); }
        else _mpv.PlayPause();
    }

    public void Next() => _playlist.Advance();

    // Si pasaron más de 5s, vuelve al inicio; sino, pista anterior.
    public void Preview()
    {
        if (_mpv.PositionSec > 5) { _mpv.SeekAbsolute(0); return; }
        _playlist.GoPrev();
    }

    public void FrameStep() => _mpv.FrameStep();
    public void FrameBackStep() => _mpv.FrameBackStep();
    public void SeekRelative(double delta) => _mpv.SeekRelative(delta);
    public void VolumeDelta(double delta) => _mpv.SetVolume01Relative(delta);
    public void ToggleMute() => _mpv.ToggleMute();
    public void CycleLoopMode() => _playlist.CycleLoopMode();
    public void ToggleShuffle() => _playlist.Shuffle = !_playlist.Shuffle;

    public void NextAudioTrack()
    {
        if (_mpv.AudioTracks.Count == 0) return;
        var list = _mpv.AudioTracks;
        int idx = list.ToList().FindIndex(t => t.IsSelected);
        _mpv.SetAudioTrack(list[(idx + 1) % list.Count].Id);
    }
}

using System;
using System.Collections.Generic;
using MediaPlayer.Models;

namespace MediaPlayer.Services;

/// <summary>Cola de reproducción. Maneja índice actual, Next/Prev según LoopMode y shuffle.</summary>
public class PlaylistService
{
    private readonly List<PlaylistItem> _items = new List<PlaylistItem>();
    private int _currentIndex = -1;
    private LoopMode _loopMode = LoopMode.None;
    private bool _shuffle;
    private List<int> _shuffleOrder = new List<int>();
    private int _shufflePos = -1;
    private readonly Random _rng = new Random();

    public IReadOnlyList<PlaylistItem> Items => _items;
    public int Count => _items.Count;
    public int CurrentIndex => _currentIndex;
    public PlaylistItem Current => (_currentIndex >= 0 && _currentIndex < _items.Count) ? _items[_currentIndex] : null;

    public LoopMode LoopMode
    {
        get => _loopMode;
        set
        {
            if (_loopMode == value) return;
            _loopMode = value;
            LoopModeChanged?.Invoke(value);
        }
    }

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            if (_shuffle) RebuildShuffleOrder();
            ShuffleChanged?.Invoke(value);
        }
    }

    public event Action Changed;
    public event Action<int> CurrentChanged;
    public event Action<LoopMode> LoopModeChanged;
    public event Action<bool> ShuffleChanged;

    public void Add(string path, string title = null)
    {
        _items.Add(new PlaylistItem(path, title));
        if (_currentIndex < 0) { _currentIndex = 0; CurrentChanged?.Invoke(_currentIndex); }
        if (_shuffle) RebuildShuffleOrder();
        MarkCurrent();
        Changed?.Invoke();
    }

    public void AddRange(IEnumerable<string> paths)
    {
        bool firstLoad = _items.Count == 0;
        foreach (var p in paths) _items.Add(new PlaylistItem(p));
        if (firstLoad && _items.Count > 0) { _currentIndex = 0; CurrentChanged?.Invoke(_currentIndex); }
        if (_shuffle) RebuildShuffleOrder();
        MarkCurrent();
        Changed?.Invoke();
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items.RemoveAt(index);

        if (_currentIndex > index) _currentIndex--;
        else if (_currentIndex == index)
        {
            if (_currentIndex >= _items.Count) _currentIndex = _items.Count - 1;
            CurrentChanged?.Invoke(_currentIndex);
        }
        if (_shuffle) RebuildShuffleOrder();
        MarkCurrent();
        Changed?.Invoke();
    }

    public void Clear()
    {
        _items.Clear();
        _currentIndex = -1;
        _shuffleOrder.Clear();
        _shufflePos = -1;
        CurrentChanged?.Invoke(_currentIndex);
        Changed?.Invoke();
    }

    public void Move(int from, int to)
    {
        if (from < 0 || from >= _items.Count || to < 0 || to >= _items.Count || from == to) return;

        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);

        if (_currentIndex == from) _currentIndex = to;
        else if (from < _currentIndex && to >= _currentIndex) _currentIndex--;
        else if (from > _currentIndex && to <= _currentIndex) _currentIndex++;

        if (_shuffle) RebuildShuffleOrder();
        MarkCurrent();
        Changed?.Invoke();
    }

    public void SetCurrent(int index)
    {
        if (index < 0 || index >= _items.Count || _currentIndex == index) return;
        _currentIndex = index;
        if (_shuffle) _shufflePos = _shuffleOrder.IndexOf(index);
        MarkCurrent();
        CurrentChanged?.Invoke(_currentIndex);
    }

    public int PeekNext()
    {
        if (_items.Count == 0) return -1;
        if (_loopMode == LoopMode.Track && _currentIndex >= 0) return _currentIndex;

        if (_shuffle)
        {
            if (_shuffleOrder.Count == 0) RebuildShuffleOrder();
            int nextPos = _shufflePos + 1;
            if (nextPos >= _shuffleOrder.Count)
            {
                if (_loopMode == LoopMode.Playlist)
                {
                    RebuildShuffleOrder();
                    return _shuffleOrder.Count > 0 ? _shuffleOrder[0] : -1;
                }
                return -1;
            }
            return _shuffleOrder[nextPos];
        }

        int next = _currentIndex + 1;
        if (next < _items.Count) return next;
        return _loopMode == LoopMode.Playlist ? 0 : -1;
    }

    public int PeekPrev()
    {
        if (_items.Count == 0) return -1;
        if (_loopMode == LoopMode.Track && _currentIndex >= 0) return _currentIndex;

        if (_shuffle)
        {
            int prevPos = _shufflePos - 1;
            if (prevPos < 0) return _loopMode == LoopMode.Playlist ? _shuffleOrder[_shuffleOrder.Count - 1] : -1;
            return _shuffleOrder[prevPos];
        }

        int prev = _currentIndex - 1;
        if (prev >= 0) return prev;
        return _loopMode == LoopMode.Playlist ? _items.Count - 1 : -1;
    }

    public int Advance()
    {
        int next = PeekNext();
        if (next < 0) return -1;
        SetCurrent(next);
        return next;
    }

    public int GoPrev()
    {
        int prev = PeekPrev();
        if (prev < 0) return -1;
        SetCurrent(prev);
        return prev;
    }

    public void CycleLoopMode()
    {
        LoopMode = _loopMode switch
        {
            LoopMode.None => LoopMode.Track,
            LoopMode.Track => LoopMode.Playlist,
            _ => LoopMode.None
        };
    }

    private void RebuildShuffleOrder()
    {
        _shuffleOrder.Clear();
        for (int i = 0; i < _items.Count; i++) _shuffleOrder.Add(i);

        // Fisher-Yates
        for (int i = _shuffleOrder.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (_shuffleOrder[i], _shuffleOrder[j]) = (_shuffleOrder[j], _shuffleOrder[i]);
        }

        // El current va primero en el orden shuffle.
        if (_currentIndex >= 0 && _currentIndex < _items.Count)
        {
            _shuffleOrder.Remove(_currentIndex);
            _shuffleOrder.Insert(0, _currentIndex);
            _shufflePos = 0;
        }
        else _shufflePos = -1;
    }

    private void MarkCurrent()
    {
        for (int i = 0; i < _items.Count; i++) _items[i].IsCurrent = (i == _currentIndex);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using MediaPlayer.Models;

namespace MediaPlayer.Services;

/// <summary>Cola de reproducción. Maneja índice actual, Next/Prev según LoopMode y shuffle.</summary>
public class PlaylistService
{
    private readonly List<PlaylistItem> _originalItems = new();
    private readonly List<PlaylistItem> _items = new();
    private int _currentIndex = -1;
    private LoopMode _loopMode = LoopMode.None;
    private bool _shuffle;
    private readonly Random _rng = new();

    public IReadOnlyList<PlaylistItem> Items => _items;
    public int Count => _items.Count;
    public int CurrentIndex => _currentIndex;
    public PlaylistItem Current => (_currentIndex >= 0 && _currentIndex < _items.Count) ? _items[_currentIndex] : null;

    public LoopMode LoopMode
    {
        get => _loopMode;
        set { if (_loopMode != value) _loopMode = value; }
    }

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            ApplyShuffle();
        }
    }

    public event Action Changed;
    public event Action<int> CurrentChanged;

    public void Add(string path, string title = null)
    {
        var item = new PlaylistItem(path, title);
        _originalItems.Add(item);
        _items.Add(item);
        if (_currentIndex < 0) { _currentIndex = 0; CurrentChanged?.Invoke(_currentIndex); }
        if (_shuffle) ApplyShuffle();
        MarkCurrent();
        Changed?.Invoke();
    }

    public void AddRange(IEnumerable<string> paths)
    {
        bool firstLoad = _items.Count == 0;
        foreach (var p in paths)
        {
            var item = new PlaylistItem(p);
            _originalItems.Add(item);
            _items.Add(item);
        }
        if (firstLoad && _items.Count > 0) { _currentIndex = 0; CurrentChanged?.Invoke(_currentIndex); }
        if (_shuffle) ApplyShuffle();
        MarkCurrent();
        Changed?.Invoke();
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        _items.RemoveAt(index);
        _originalItems.Remove(item);

        if (_currentIndex > index) _currentIndex--;
        else if (_currentIndex == index)
        {
            if (_currentIndex >= _items.Count) _currentIndex = _items.Count - 1;
            CurrentChanged?.Invoke(_currentIndex);
        }
        MarkCurrent();
        Changed?.Invoke();
    }

    public void Clear()
    {
        _items.Clear();
        _originalItems.Clear();
        _currentIndex = -1;
        CurrentChanged?.Invoke(_currentIndex);
        Changed?.Invoke();
    }

    public void SetCurrent(int index)
    {
        if (index < 0 || index >= _items.Count || _currentIndex == index) return;
        _currentIndex = index;
        MarkCurrent();
        CurrentChanged?.Invoke(_currentIndex);
    }

    public int PeekNext()
    {
        if (_items.Count == 0) return -1;
        if (_loopMode == LoopMode.Track && _currentIndex >= 0) return _currentIndex;

        int next = _currentIndex + 1;
        if (next < _items.Count) return next;
        return _loopMode == LoopMode.Playlist ? 0 : -1;
    }

    public int PeekPrev()
    {
        if (_items.Count == 0) return -1;
        if (_loopMode == LoopMode.Track && _currentIndex >= 0) return _currentIndex;

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

    /// <summary>
    /// Reordena _items aleatoriamente si _shuffle es true, o restaura el orden
    /// original si es false. Mantiene el item actual seleccionado.
    /// </summary>
    private void ApplyShuffle()
    {
        var current = Current;

        if (_shuffle)
        {
            // Copiar originales y mezclar (Fisher-Yates)
            _items.Clear();
            _items.AddRange(_originalItems);
            for (int i = _items.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_items[i], _items[j]) = (_items[j], _items[i]);
            }

            // Mover el current al principio para que no se repita inmediatamente.
            if (current != null)
            {
                _items.Remove(current);
                _items.Insert(0, current);
                _currentIndex = 0;
            }
        }
        else
        {
            // Restaurar orden original
            _items.Clear();
            _items.AddRange(_originalItems);
        }

        // Actualizar el índice del current en la lista nueva
        if (current != null)
        {
            int newIdx = _items.IndexOf(current);
            if (newIdx >= 0) _currentIndex = newIdx;
        }

        MarkCurrent();
        Changed?.Invoke();
    }

    private void MarkCurrent()
    {
        for (int i = 0; i < _items.Count; i++) _items[i].IsCurrent = (i == _currentIndex);
    }
}

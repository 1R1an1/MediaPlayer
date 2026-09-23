using System;
using System.Collections.Generic;
using MediaPlayer.Models;

namespace MediaPlayer.Services;

/// <summary>
/// Cola de reproducción interna. No usa la playlist nativa de mpv.
/// Maneja índice actual, Next/Prev según LoopMode, y shuffle opcional.
/// </summary>
public class PlaylistService
{
    private readonly List<PlaylistItem> _items = new List<PlaylistItem>();
    private int _currentIndex = -1;
    private LoopMode _loopMode = LoopMode.None;
    private bool _shuffle;
    private List<int> _shuffleOrder = new List<int>();
    private int _shufflePos = -1;
    private Random _rng = new Random();

    public IReadOnlyList<PlaylistItem> Items => _items;
    public int Count => _items.Count;
    public int CurrentIndex => _currentIndex;
    public PlaylistItem Current => (_currentIndex >= 0 && _currentIndex < _items.Count)
        ? _items[_currentIndex]
        : null;
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

    public event Action Changed;                 // la lista cambió (add/remove/clear/reorder)
    public event Action<int> CurrentChanged;     // cambió el item actual (índice)
    public event Action<LoopMode> LoopModeChanged;
    public event Action<bool> ShuffleChanged;

    // ============================================================
    // ====================  Modificación  ========================
    // ============================================================
    public void Add(string path, string title = null)
    {
        var item = new PlaylistItem(path, title);
        _items.Add(item);
        if (_currentIndex < 0)
        {
            _currentIndex = 0;
            CurrentChanged?.Invoke(_currentIndex);
        }
        if (_shuffle) RebuildShuffleOrder();
        MarkCurrent();
        Changed?.Invoke();
    }

    public void AddRange(IEnumerable<string> paths)
    {
        bool firstLoad = _items.Count == 0;
        foreach (var p in paths)
            _items.Add(new PlaylistItem(p));

        if (firstLoad && _items.Count > 0)
        {
            _currentIndex = 0;
            CurrentChanged?.Invoke(_currentIndex);
        }
        if (_shuffle) RebuildShuffleOrder();
        MarkCurrent();
        Changed?.Invoke();
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;

        bool wasCurrent = index == _currentIndex;
        _items.RemoveAt(index);

        if (_currentIndex > index) _currentIndex--;
        else if (_currentIndex == index)
        {
            // si era el current, mantener índice si hay siguiente, sino retroceder
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
        if (from < 0 || from >= _items.Count) return;
        if (to < 0 || to >= _items.Count) return;
        if (from == to) return;

        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);

        // ajustar current
        if (_currentIndex == from) _currentIndex = to;
        else if (from < _currentIndex && to >= _currentIndex) _currentIndex--;
        else if (from > _currentIndex && to <= _currentIndex) _currentIndex++;

        if (_shuffle) RebuildShuffleOrder();
        MarkCurrent();
        Changed?.Invoke();
    }

    public void SetCurrent(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        if (_currentIndex == index) return;
        _currentIndex = index;
        // Resetear posición en shuffle order si aplica
        if (_shuffle)
            _shufflePos = _shuffleOrder.IndexOf(index);
        MarkCurrent();
        CurrentChanged?.Invoke(_currentIndex);
    }

    // ============================================================
    // ================  Navegación (Next/Prev)  ==================
    // ============================================================
    /// <summary>
    /// Devuelve el índice del siguiente item a reproducir, o -1 si no hay.
    /// NO muta estado. El caller (MainWindow) hace SetCurrent + LoadFile.
    /// </summary>
    public int PeekNext()
    {
        if (_items.Count == 0) return -1;

        // Loop track: el mismo índice
        if (_loopMode == LoopMode.Track && _currentIndex >= 0)
            return _currentIndex;

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
        if (_loopMode == LoopMode.Playlist) return 0;
        return -1;
    }

    public int PeekPrev()
    {
        if (_items.Count == 0) return -1;

        // Si ya pasamos más de 5s, prev = principio del actual (comportamiento típico)
        // Eso lo maneja el caller. Acá devolvemos el índice previo real.

        if (_loopMode == LoopMode.Track && _currentIndex >= 0)
            return _currentIndex;

        if (_shuffle)
        {
            int prevPos = _shufflePos - 1;
            if (prevPos < 0)
            {
                if (_loopMode == LoopMode.Playlist)
                    return _shuffleOrder[_shuffleOrder.Count - 1];
                return -1;
            }
            return _shuffleOrder[prevPos];
        }

        int prev = _currentIndex - 1;
        if (prev >= 0) return prev;
        if (_loopMode == LoopMode.Playlist) return _items.Count - 1;
        return -1;
    }

    /// <summary>
    /// Avanza la playlist al siguiente. Llamar cuando termina el archivo actual
    /// o cuando el usuario presiona Next. Devuelve el nuevo índice actual o -1.
    /// </summary>
    public int Advance()
    {
        int next = PeekNext();
        if (next < 0)
        {
            // Sin siguiente: si loop None, parar
            return -1;
        }
        SetCurrent(next);
        return next;
    }

    /// <summary>
    /// Retrocede. Llamar cuando el usuario presiona Prev.
    /// </summary>
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

    // ============================================================
    // ==================  Shuffle helpers  ======================
    // ============================================================
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

        // Asegurar que el current sea el primero del orden shuffle
        if (_currentIndex >= 0 && _currentIndex < _items.Count)
        {
            _shuffleOrder.Remove(_currentIndex);
            _shuffleOrder.Insert(0, _currentIndex);
            _shufflePos = 0;
        }
        else
        {
            _shufflePos = -1;
        }
    }

    private void MarkCurrent()
    {
        for (int i = 0; i < _items.Count; i++)
            _items[i].IsCurrent = (i == _currentIndex);
    }
}

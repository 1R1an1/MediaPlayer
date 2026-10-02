/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaPlayer.Models;
using SharpUtils.Linux;

namespace MediaPlayer.Services;

/// <summary>Cola de reproducción. Maneja índice actual, Next/Prev según LoopMode y shuffle.</summary>
public class PlaylistService
{
    private MpvPlayer _mpv => App.Mpv;

    private readonly List<PlaylistItem> _originalItems = new();
    private readonly ObservableCollection<PlaylistItem> _items = new();
    private int _currentIndex = -1;
    private LoopMode _loopMode = LoopMode.None;
    private bool _shuffle;
    private readonly Random _rng = new();

    public event Action<LoopMode> LoopModeChanged;
    public event Action<bool> ShuffleChanged;
    public event Action<int> CurrentChanged;

    public ReadOnlyObservableCollection<PlaylistItem> Items => new ReadOnlyObservableCollection<PlaylistItem>(_items);
    public int CurrentIndex
    {
        get => _currentIndex;
        private set
        {
            if (_currentIndex == value) return;
            int prev = _currentIndex;
            _currentIndex = value;
            MarkCurrent(prev);
            CurrentChanged?.Invoke(_currentIndex);
        }
    }

    public PlaylistItem Current => (CurrentIndex >= 0 && CurrentIndex < _items.Count) ? _items[CurrentIndex] : null;

    public LoopMode LoopMode
    {
        get => _loopMode;
        private set
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
            _mpv.Shuffle = value;
            ShuffleChanged?.Invoke(value);
            ApplyShuffle();
        }
    }

    public PlaylistService(string[] args)
    {
        _mpv.MPRISLoopChanged += SetLoopMode;
        _mpv.EndReached += () =>
        {
            if (LoopMode == LoopMode.Track)
            {
                _mpv.SeekAbsolute(0);
                _mpv.Play();
            }
            else Advance();
        };
        if (args == null || args.Length < 1) return;
        foreach (var p in args.Where(f => File.Exists(f) || Directory.Exists(f)).Select(Path.GetFullPath).OrderBy(f => f))
        {
            if (Directory.Exists(p))
            {
                foreach (var f in LinuxKRL.GetReadableFiles(p, MainWindow.IsMediaFile).OrderBy(f => f))
                {
                    var file = new PlaylistItem(f);
                    _originalItems.Add(file);
                    _items.Add(file);
                }
                continue;
            }
            var item = new PlaylistItem(p);
            _originalItems.Add(item);
            _items.Add(item);
        }
        Probe();
    }

    public void Add(params IEnumerable<string> paths)
    {
        if (paths == null || paths.Count() < 1) return;
        bool firstLoad = _items.Count == 0;
        var old = _originalItems.Count;
        foreach (var p in paths)
        {
            var item = new PlaylistItem(p);
            _originalItems.Add(item);
            _items.Add(item);
        }
        if (firstLoad && _items.Count > 0) { CurrentIndex = 0; SetCurrent(CurrentIndex); }
        if (_shuffle)
            ApplyShuffle();

        Probe(old, -1);
    }
    public void Move(int oldIndex, int newIndex) => _items.Move(oldIndex, newIndex);


    public void AddNew(IEnumerable<string> paths)
    {
        Clear();
        Add(paths);
        SetCurrent(CurrentIndex);
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _originalItems.Remove(_items[index]);
        _items.RemoveAt(index);

        if (CurrentIndex > index) CurrentIndex--;
        else if (CurrentIndex == index)
        {
            if (CurrentIndex >= _items.Count)
                CurrentIndex = _items.Count - 1;

            if (CurrentIndex >= 0)
            {
                lastLoaded = -1;
                MarkCurrent(CurrentIndex);
                SetCurrent(CurrentIndex);
            }
        }
    }

    public void Clear()
    {
        _items.Clear();
        _originalItems.Clear();
        CurrentIndex = -1;
        lastLoaded = -1;
    }

    private int lastLoaded = -1;
    public void SetCurrent(int index)
    {
        if (index < 0 || index >= _items.Count || lastLoaded == index) return;
        CurrentIndex = index;
        lastLoaded = index;
        _mpv.LoadFile(Current.Path);
    }

    public int PeekNext()
    {
        if (_items.Count == 0) return -1;
        int next = CurrentIndex + 1;
        if (next < _items.Count) return next;
        return _loopMode == LoopMode.Playlist ? 0 : -1;
    }

    public int PeekPrev()
    {
        if (_items.Count == 0) return -1;
        int prev = CurrentIndex - 1;
        if (prev >= 0) return prev;
        return _loopMode == LoopMode.Playlist ? _items.Count - 1 : -1;
    }

    public void Advance()
    {
        int next = PeekNext();
        if (next < 0) return;
        SetCurrent(next);
    }

    public void GoPrev()
    {
        int prev = PeekPrev();
        if (prev < 0) return;
        SetCurrent(prev);
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
            for (int i = _items.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                if (i != j) _items.Move(i, j);
            }
        }
        else
        {
            for (int i = 0; i < _originalItems.Count; i++)
            {
                var item = _originalItems[i];
                int currentPos = _items.IndexOf(item);
                if (currentPos != i) _items.Move(currentPos, i);
            }
        }

        // Actualizar el índice del current en la lista nueva
        if (current != null)
        {
            int index = _items.IndexOf(current);
            if (_shuffle)
            {
                if (index != 0) _items.Move(index, 0);
                CurrentIndex = 0;
            }
            else
                CurrentIndex = index;
        }
    }

    /// <summary>
    /// Extrae duración y cover de cada archivo en un thread aparte.
    /// Dispara Changed después de cada item para que la UI se actualice.
    /// </summary>
    public void Probe(int start = 0, int end = -1)
    {
        var items = _originalItems[start..(end == -1 ? _originalItems.Count : end)];
        _ = Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount + 2 }, async (item, _) =>
        {
            try
            {
                // Duración con ffprobe
                var probe = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffprobe",
                        Arguments = $"-v quiet -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{item.Path}\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                probe.Start();
                string durStr = (await probe.StandardOutput.ReadToEndAsync()).Trim();
                await probe.WaitForExitAsync();

                if (double.TryParse(durStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
                    item.Duration = TimeSpan.FromSeconds(dur);

                // Artista con ffprobe
                var artistProbe = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffprobe",
                        Arguments = $"-v quiet -show_entries format_tags=artist -of default=noprint_wrappers=1:nokey=1 \"{item.Path}\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                artistProbe.Start();
                string artistStr = (await artistProbe.StandardOutput.ReadToEndAsync()).Trim();
                await artistProbe.WaitForExitAsync();
                item.Artist = artistStr;

                // Cover con ffmpeg
                string tempDir = Directory.CreateTempSubdirectory("mpv_pl_").FullName;
                string ext = Path.GetExtension(item.Path).ToLowerInvariant();

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = tempDir,
                };

                string coverFile;
                if (ext == ".mkv")
                {
                    // MKV: cover embebido como attachment
                    psi.Arguments = $"-dump_attachment:t \"\" -i \"{item.Path}\" -y -loglevel quiet";
                    coverFile = Path.Combine(tempDir, "cover.webp");
                }
                else
                {
                    // MP3/FLAC/OGG/M4A: cover embebido como stream de video
                    psi.Arguments = $"-i \"{item.Path}\" -map 0:v:0 -c:v copy -y -loglevel quiet cover.jpg";
                    coverFile = Path.Combine(tempDir, "cover.jpg");
                }

                var extract = new Process { StartInfo = psi };
                extract.Start();
                await extract.WaitForExitAsync();

                if (File.Exists(coverFile))
                    item.CoverBytes = await File.ReadAllBytesAsync(coverFile);

                try { Directory.Delete(tempDir, true); } catch { }
            }
            catch { }
        });
    }

    private void MarkCurrent(int prev)
    {
        if (prev >= 0 && prev < _items.Count)
            _items[prev].IsCurrent = false;
        if (_currentIndex >= 0 && _currentIndex < _items.Count)
            _items[_currentIndex].IsCurrent = true;
    }


    public void CycleLoopMode() => SetLoopMode(_loopMode switch
    {
        LoopMode.None => LoopMode.Track,
        LoopMode.Track => LoopMode.Playlist,
        LoopMode.Playlist => LoopMode.None,
        _ => throw new InvalidCastException()
    });

    public void SetLoopMode(LoopMode mode)
    {
        _mpv.LoopStatus = mode switch
        {
            LoopMode.None => "None",
            LoopMode.Track => "Track",
            LoopMode.Playlist => "Playlist",
            _ => throw new InvalidCastException()
        };
        LoopMode = mode;
    }

    public void SetLoopMode(string mode) => SetLoopMode(mode switch
    {
        "Track" => LoopMode.Track,
        "Playlist" => LoopMode.Playlist,
        "None" => LoopMode.None,
        _ => throw new InvalidCastException()
    });
}

/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using MediaPlayer.Services;

namespace MediaPlayer.Models;

public class PlaylistItem : INotifyPropertyChanged
{
    public string Path { get; set; }

    private string _title;
    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set { _isCurrent = value; OnPropertyChanged(); }
    }

    private string _artist;
    public string Artist
    {
        get => _artist;
        set { _artist = value; OnPropertyChanged(); }
    }

    private byte[] _coverBytes;
    public byte[] CoverBytes
    {
        get => _coverBytes;
        set
        {
            _coverBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CoverMap));
        }
    }

    private byte[] _lastCoverBytes;
    private Bitmap _coverMap;
    public Bitmap CoverMap
    {
        get
        {
            if (_lastCoverBytes != _coverBytes)
            {
                _lastCoverBytes = _coverBytes;
                _coverMap = _coverBytes?.Length > 0 ? new Bitmap(new MemoryStream(_coverBytes)) : null;
            }
            return _coverMap;
        }
    }
    public TimeSpan Duration { set { _durationText = value == default ? "--:--" : MediaUtils.FormatTime(value.TotalSeconds); OnPropertyChanged(nameof(DurationText)); } }

    public string _durationText;
    public string DurationText => _durationText;

    public PlaylistItem(string path, string title = null, TimeSpan duration = default)
    {
        Path = path;
        _title = title ?? System.IO.Path.GetFileNameWithoutExtension(path);
        Duration = duration;
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

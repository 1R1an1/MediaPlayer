using System;
using System.Threading;
using System.Threading.Tasks;
using MediaPlayer.Models;
using MediaPlayer.Services;
using TermFlow.Core;
using TermFlow.Dev;
using TermFlow.Dev.CanvasExt;

namespace MediaPlayer.TUI;

public class BasicTUI
{
    private static MpvPlayer _mpv => App.Mpv;
    private static PlaylistService _playlist => App.Playlist;

    public static void Start(string[] args)
    {
        App.Init(args);
        Engine.Setup();
        Console.WriteLine();

        SemaphoreSlim _renderSignal = new(0, 1);
        int _renderPending = 0;
        double vol = 1, pos = 0, dur = 0;
        string title = "", artist = "";
        bool isPlaying = true, shuffle = false, mute = false;
        LoopMode loop = LoopMode.None;

        void RequestRender()
        {
            if (Interlocked.Exchange(ref _renderPending, 1) == 0)
                _renderSignal.Release();
        }

        _mpv.OnVolumeChanged += v => { vol = v; RequestRender(); };
        _mpv.IsPlayingChanged += p => { isPlaying = p; RequestRender(); };
        _mpv.PositionChanged += p => { pos = p; RequestRender(); };
        _mpv.DurationChanged += d => { dur = d; RequestRender(); };
        _mpv.MetadataChanged += () => { title = _mpv.Title ?? ""; artist = _mpv.Artist ?? ""; RequestRender(); };
        _mpv.MuteChanged += m => { mute = m; RequestRender(); };
        _playlist.LoopModeChanged += l => { loop = l; RequestRender(); };
        _playlist.ShuffleChanged += s => { shuffle = s; RequestRender(); };

        using var canvas = new TermCanvas(5, true, false, 250, (_, _, _) => RequestRender());

        var input = new InputRouter().Bind("", "", _mpv.PlayPause, ConsoleKey.Spacebar, ConsoleKey.K)
        .Bind("", "", () => _mpv.SeekRelative(-5), ConsoleKey.LeftArrow)
        .Bind("", "", () => _mpv.SeekRelative(5), ConsoleKey.RightArrow)
        .Bind("", "", () => _mpv.SetVolume01(vol + 0.05), ConsoleKey.UpArrow)
        .Bind("", "", () => _mpv.SetVolume01(vol - 0.05), ConsoleKey.DownArrow)
        .Bind("", "", _playlist.Advance, ConsoleKey.N)
        .Bind("", "", () => { if (_mpv.PositionSec > 5) { _mpv.SeekAbsolute(0); return; } _playlist.GoPrev(); }, ConsoleKey.B)
        .Bind("", "", _playlist.CycleLoopMode, ConsoleKey.L)
        .Bind("", "", () => _playlist.Shuffle = !_playlist.Shuffle, ConsoleKey.S)
        .Bind("", "", () => Environment.Exit(0), ConsoleKey.Q)
        .Bind("", "", () => _mpv.SetMute(!_mpv.IsMuted), ConsoleKey.M);

        ThreadPool.QueueUserWorkItem(async _ =>
        {
            while (true)
            {
                var keys = InputReader.ReadInput();
                if (keys.Type != InputEventType.None)
                    input.Handle(keys);

                await Task.Delay(50);
            }
        });

        RequestRender();
        while (true)
        {
            _renderSignal.Wait();
            Interlocked.Exchange(ref _renderPending, 0);

            canvas.Resize(Console.WindowWidth, canvas.Height);

            canvas.WriteAtAndClear(0, 0, title);
            canvas.WriteAtAndClear(0, 1, artist);
            string posS = PlaylistItem.FormatTime(pos), durS = PlaylistItem.FormatTime(dur);
            int w = (int)(canvas.Width / 1.5);
            string posB = new string('━', (int)(pos / dur * (w - posS.Length - durS.Length - 2))), durB = ThemeColors.Dim + new string('━', w - posB.Length - posS.Length - durS.Length - 2) + ThemeColors.Reset;
            canvas.WriteAtAndClear(0, 2, $"{posS} {posB}{durB} {durS}");
            string volS = mute ? $"{ThemeColors.Dim}Mute{ThemeColors.Reset}" : $"{(int)(vol * 100)}%";
            string volB = new string('━', (int)(vol * (w - volS.GetVisualLength() - 6))), vol2B = ThemeColors.Dim + new string('━', w - volB.Length - volS.GetVisualLength() - 6) + ThemeColors.Reset;
            canvas.WriteAtAndClear(0, 3, $"vol: {volS} {volB}{vol2B}");
            canvas.WriteAtAndClear(0, 4, $"[{(isPlaying ? "⏸" : "▶")}] [{(loop == LoopMode.None ? $"{ThemeColors.Dim}None{ThemeColors.Reset}" : loop == LoopMode.Playlist ? "L" : "L1")}] Shuffle: {(!shuffle ? ThemeColors.Dim : "")}{shuffle}{ThemeColors.Reset}");
            canvas.Flush();
            Thread.Sleep(200);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia.Threading;
using MediaPlayer.Models;
using MediaPlayer.Native;
using SharpUtils.Linux;

namespace MediaPlayer.Services;

/// <summary>
/// Wrapper sobre libmpv en modo render API. Hereda de MprisSource.
/// </summary>
public class MpvPlayer : MprisSource, IDisposable
{
    private IntPtr _handle;
    private Thread _eventThread;
    private volatile bool _running;
    private LibMpv.MpvWakeupCallback _wakeupCb;
    private bool _eofFired;

    // Estado público (accedido desde el thread de eventos y desde UI).
    public bool EofReached;
    public double PositionSec;
    public double DurationSec;
    public bool IsMuted;
    public string CurrentPath;
    public string MediaTitle;
    public List<AudioTrack> AudioTracks = new List<AudioTrack>();
    public int CurrentAudioId = -1;
    public bool IsInitialized => _handle != IntPtr.Zero;
    public IntPtr MpvHandle => _handle;

    // Eventos de estado. Disparados en el UI thread. Solo los que UI necesita.
    public event Action<bool> IsPlayingChanged;
    public event Action<double> PositionChanged;
    public event Action<double> DurationChanged;
    public event Action<double> VolumeChanged;
    public event Action<bool> MuteChanged;
    public event Action<string> PathChanged;
    public event Action FileLoaded;
    public event Action EndReached;
    public event Action<string> ErrorOccurred;

    // MPRIS nos avisa via estos cuando el escritorio manda Next/Prev/Quit/Raise
    public event Action NextRequested;
    public event Action PrevRequested;
    public event Action QuitRequested;
    public event Action RaiseRequested;

    public void InitForRenderApi()
    {
        if (_handle != IntPtr.Zero)
            throw new InvalidOperationException("MpvPlayer ya está inicializado.");

        _handle = LibMpv.mpv_create();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create() falló. ¿Está instalado libmpv2?");

        // vo=libmpv: mpv no crea ventana, render via render API.
        SetOptionString("vo", "libmpv");
        // Hardware decoding en hardware dedicado (NVDEC/VAAPI), no GPU 3D.
        SetOptionString("hwdec", "vaapis");
        // Scalers livianos para bajar carga de GPU.
        SetOptionString("scale", "bilinear");
        SetOptionString("cscale", "bilinear");
        SetOptionString("dscale", "bilinear");
        SetOptionString("correct-downscaling", "yes");
        SetOptionString("sws-scaler", "bilinear");
        SetOptionString("interpolation", "no");
        SetOptionString("deband", "no");
        SetOptionString("dither-depth", "no");

        // Sin OSC ni input nativo (los manejamos desde Avalonia).
        SetOptionString("osc", "no");
        SetOptionString("osd-level", "0");
        SetOptionString("input-default-bindings", "no");
        SetOptionString("input-vo-keyboard", "no");
        SetOptionString("input-cursor", "no");
        SetOptionString("config", "no");
        SetOptionString("terminal", "no");
        SetOptionString("force-window", "no");
        SetOptionString("audio-display", "no");
        SetOptionString("keep-open", "yes");
        SetOptionString("idle", "yes");
        SetOptionString("ytdl", "no");

        if (LibMpv.mpv_initialize(_handle) < 0)
            throw new InvalidOperationException("mpv_initialize() falló.");

        ObserveProperties();
        StartEventLoop();
    }

    private void ObserveProperties()
    {
        LibMpv.mpv_observe_property(_handle, 0, "time-pos", LibMpv.MPV_FORMAT_DOUBLE);
        LibMpv.mpv_observe_property(_handle, 1, "duration", LibMpv.MPV_FORMAT_DOUBLE);
        LibMpv.mpv_observe_property(_handle, 2, "pause", LibMpv.MPV_FORMAT_FLAG);
        LibMpv.mpv_observe_property(_handle, 3, "volume", LibMpv.MPV_FORMAT_DOUBLE);
        LibMpv.mpv_observe_property(_handle, 4, "speed", LibMpv.MPV_FORMAT_DOUBLE);
        LibMpv.mpv_observe_property(_handle, 5, "mute", LibMpv.MPV_FORMAT_FLAG);
        LibMpv.mpv_observe_property(_handle, 6, "path", LibMpv.MPV_FORMAT_STRING);
        LibMpv.mpv_observe_property(_handle, 7, "media-title", LibMpv.MPV_FORMAT_STRING);
        LibMpv.mpv_observe_property(_handle, 8, "track-list/count", LibMpv.MPV_FORMAT_INT64);
        LibMpv.mpv_observe_property(_handle, 9, "aid", LibMpv.MPV_FORMAT_INT64);
        LibMpv.mpv_observe_property(_handle, 10, "eof-reached", LibMpv.MPV_FORMAT_FLAG);
    }

    private void StartEventLoop()
    {
        _wakeupCb = new LibMpv.MpvWakeupCallback(OnWakeup);
        LibMpv.mpv_set_wakeup_callback(_handle, _wakeupCb, IntPtr.Zero);

        _running = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    private static void OnWakeup(IntPtr ctx) { }

    private void EventLoop()
    {
        while (_running)
        {
            IntPtr evPtr = LibMpv.mpv_wait_event(_handle, -1);
            if (!_running) break;

            var ev = Marshal.PtrToStructure<LibMpv.mpv_event>(evPtr);
            if (ev.event_id == LibMpv.MPV_EVENT_NONE) continue;

            try { HandleEvent(ev); }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Invoke(() => ErrorOccurred?.Invoke("event loop: " + ex.Message));
            }
        }
    }

    private void HandleEvent(LibMpv.mpv_event ev)
    {
        switch (ev.event_id)
        {
            case LibMpv.MPV_EVENT_PROPERTY_CHANGE:
                HandlePropertyChange(ev);
                break;

            case LibMpv.MPV_EVENT_FILE_LOADED:
                LoadTrackList();
                DurationSec = GetDoubleProperty("duration");
                PositionSec = GetDoubleProperty("time-pos");
                Dispatcher.UIThread.Invoke(() =>
                {
                    DurationChanged?.Invoke(DurationSec);
                    PositionChanged?.Invoke(PositionSec);
                    FileLoaded?.Invoke();
                });
                break;

            case LibMpv.MPV_EVENT_END_FILE:
                var ef = Marshal.PtrToStructure<LibMpv.mpv_event_end_file>(ev.data);
                if (ef.reason == LibMpv.MPV_END_FILE_REASON_ERROR)
                {
                    string msg = GetStringProperty("error-string") ?? "unknown error";
                    Dispatcher.UIThread.Invoke(() => ErrorOccurred?.Invoke(msg));
                }
                break;

            case LibMpv.MPV_EVENT_SHUTDOWN:
                _running = false;
                break;
        }
    }

    private void HandlePropertyChange(LibMpv.mpv_event ev)
    {
        var prop = Marshal.PtrToStructure<LibMpv.mpv_event_property>(ev.data);
        string name = Marshal.PtrToStringUTF8(prop.name);

        switch (name)
        {
            case "time-pos":
                if (prop.format == LibMpv.MPV_FORMAT_DOUBLE)
                {
                    PositionSec = Marshal.PtrToStructure<double>(prop.data);
                    PositionUs = (long)(PositionSec * 1_000_000);
                    Dispatcher.UIThread.Invoke(() => PositionChanged?.Invoke(PositionSec));
                }
                break;

            case "duration":
                if (prop.format == LibMpv.MPV_FORMAT_DOUBLE)
                {
                    DurationSec = Marshal.PtrToStructure<double>(prop.data);
                    DurationUs = (long)(DurationSec * 1_000_000);
                    Dispatcher.UIThread.Invoke(() => DurationChanged?.Invoke(DurationSec));
                }
                break;

            case "pause":
                if (prop.format == LibMpv.MPV_FORMAT_FLAG)
                {
                    int val = Marshal.PtrToStructure<int>(prop.data);
                    IsPlaying = val == 0;
                    base.IsPlaying = IsPlaying;
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        IsPlayingChanged?.Invoke(IsPlaying);
                        MprisService.Update();
                    });
                }
                break;

            case "volume":
                if (prop.format == LibMpv.MPV_FORMAT_DOUBLE)
                {
                    double v = Marshal.PtrToStructure<double>(prop.data);
                    Volume = v / 100.0;
                    base.Volume = Volume;
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        VolumeChanged?.Invoke(Volume);
                        MprisService.Update();
                    });
                }
                break;

            case "speed":
                if (prop.format == LibMpv.MPV_FORMAT_DOUBLE)
                {
                    Rate = Marshal.PtrToStructure<double>(prop.data);
                    base.Rate = Rate;
                    Dispatcher.UIThread.Invoke(MprisService.Update);
                }
                break;

            case "mute":
                if (prop.format == LibMpv.MPV_FORMAT_FLAG)
                {
                    int val = Marshal.PtrToStructure<int>(prop.data);
                    IsMuted = val != 0;
                    Dispatcher.UIThread.Invoke(() => MuteChanged?.Invoke(IsMuted));
                }
                break;

            case "path":
                CurrentPath = ReadPropString(prop);
                Dispatcher.UIThread.Invoke(() => PathChanged?.Invoke(CurrentPath));
                break;

            case "media-title":
                MediaTitle = ReadPropString(prop);
                Title = MediaTitle;
                Dispatcher.UIThread.Invoke(MprisService.Update);
                break;

            case "track-list/count":
                LoadTrackList();
                break;

            case "aid":
                if (prop.format == LibMpv.MPV_FORMAT_INT64)
                {
                    CurrentAudioId = (int)Marshal.PtrToStructure<long>(prop.data);
                    foreach (var t in AudioTracks) t.IsSelected = t.Id == CurrentAudioId;
                }
                break;

            case "eof-reached":
                if (prop.format == LibMpv.MPV_FORMAT_FLAG)
                {
                    int eof = Marshal.PtrToStructure<int>(prop.data);
                    EofReached = eof != 0;
                    if (eof != 0 && !_eofFired)
                    {
                        _eofFired = true;
                        Dispatcher.UIThread.Invoke(() => EndReached?.Invoke());
                    }
                    else if (eof == 0) _eofFired = false;
                }
                break;
        }
    }

    private static string ReadPropString(LibMpv.mpv_event_property prop)
    {
        if (prop.format != LibMpv.MPV_FORMAT_STRING) return null;
        IntPtr strPtr = Marshal.ReadIntPtr(prop.data);
        if (strPtr == IntPtr.Zero) return null;
        return Marshal.PtrToStringUTF8(strPtr);
    }

    private void LoadTrackList()
    {
        var tracks = new List<AudioTrack>();
        int count = GetIntProperty("track-list/count");
        int currentAid = GetIntProperty("aid");

        for (int i = 0; i < count; i++)
        {
            string type = GetStringProperty("track-list/" + i + "/type");
            if (type != "audio") continue;

            int id = GetIntProperty("track-list/" + i + "/id");
            tracks.Add(new AudioTrack
            {
                Id = id,
                Title = GetStringProperty("track-list/" + i + "/title"),
                Lang = GetStringProperty("track-list/" + i + "/lang"),
                Channels = GetIntProperty("track-list/" + i + "/demux-channel-count"),
                Codec = GetStringProperty("track-list/" + i + "/codec"),
                IsSelected = id == currentAid
            });
        }

        AudioTracks = tracks;
        CurrentAudioId = currentAid;
    }

    // Control API
    public void LoadFile(string path) { if (IsInitialized) Command("loadfile", path, "replace"); }
    public void PlayPause() => Command("cycle", "pause");
    public void Play() => SetPropertyString("pause", "no");
    public void SetVolume01(double v01) => SetDoubleProperty("volume", Math.Clamp(v01, 0, 1) * 100.0);
    public void SetMute(bool m) => SetPropertyString("mute", m ? "yes" : "no");
    public void SetAudioTrack(int id) => SetIntProperty("aid", id);

    public void SeekAbsolute(double seconds)
        => Command("seek", seconds.ToString("F3", CultureInfo.InvariantCulture), "absolute");

    public void SeekRelative(double delta)
        => Command("seek", delta.ToString("F3", CultureInfo.InvariantCulture), "relative");

    // MprisSource overrides
    public override void TogglePlayPause() => PlayPause();
    public override void Stop() => Command("stop");
    public override void Seek(long offsetUs) => SeekRelative(offsetUs / 1_000_000.0);
    public override void SetPosition(long positionUs) => SeekAbsolute(positionUs / 1_000_000.0);
    public override void OpenUri(string uri) => LoadFile(uri);

    public override void Next() => Dispatcher.UIThread.Invoke(() => NextRequested?.Invoke());
    public override void Prev() => Dispatcher.UIThread.Invoke(() => PrevRequested?.Invoke());
    public override void Quit() => Dispatcher.UIThread.Invoke(() => QuitRequested?.Invoke());
    public override void Raise() => Dispatcher.UIThread.Invoke(() => RaiseRequested?.Invoke());

    // P/Invoke helpers
    private void SetOptionString(string name, string value)
    {
        int err = LibMpv.mpv_set_option_string(_handle, name, value);
        if (err < 0)
            throw new InvalidOperationException($"mpv_set_option_string({name}={value}) failed: {err}");
    }

    public void SetPropertyString(string name, string value)
    {
        if (IsInitialized) LibMpv.mpv_set_property_string(_handle, name, value);
    }

    public void SetIntProperty(string name, long val)
    {
        if (IsInitialized) LibMpv.mpv_set_property(_handle, name, LibMpv.MPV_FORMAT_INT64, ref val);
    }

    public void SetDoubleProperty(string name, double val)
    {
        if (IsInitialized) LibMpv.mpv_set_property(_handle, name, LibMpv.MPV_FORMAT_DOUBLE, ref val);
    }

    public string GetStringProperty(string name)
    {
        if (!IsInitialized) return null;
        return LibMpv.PtrToStringUtf8AndFree(LibMpv.mpv_get_property_string(_handle, name));
    }

    public int GetIntProperty(string name)
    {
        if (!IsInitialized) return -1;
        long val = 0;
        int err = LibMpv.mpv_get_property(_handle, name, LibMpv.MPV_FORMAT_INT64, ref val);
        return err < 0 ? -1 : (int)val;
    }

    public double GetDoubleProperty(string name)
    {
        if (!IsInitialized) return 0;
        double val = 0;
        int err = LibMpv.mpv_get_property(_handle, name, LibMpv.MPV_FORMAT_DOUBLE, ref val);
        return err < 0 ? 0 : val;
    }

    /// <summary>Ejecuta un comando mpv. Ej: Command("loadfile", path, "replace").</summary>
    public void Command(params string[] args)
    {
        if (!IsInitialized || args == null || args.Length == 0) return;

        IntPtr[] ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(args[i] + "\0");
                ptrs[i] = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, ptrs[i], bytes.Length);
            }
            ptrs[args.Length] = IntPtr.Zero;
            LibMpv.mpv_command(_handle, ptrs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
                if (ptrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(ptrs[i]);
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        _running = false;
        try { LibMpv.mpv_wakeup(_handle); } catch { }
        _eventThread?.Join(2000);
        try { LibMpv.mpv_terminate_destroy(_handle); } catch { }
        _handle = IntPtr.Zero;
    }
}

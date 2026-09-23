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
/// Wrapper sobre LibMpv. Inicializa mpv con todo lo nativo deshabilitado
/// (osc, osd, input, config, terminal) — solo renderiza video.
/// Hereda de MprisSource para que MprisService pueda hablar con el escritorio.
/// </summary>
public class MpvPlayer : MprisSource, IDisposable
{
    private IntPtr _handle;
    private Thread _eventThread;
    private volatile bool _running;
    private LibMpv.MpvWakeupCallback _wakeupCb; // keep alive: GC no se lo lleva

    // Estado (accedido desde el thread de eventos y desde UI)
    public double PositionSec;
    public double DurationSec;
    public bool IsMuted;
    public string CurrentPath;
    public string MediaTitle;
    public List<AudioTrack> AudioTracks = new List<AudioTrack>();
    public int CurrentAudioId = -1;
    public bool IsInitialized => _handle != IntPtr.Zero;

    // Eventos (siempre disparados en el UI thread vía Dispatcher.UIThread.Invoke)
    public event Action<bool> IsPlayingChanged;
    public event Action<double> PositionChanged;
    public event Action<double> DurationChanged;
    public event Action<double> VolumeChanged;
    public event Action<double> RateChanged;
    public event Action<bool> MuteChanged;
    public event Action<string> PathChanged;
    public event Action<string> MediaTitleChanged;
    public event Action<List<AudioTrack>> AudioTracksChanged;
    public event Action<int> CurrentAudioTrackChanged;
    public event Action EndReached;
    public event Action<string> ErrorOccurred;
    public event Action FileLoaded;

    // Para que MainWindow reaccione a Next/Prev/Quit/Raise desde MPRIS
    public event Action NextRequested;
    public event Action PrevRequested;
    public event Action QuitRequested;
    public event Action RaiseRequested;

    /// <summary>
    /// Inicializa mpv en modo render API (OpenGL interop). NO pasa wid, así
    /// que mpv no crea su propia window. El caller tiene que crear un
    /// mpv_render_context después y llamar a mpv_render_context_render en
    /// cada frame.
    ///
    /// Útil para integrar mpv en un toolkit de UI sin airspace problem.
    /// </summary>
    public void InitForRenderApi()
    {
        Log("MpvPlayer.InitForRenderApi: START (sin wid, modo render)");
        if (_handle != IntPtr.Zero)
            throw new InvalidOperationException("MpvPlayer ya está inicializado.");

        _handle = LibMpv.mpv_create();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException(
                "mpv_create() falló. ¿Está instalado libmpv2? (apt install libmpv2 / pacman -S mpv)");

        // Deshabilitar todo lo nativo de mpv. El video se renderiza via
        // render API, no via VO propio.
        SetOptionString("vo", "libmpv");

        // Hardware decoding: el DECODE va a hardware dedicado (NVDEC en NVIDIA,
        // VAAPI en Intel/AMD, VideoToolbox en Mac). NO a shaders de GPU
        // integrada. Esto baja MUCHO el uso de GPU integrada porque el frame
        // ya viene decodeado del hardware dedicado.
        SetOptionString("hwdec", "vaapis");

        // Scalers más livianos que lanczos (default). bilinear es el más
        // barato, calidad suficiente para la mayoría de los casos.
        SetOptionString("scale", "bilinear");
        SetOptionString("cscale", "bilinear");
        SetOptionString("dscale", "bilinear");
        SetOptionString("correct-downscaling", "yes");
        // Fallback de scaler por software (cuando hwdec no está disponible)
        SetOptionString("sws-scaler", "bilinear");

        // Sin interpolación de frames ni post-proc pesado
        SetOptionString("interpolation", "no");
        SetOptionString("deband", "no");
        SetOptionString("dither-depth", "no");


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

        // Log interno de mpv a /tmp/mpv.log para debug
        // SetOptionString("log-file", "/tmp/mpv.log");
        // SetOptionString("msg-level", "all=v");

        if (LibMpv.mpv_initialize(_handle) < 0)
            throw new InvalidOperationException("mpv_initialize() falló.");
        Log("MpvPlayer.InitForRenderApi: mpv_initialize OK");

        // Observar propiedades
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

        _wakeupCb = new LibMpv.MpvWakeupCallback(OnWakeup);
        LibMpv.mpv_set_wakeup_callback(_handle, _wakeupCb, IntPtr.Zero);

        _running = true;
        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "mpv-events"
        };
        _eventThread.Start();
    }

    /// <summary>
    /// Handle interno de mpv. Lo usa MpvGlControl para crear el render context.
    /// </summary>
    public IntPtr MpvHandle => _handle;

    private void OnWakeup(IntPtr ctx)
    {
        // mpv llama esto desde un thread propio. No hacemos nada: el thread
        // de eventos ya está bloqueado en wait_event y va a despertar solo.
    }

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        try { System.IO.File.AppendAllText("/tmp/mediaplayer.log", line + "\n"); }
        catch { }
    }

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

            case LibMpv.MPV_EVENT_START_FILE:
                Log("HandleEvent: START_FILE");
                break;

            case LibMpv.MPV_EVENT_FILE_LOADED:
                Log("HandleEvent: FILE_LOADED");
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
                Log($"HandleEvent: END_FILE reason={ef.reason} error={ef.error}");
                if (ef.reason == LibMpv.MPV_END_FILE_REASON_ERROR)
                {
                    string msg = GetStringProperty("error-string") ?? "unknown error";
                    Dispatcher.UIThread.Invoke(() => ErrorOccurred?.Invoke(msg));
                }
                else if (ef.reason == LibMpv.MPV_END_FILE_REASON_EOF)
                {
                    Dispatcher.UIThread.Invoke(() => EndReached?.Invoke());
                }
                break;

            case LibMpv.MPV_EVENT_SHUTDOWN:
                Log("HandleEvent: SHUTDOWN");
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
                    // Sync MPRIS
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
                    // Sync MPRIS
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
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        RateChanged?.Invoke(Rate);
                        MprisService.Update();
                    });
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
                Dispatcher.UIThread.Invoke(() =>
                {
                    MediaTitleChanged?.Invoke(MediaTitle);
                    MprisService.Update();
                });
                break;

            case "track-list/count":
                LoadTrackList();
                break;

            case "aid":
                if (prop.format == LibMpv.MPV_FORMAT_INT64)
                {
                    CurrentAudioId = (int)Marshal.PtrToStructure<long>(prop.data);
                    foreach (var t in AudioTracks)
                        t.IsSelected = t.Id == CurrentAudioId;
                    Dispatcher.UIThread.Invoke(() => CurrentAudioTrackChanged?.Invoke(CurrentAudioId));
                }
                break;
        }
    }

    private static string ReadPropString(LibMpv.mpv_event_property prop)
    {
        if (prop.format != LibMpv.MPV_FORMAT_STRING) return null;
        IntPtr strPtr = Marshal.ReadIntPtr(prop.data);
        if (strPtr == IntPtr.Zero) return null;
        // mpv libera esta string, NO debemos llamar mpv_free
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
        Dispatcher.UIThread.Invoke(() =>
        {
            AudioTracksChanged?.Invoke(tracks);
            CurrentAudioTrackChanged?.Invoke(currentAid);
        });
    }

    // ============================================================
    // ===================  API de control  ======================
    // ============================================================
    public void LoadFile(string path)
    {
        if (!IsInitialized) return;
        Command("loadfile", path, "replace");
    }

    public void LoadFileAppend(string path)
    {
        if (!IsInitialized) return;
        Command("loadfile", path, "append-play");
    }

    public void PlayPause() => Command("cycle", "pause");
    public void Play() => SetPropertyString("pause", "no");
    public void Pause() => SetPropertyString("pause", "yes");

    public void StopPlayback()
    {
        Command("stop");
        PositionSec = 0;
        Dispatcher.UIThread.Invoke(() => PositionChanged?.Invoke(0));
    }

    public void SeekAbsolute(double seconds)
        => Command("seek", seconds.ToString("F3", CultureInfo.InvariantCulture), "absolute");

    public void SeekRelative(double secondsDelta)
        => Command("seek", secondsDelta.ToString("F3", CultureInfo.InvariantCulture), "relative");

    public void SetVolume01(double v01)
    {
        v01 = Math.Clamp(v01, 0.0, 1.0);
        SetDoubleProperty("volume", v01 * 100.0);
    }

    public void SetRate(double r) => SetDoubleProperty("speed", r);
    public void SetMute(bool m) => SetPropertyString("mute", m ? "yes" : "no");
    public void SetAudioTrack(int id) => SetIntProperty("aid", id);

    // ============================================================
    // =============  Overrides de MprisSource  ==================
    // ============================================================
    public override void TogglePlayPause() => PlayPause();
    public override void Stop() => StopPlayback();
    public override void Seek(long offsetUs) => SeekRelative(offsetUs / 1_000_000.0);
    public override void SetPosition(long positionUs) => SeekAbsolute(positionUs / 1_000_000.0);
    public override void OpenUri(string uri) => LoadFile(uri);

    public override void Next() => Dispatcher.UIThread.Invoke(() => NextRequested?.Invoke());
    public override void Prev() => Dispatcher.UIThread.Invoke(() => PrevRequested?.Invoke());
    public override void Quit() => Dispatcher.UIThread.Invoke(() => QuitRequested?.Invoke());
    public override void Raise() => Dispatcher.UIThread.Invoke(() => RaiseRequested?.Invoke());

    // ============================================================
    // =============  Helpers P/Invoke de alto nivel  ============
    // ============================================================
    private void SetOptionString(string name, string value)
    {
        int err = LibMpv.mpv_set_option_string(_handle, name, value);
        if (err < 0)
            throw new InvalidOperationException(
                "mpv_set_option_string(" + name + "=" + value + ") failed: " + err);
    }

    public void SetPropertyString(string name, string value)
    {
        if (!IsInitialized) return;
        LibMpv.mpv_set_property_string(_handle, name, value);
    }

    public void SetIntProperty(string name, long val)
    {
        if (!IsInitialized) return;
        LibMpv.mpv_set_property(_handle, name, LibMpv.MPV_FORMAT_INT64, ref val);
    }

    public void SetDoubleProperty(string name, double val)
    {
        if (!IsInitialized) return;
        LibMpv.mpv_set_property(_handle, name, LibMpv.MPV_FORMAT_DOUBLE, ref val);
    }

    public string GetStringProperty(string name)
    {
        if (!IsInitialized) return null;
        IntPtr ptr = LibMpv.mpv_get_property_string(_handle, name);
        return LibMpv.PtrToStringUtf8AndFree(ptr);
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

    /// <summary>
    /// Ejecuta un comando mpv. Ej: Command("loadfile", "/path/to.mkv", "replace")
    /// </summary>
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
            {
                if (ptrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(ptrs[i]);
            }
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

using System;
using System.Collections.Generic;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MediaPlayer.Models;
using MediaPlayer.Services;

namespace MediaPlayer.Views;

/// <summary>
/// Vista del reproductor: MpvHost (arriba) + panel de controles (abajo).
///
/// Los controles viven DEBAJO del MpvHost en el Grid, no encima. mpv solo
/// dibuja en su child window (que cubre la row 0); los controles Avalonia
/// viven en la row 1, donde mpv no dibuja. No hay superposición → no hay
/// airspace problem → no hace falta ventana flotante.
///
/// Auto-hide: toggle IsVisible en el panel de controles. Cuando está oculto,
/// el Grid le da Height=0 a esa row y el MpvHost se expande a toda la ventana.
/// </summary>
public partial class PlayerView : UserControl
{
    public MpvPlayer Player;
    public bool IsMpvReady;

    private bool _seeking;
    private Timer _hideTimer;
    private bool _layoutReadyFired;
    private int _initAttempts;

    // Eventos de UI para que MainWindow los maneje
    public event Action PrevClicked;
    public event Action NextClicked;
    public event Action PlayPauseClicked;
    public event Action LoopClicked;
    public event Action AudioButtonClicked;
    public event Action<double> VolumeChanged01;
    public event Action<bool> MuteToggled;
    public event Action<double> SeekRequested;
    public event Action VideoDoubleClicked;
    public event Action VideoSingleClicked;

    public PlayerView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Hook UI events (los controles viven en este mismo UserControl ahora)
        PlayPauseBtn.Click += OnPlayPauseClick;
        PrevBtn.Click += (s, ev) => PrevClicked?.Invoke();
        NextBtn.Click += (s, ev) => NextClicked?.Invoke();
        LoopBtn.Click += (s, ev) => LoopClicked?.Invoke();
        AudioBtn.Click += (s, ev) => AudioButtonClicked?.Invoke();
        MuteBtn.Click += (s, ev) => MuteToggled?.Invoke(Player?.IsMuted != true);

        SeekBar.PointerPressed += OnSeekStart;
        SeekBar.PointerReleased += OnSeekEnd;
        SeekBar.PointerMoved += OnSeekMove;
        VolumeSlider.ValueChanged += OnVolumeChanged;

        // Pointer events en el RootGrid para detectar mouse y toggle auto-hide
        RootGrid.PointerMoved += OnRootPointerMoved;
        RootGrid.PointerExited += (s, ev) => HideControls();
        RootGrid.PointerPressed += OnRootPointerPressed;

        // Auto-hide timer (3s)
        _hideTimer = new Timer(3000) { AutoReset = false };
        _hideTimer.Elapsed += (s, ev) => Dispatcher.UIThread.Invoke(HideControls);

        // Arrancar mostrando los controles
        ShowControls();

        // mpv se inicializa después del primer layout pass con size no-cero.
        LayoutUpdated += OnFirstLayout;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _hideTimer?.Stop();
        Player?.Dispose();
        Player = null;
        IsMpvReady = false;
    }

    private void OnFirstLayout(object sender, EventArgs e)
    {
        if (IsMpvReady || _layoutReadyFired) return;
        if (Bounds.Width == 0 || Bounds.Height == 0) return;

        _layoutReadyFired = true;
        LayoutUpdated -= OnFirstLayout;

        Dispatcher.UIThread.Post(InitMpv, DispatcherPriority.Render);
    }

    // ============================================================
    // =====================  mpv init  ===========================
    // ============================================================
    private void InitMpv()
    {
        if (IsMpvReady) return;

        if (VideoHost == null || !VideoHost.IsReady || VideoHost.Handle == IntPtr.Zero
            || Bounds.Width == 0 || Bounds.Height == 0)
        {
            _initAttempts++;
            if (_initAttempts > 50)
            {
                Log("InitMpv: TIMEOUT - no se pudo obtener handle después de 5s");
                return;
            }
            Dispatcher.UIThread.Post(InitMpv, DispatcherPriority.Render);
            return;
        }

        try
        {
            Player = new MpvPlayer();
            Player.Init(VideoHost.Handle);
            IsMpvReady = true;

            Player.IsPlayingChanged += OnIsPlayingChanged;
            Player.PositionChanged += OnPositionChanged;
            Player.DurationChanged += OnDurationChanged;
            Player.VolumeChanged += OnPlayerVolumeChanged;
            Player.MuteChanged += OnPlayerMuteChanged;

            VolumeSlider.Value = Player.Volume;
        }
        catch (Exception ex)
        {
            Log("InitMpv: EXCEPTION - " + ex);
        }
    }

    // ============================================================
    // ====================  Controls panel  ======================
    // ============================================================
    // Auto-hide con IsVisible. Cuando el panel está oculto, el Grid le da
    // Height=0 a esa row y el video se expande a toda la ventana.
    private void ShowControls()
    {
        ControlsPanel.IsVisible = true;
        _hideTimer?.Stop();
        _hideTimer?.Start();
    }

    private void HideControls() => ControlsPanel.IsVisible = false;

    private void OnRootPointerMoved(object sender, PointerEventArgs e)
    {
        // Cualquier movimiento del mouse muestra los controles.
        ShowControls();
    }

    private void OnRootPointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount >= 2)
            VideoDoubleClicked?.Invoke();
        else
            VideoSingleClicked?.Invoke();
        ShowControls();
    }

    // ============================================================
    // =====================  Seek bar  ===========================
    // ============================================================
    private void OnSeekStart(object sender, PointerPressedEventArgs e)
    {
        _seeking = true;
        _hideTimer?.Stop();
    }

    private void OnSeekEnd(object sender, PointerReleasedEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        SeekRequested?.Invoke(SeekBar.Value);
        ShowControls();
    }

    private void OnSeekMove(object sender, PointerEventArgs e) => _hideTimer?.Stop();

    // ============================================================
    // ===================  Player → UI sync  =====================
    // ============================================================
    private void OnIsPlayingChanged(bool playing)
    {
        PlayIcon.IsVisible = !playing;
        PauseIcon.IsVisible = playing;
    }

    private void OnPositionChanged(double sec)
    {
        if (_seeking) return;
        if (Player != null && Player.DurationSec > 0)
            SeekBar.Value = sec;
        CurrentTimeText.Text = PlaylistItem.FormatTime(sec);
    }

    private void OnDurationChanged(double sec)
    {
        SeekBar.Maximum = sec > 0 ? sec : 1;
        DurationText.Text = PlaylistItem.FormatTime(sec);
    }

    private void OnPlayerVolumeChanged(double v01)
    {
        if (!_seeking)
            VolumeSlider.Value = v01;
    }

    private void OnPlayerMuteChanged(bool muted)
    {
        VolIcon.IsVisible = !muted;
        MuteIcon.IsVisible = muted;
    }

    // ============================================================
    // ===================  UI → Player actions  ==================
    // ============================================================
    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        PlayPauseClicked?.Invoke();
        ShowControls();
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
        => VolumeChanged01?.Invoke(VolumeSlider.Value);

    // ============================================================
    // ============  API pública para MainWindow  =================
    // ============================================================
    public void SetLoopModeLabel(LoopMode mode)
    {
        var mutedColor = Brush.Parse("#888888");
        var accentColor = Brush.Parse("#ffffff");

        switch (mode)
        {
            case LoopMode.None:
                LoopLabel.Text = "L";
                LoopLabel.Foreground = mutedColor;
                break;
            case LoopMode.Track:
                LoopLabel.Text = "L1";
                LoopLabel.Foreground = accentColor;
                break;
            case LoopMode.Playlist:
                LoopLabel.Text = "L";
                LoopLabel.Foreground = accentColor;
                break;
        }
    }

    public void ShowAudioMenu(List<AudioTrack> tracks, Action<int> onSelected)
    {
        if (tracks == null || tracks.Count == 0) return;

        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        foreach (var t in tracks)
        {
            var mi = new MenuItem
            {
                Header = t.Display,
                IsChecked = t.IsSelected,
                ToggleType = MenuItemToggleType.Radio,
            };
            int id = t.Id;
            mi.Click += (s, e) => onSelected(id);
            items.Add(mi);
        }
        menu.ItemsSource = items;
        menu.Open(AudioBtn);
    }

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        try { System.IO.File.AppendAllText("/tmp/mediaplayer.log", line + "\n"); }
        catch { }
    }
}

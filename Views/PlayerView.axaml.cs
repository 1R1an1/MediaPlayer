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
/// Vista del reproductor: MpvGlControl (video via OpenGL) + overlay con controles.
///
/// El video se renderiza via OpenGL interop (mpv render API) a una textura que
/// es un control más del árbol visual de Avalonia. Los controles viven encima
/// como overlay. Sin airspace problem: tooltips, controles arriba/abajo/costados,
/// todo funciona como con cualquier control Avalonia.
/// </summary>
public partial class PlayerView : UserControl
{
    public MpvPlayer Player;
    public bool IsMpvReady;

    private bool _seeking;
    private Timer _hideTimer;
    private bool _layoutReadyFired;

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

        RootGrid.PointerMoved += OnRootPointerMoved;
        RootGrid.PointerExited += (s, ev) => HideControls();
        RootGrid.PointerPressed += OnRootPointerPressed;

        _hideTimer = new Timer(3000) { AutoReset = false };
        _hideTimer.Elapsed += (s, ev) => Dispatcher.UIThread.Invoke(HideControls);

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

        try
        {
            Player = new MpvPlayer();
            // Modo render API: mpv renderiza a un FBO nuestro, no a una window.
            // El MpvGlControl se encarga de crear el render context y de
            // llamar a mpv_render_context_render en cada frame.
            Player.InitForRenderApi();

            // Pasarle el handle de mpv al control GL para que cree el render context
            VideoHost.SetMpvHandle(Player.MpvHandle);

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
    // ====================  Controls overlay  ====================
    // ============================================================
    private void ShowControls()
    {
        ControlsOverlay.Opacity = 1;
        _hideTimer?.Stop();
        _hideTimer?.Start();
    }

    private void HideControls() => ControlsOverlay.Opacity = 0;

    private void OnRootPointerMoved(object sender, PointerEventArgs e)
    {
        if (ControlsOverlay.Opacity == 0)
        {
            ShowControls();
            return;
        }

        var pos = e.GetPosition(ControlsOverlay);
        if (pos.X >= 0 && pos.X <= ControlsOverlay.Bounds.Width &&
            pos.Y >= 0 && pos.Y <= ControlsOverlay.Bounds.Height)
        {
            _hideTimer?.Stop();
            return;
        }
        ShowControls();
    }

    private void OnRootPointerPressed(object sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(ControlsOverlay);
        bool onControls = pos.X >= 0 && pos.X <= ControlsOverlay.Bounds.Width &&
                          pos.Y >= 0 && pos.Y <= ControlsOverlay.Bounds.Height;
        if (onControls) return;

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

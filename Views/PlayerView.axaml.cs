using System;
using System.Collections.Generic;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MediaPlayer.Models;
using MediaPlayer.Services;

namespace MediaPlayer.Views;

/// <summary>
/// Vista del reproductor: MpvGlControl + overlay con controles.
/// mpv renderiza via OpenGL a una textura Avalonia; los controles son
/// un Border encima en el mismo Grid. Sin airspace problem.
/// </summary>
public partial class PlayerView : UserControl
{
    public MpvPlayer Player;
    public bool IsMpvReady;

    private Timer _hideTimer;
    private bool _layoutReadyFired;
    private bool _seeking;

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

        PlayPauseBtn.Click += (s, ev) => { PlayPauseClicked?.Invoke(); ShowControls(); };
        PrevBtn.Click += (s, ev) => PrevClicked?.Invoke();
        NextBtn.Click += (s, ev) => NextClicked?.Invoke();
        LoopBtn.Click += (s, ev) => LoopClicked?.Invoke();
        AudioBtn.Click += (s, ev) => AudioButtonClicked?.Invoke();
        MuteBtn.Click += (s, ev) => MuteToggled?.Invoke(Player?.IsMuted != true);

        SeekBar.PointerPressed += OnSeekStart;
        SeekBar.PointerReleased += OnSeekEnd;
        SeekBar.PointerMoved += OnSeekMove;
        VolumeSlider.ValueChanged += (s, ev) => VolumeChanged01?.Invoke(VolumeSlider.Value);

        RootGrid.PointerMoved += OnRootPointerMoved;
        RootGrid.PointerExited += (s, ev) => HideControls();
        RootGrid.PointerPressed += OnRootPointerPressed;

        // Space no activa botones: solo Enter. Así no choca con el atajo global.
        foreach (var btn in new[] { PlayPauseBtn, PrevBtn, NextBtn, LoopBtn, AudioBtn, MuteBtn })
            btn.AddHandler(KeyUpEvent, OnButtonKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);

        _hideTimer = new Timer(3000) { AutoReset = false };
        _hideTimer.Elapsed += (s, ev) => Dispatcher.UIThread.Invoke(HideControls);

        ShowControls();
        LayoutUpdated += OnFirstLayout;
    }

    private void OnButtonKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;
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

    private void InitMpv()
    {
        if (IsMpvReady) return;
        try
        {
            Player = new MpvPlayer();
            Player.InitForRenderApi();
            VideoHost.SetMpvHandle(Player.MpvHandle);
            IsMpvReady = true;

            Player.IsPlayingChanged += p => { PlayIcon.IsVisible = !p; PauseIcon.IsVisible = p; };
            Player.PositionChanged += OnPositionChanged;
            Player.DurationChanged += sec => { SeekBar.Maximum = sec > 0 ? sec : 1; DurationText.Text = PlaylistItem.FormatTime(sec); };
            Player.VolumeChanged += v => { if (!_seeking) VolumeSlider.Value = v; };
            Player.MuteChanged += m => { VolIcon.IsVisible = !m; MuteIcon.IsVisible = m; };

            VolumeSlider.Value = Player.Volume;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("InitMpv: " + ex);
        }
    }

    // Auto-hide de los controles
    private void ShowControls()
    {
        ControlsOverlay.Opacity = 1;
        _hideTimer?.Stop();
        _hideTimer?.Start();
    }

    private void HideControls() => ControlsOverlay.Opacity = 0;

    private void OnRootPointerMoved(object sender, PointerEventArgs e)
    {
        if (ControlsOverlay.Opacity == 0) { ShowControls(); return; }

        // Si el mouse está sobre el overlay, no ocultar.
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

        if (e.ClickCount >= 2) VideoDoubleClicked?.Invoke();
        else VideoSingleClicked?.Invoke();
        ShowControls();
    }

    // Seek manual: capturamos el pointer y calculamos la posición sobre el track.
    private void OnSeekStart(object sender, PointerPressedEventArgs e)
    {
        _seeking = true;
        _hideTimer?.Stop();
        e.Pointer.Capture(SeekBar);
        UpdateSeekBarFromPointer(e);
        e.Handled = true;
    }

    private void OnSeekEnd(object sender, PointerReleasedEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        e.Pointer.Capture(null);
        SeekRequested?.Invoke(SeekBar.Value);
        ShowControls();
        e.Handled = true;
    }

    private void OnSeekMove(object sender, PointerEventArgs e)
    {
        if (!_seeking) return;
        _hideTimer?.Stop();
        UpdateSeekBarFromPointer(e);
        e.Handled = true;
    }

    private void UpdateSeekBarFromPointer(PointerEventArgs e)
    {
        var pos = e.GetPosition(SeekBar);
        double trackWidth = SeekBar.Bounds.Width;
        if (trackWidth <= 0) return;

        double thumbWidth = 12;
        double usable = trackWidth - thumbWidth;
        if (usable <= 0) return;

        double rel = Math.Clamp((pos.X - thumbWidth / 2) / usable, 0, 1);
        SeekBar.Value = SeekBar.Minimum + rel * (SeekBar.Maximum - SeekBar.Minimum);
    }

    private void OnPositionChanged(double sec)
    {
        if (_seeking) return;
        if (Player != null && Player.DurationSec > 0 && Math.Abs(SeekBar.Value - sec) > 0.1)
            SeekBar.Value = sec;
        CurrentTimeText.Text = PlaylistItem.FormatTime(sec);
    }

    // API pública para MainWindow
    public void SetLoopModeLabel(LoopMode mode)
    {
        var muted = Brush.Parse("#888888");
        var accent = Brush.Parse("#ffffff");
        LoopLabel.Foreground = (mode == LoopMode.None) ? muted : accent;
        LoopLabel.Text = (mode == LoopMode.Track) ? "L1" : "L";
    }

    public void ShowAudioMenu(List<AudioTrack> tracks, Action<int> onSelected)
    {
        if (tracks == null || tracks.Count == 0) return;

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
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(AudioBtn);
    }
}

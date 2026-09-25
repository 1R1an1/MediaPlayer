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
    private bool _canHide = true;
    private bool _canHideVolume = true;

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

    private bool IsPointerOverVoluenPopup(Point pos) => VolumePopup.IsVisible ? IsPointerInside(VolumePopup, pos) : false;

    public PlayerView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        PlayPauseBtn.Click += (_, _) => PlayPauseClicked?.Invoke();
        PrevBtn.Click += (_, _) => PrevClicked?.Invoke();
        NextBtn.Click += (_, _) => NextClicked?.Invoke();
        LoopBtn.Click += (_, _) => LoopClicked?.Invoke();
        AudioBtn.Click += (_, _) => AudioButtonClicked?.Invoke();
        MuteBtn.Click += (_, _) => MuteToggled?.Invoke(Player?.IsMuted != true);

        // Seek: Tunnel + handledEventsToo porque el Slider maneja PointerPressed/Released
        // en sus RepeatButtons internos y no siempre los propaga al padre.
        SeekBar.AddHandler(PointerPressedEvent, (_, _) => { if (_seeking) return; _seeking = true; _canHide = false; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) => { if (!_seeking) return; _seeking = false; _canHide = true; SeekRequested?.Invoke(SeekBar.Value); },
            RoutingStrategies.Tunnel, handledEventsToo: true);

        SeekBar.ValueChanged += (_, _) => CurrentTimeText.Text = PlaylistItem.FormatTime(SeekBar.Value);
        VolumeSlider.ValueChanged += (_, _) => VolumeChanged01?.Invoke(VolumeSlider.Value);

        // Hover del botón de volumen → mostrar/ocultar popup.
        VolumeSlider.AddHandler(PointerPressedEvent, (_, _) => { _canHide = false; _canHideVolume = false; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        VolumeSlider.AddHandler(PointerReleasedEvent, (_, _) => { _canHide = true; _canHideVolume = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);

        RootGrid.PointerMoved += OnRootPointerMoved;
        RootGrid.PointerExited += (_, _) => HideControls();
        RootGrid.PointerPressed += OnRootPointerPressed;

        // Space no activa botones (solo Enter), así no choca con el atajo global.
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
            // Mostrar el video cuando mpv carga un archivo, ocultar cuando no hay.
            Player.PathChanged += path => VideoHost.IsVisible = !string.IsNullOrEmpty(path);
            Player.FileLoaded += () => VideoHost.IsVisible = true;

            VolumeSlider.Value = Player.Volume;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("InitMpv: " + ex);
        }
    }

    private void ShowControls()
    {
        ControlsOverlay.Opacity = 1;
        _hideTimer?.Stop();
        _hideTimer?.Start();
    }

    private void HideControls()
    {
        if (_canHide)
        {
            ControlsOverlay.Opacity = 0;
            _hideTimer?.Stop();
        }
    }

    private bool IsPointerInside(Visual visual, Point rootPoint)
    {
        var point = visual.TranslatePoint(new Point(0, 0), RootGrid);
        return point.HasValue && new Rect(point.Value, visual.Bounds.Size).Contains(rootPoint);
    }

    private void OnRootPointerMoved(object sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(RootGrid);

        bool overVolume = !_canHideVolume ||
            IsPointerInside(MuteBtn, pos) ||
            IsPointerOverVoluenPopup(pos);

        VolumePopup.IsVisible = overVolume;

        // Durante el seek no tocamos el overlay (evita flicker durante el drag).
        if (_seeking) return;
        if (ControlsOverlay.Opacity == 0)
            ShowControls();
        else if (IsPointerInside(ControlsOverlay, pos) || IsPointerOverVoluenPopup(pos))
        {
            _canHide = false;
            _hideTimer?.Stop();
        }
        else
        {
            _canHide = true;
            _hideTimer?.Start();
        }
    }

    private void OnRootPointerPressed(object sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(RootGrid);
        bool onControls = IsPointerInside(ControlsOverlay, pos) || IsPointerOverVoluenPopup(pos);
        if (onControls) return;

        if (e.ClickCount >= 2) VideoDoubleClicked?.Invoke();
        else VideoSingleClicked?.Invoke();
        ShowControls();
    }

    private void OnPositionChanged(double sec)
    {
        if (_seeking) return;
        if (Player != null && Player.DurationSec > 0)
            SeekBar.Value = sec;
        CurrentTimeText.Text = PlaylistItem.FormatTime(sec);
    }

    // API pública para MainWindow
    public void SetLoopModeLabel(LoopMode mode)
    {
        var muted = Application.Current.FindResource("MutedBrush") as Brush;
        var accent = Application.Current.FindResource("AccentBrush") as Brush;
        LoopIcon.Fill = mode == LoopMode.None ? muted : accent;
        LoopIcon.IsVisible = mode != LoopMode.Track;
        LoopOneIcon.IsVisible = mode == LoopMode.Track;
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

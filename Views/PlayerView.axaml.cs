/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// </summary>
public partial class PlayerView : UserControl
{
    private MpvPlayer _mpv => App.Mpv;
    private PlaylistService _playlist => App.Playlist;
    private Color? MutedColor = Application.Current.FindResource("MutedColor") as Color?;
    private Color? AccentColor = Application.Current.FindResource("AccentColor") as Color?;

    private System.Timers.Timer _hideTimer;
    private bool _seeking;
    private bool _canHide = true;
    private bool _canHideVolume = true;

    public event Action ToggleFullScreen;

    private bool IsPointerOverVolumePopup(Point pos) => VolumePopup.Opacity == 1 ? IsPointerInside(VolumePopup, pos) : false;

    public PlayerView()
    {
        InitializeComponent();
        VideoHost.IsVisible = _playlist.Items.Count > 0;
        VideoHost.RenderContextReady += _mpv.Reload;
        VideoHost.SetMpvHandle(_mpv.MpvHandle);

        // --- UI --- //
        PlayPauseBtn.Click += (_, _) => PlayPause();
        PrevBtn.Click += (_, _) => Preview();
        NextBtn.Click += (_, _) => Next();
        MuteBtn.Click += (_, _) => _mpv?.SetMute(!_mpv.IsMuted);
        AudioBtn.Click += (_, _) => ShowAudioMenu();
        LoopBtn.Click += (_, _) => _playlist.CycleLoopMode();
        FullScreenBtn.Click += (_, _) => ToggleFullScreen?.Invoke();

        RootGrid.PointerMoved += OnRootPointerMoved;
        RootGrid.PointerExited += (_, _) => HideControls();
        RootGrid.PointerPressed += OnRootPointerPressed;
        RootGrid.PointerWheelChanged += OnRootPointerWheel;

        SeekBar.ValueChanged += (_, _) => CurrentTimeText.Text = PlaylistItem.FormatTime(SeekBar.Value);
        VolumeSlider.ValueChanged += (_, _) => _mpv?.SetVolume01(VolumeSlider.Value);

        // --- MPV-OTHERS --- //
        _mpv.IsPlayingChanged += SetPlayPause;
        _mpv.PositionChanged += OnPositionChanged;
        _mpv.DurationChanged += sec => Dispatcher.UIThread.Invoke(() => { SeekBar.Maximum = sec > 0 ? sec : 1; DurationText.Text = PlaylistItem.FormatTime(sec); });
        _mpv.OnVolumeChanged += SetVolumen;
        _mpv.MuteChanged += async m => { Dispatcher.UIThread.Invoke(() => { VolIcon.IsVisible = !m; MuteIcon.IsVisible = m; }); await ShowOverlay(m ? MuteIcon : VolIcon); };
        _mpv.PathChanged += path => Dispatcher.UIThread.Invoke(() => VideoHost.IsVisible = !string.IsNullOrEmpty(path));
        _mpv.MetadataChanged += () => Dispatcher.UIThread.Invoke(() => TitleVideo.Text = _mpv.Title);
        _playlist.LoopModeChanged += l => Dispatcher.UIThread.Invoke(() => SetLoopModeBtn(l));

        // --- TUNNEL BINDINGS --- //
        SeekBar.AddHandler(PointerPressedEvent, (_, _) => { if (_seeking) return; _seeking = true; _canHide = false; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) => { if (!_seeking) return; _seeking = false; _canHide = true; _mpv?.SeekAbsolute(SeekBar.Value); },
            RoutingStrategies.Tunnel, handledEventsToo: true);

        VolumeSlider.AddHandler(PointerPressedEvent, (_, _) => { _canHide = false; _canHideVolume = false; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        VolumeSlider.AddHandler(PointerReleasedEvent, (_, _) => { _canHide = true; _canHideVolume = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);

        foreach (var btn in new[] { PlayPauseBtn, PrevBtn, NextBtn, LoopBtn, AudioBtn, MuteBtn })
            btn.AddHandler(KeyUpEvent, (_, e) => { if (e.Key == Key.Space) e.Handled = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // --- TIMER --- //
        _hideTimer = new(3000) { AutoReset = false };
        _hideTimer.Elapsed += (_, _) => Dispatcher.UIThread.Invoke(HideControls);

        ShowControls();

        VolumeSlider.Value = _mpv.Volume;
    }

    private void ShowControls()
    {
        ControlsOverlay.Opacity = 1;
        TitleVideo.Opacity = 1;
        _hideTimer?.Stop();
        _hideTimer?.Start();
    }

    private void HideControls()
    {
        if (_canHide)
        {
            ControlsOverlay.Opacity = 0;
            TitleVideo.Opacity = 0;
            _hideTimer?.Stop();
        }
    }

    private CancellationTokenSource volumeCts;
    private async void SetVolumen(double volume)
    {
        volumeCts?.Cancel();
        volumeCts = new();
        var token = volumeCts.Token;

        var s = ((int)(volume * 100)).ToString();
        Dispatcher.UIThread.Invoke(() =>
        {
            VolumeSlider.Value = volume;
            VolumeTB.Text = s.Length == 1 ? s + "  " : s.Length == 2 ? s + " " : s;
            VolumeOverlay.Opacity = 1;
        });
        try
        {
            await Task.Delay(1000, token);
            Dispatcher.UIThread.Invoke(() => VolumeOverlay.Opacity = 0);
        }
        catch (OperationCanceledException) { }
    }

    private async void SetPlayPause(bool state)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            PauseIcon.IsVisible = state;
            PlayIcon.IsVisible = !state;
        });
        await ShowOverlay(state ? PauseIcon : PlayIcon);
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
            IsPointerOverVolumePopup(pos);
        VolumePopup.Opacity = overVolume ? 1 : 0;
        VolumePopup.IsHitTestVisible = overVolume;

        if (_seeking) return;
        if (ControlsOverlay.Opacity == 0)
            ShowControls();
        else if (IsPointerInside(ControlsOverlay, pos) || IsPointerOverVolumePopup(pos))
        {
            _canHide = false;
            _hideTimer?.Stop();
        }
        else
        {
            _canHide = true;
            _hideTimer?.Stop();
            _hideTimer?.Start();
        }
    }

    private void OnRootPointerPressed(object sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(RootGrid);
        bool onControls = IsPointerInside(ControlsOverlay, pos) || IsPointerOverVolumePopup(pos);
        if (onControls || !e.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount >= 2)
            ToggleFullScreen?.Invoke();
        else if (_mpv.CurrentPath != null)
            _mpv.PlayPause();

        ShowControls();
    }

    private void OnRootPointerWheel(object sender, PointerWheelEventArgs e)
    {
        // e.Delta.Y > 0 = scroll arriba, < 0 = scroll abajo
        if (e.Delta.Y > 0)
            _mpv?.SetVolume01(Math.Clamp(VolumeSlider.Value + 0.05, 0, 1));
        else if (e.Delta.Y < 0)
            _mpv?.SetVolume01(Math.Clamp(VolumeSlider.Value - 0.05, 0, 1));
        e.Handled = true;
    }

    private void OnPositionChanged(double sec)
    {
        if (_seeking) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_mpv.DurationSec > 0)
                SeekBar.Value = sec;
            CurrentTimeText.Text = PlaylistItem.FormatTime(sec);
        });
    }

    private async void SetLoopModeBtn(LoopMode mode)
    {
        LoopIcon.CurrentColor = mode == LoopMode.None ? MutedColor : AccentColor;
        LoopIcon.IsVisible = mode != LoopMode.Track;
        LoopOneIcon.IsVisible = mode == LoopMode.Track;
        await ShowOverlay(mode != LoopMode.Track ? LoopIcon : LoopOneIcon);
    }

    private void ShowAudioMenu()
    {
        if (_mpv.AudioTracks.Count < 1) return;

        var items = new List<MenuItem>();
        foreach (var t in _mpv.AudioTracks)
        {
            var mi = new MenuItem
            {
                Header = t.Display,
                IsChecked = t.IsSelected,
                ToggleType = MenuItemToggleType.Radio,
            };
            int id = t.Id;
            mi.Click += (s, e) => _mpv.SetAudioTrack(id);
            items.Add(mi);
        }
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(AudioBtn);
    }

    // ------ Public Methods ------ //

    private CancellationTokenSource overlayCts;
    public async Task ShowOverlay(Avalonia.Svg.Svg source)
    {
        overlayCts?.Cancel();
        overlayCts = new();
        var token = overlayCts.Token;

        Dispatcher.UIThread.Invoke(() =>
        {
            OverlayIcon.CurrentColor = source.CurrentColor;
            OverlayIcon.Path = source.Path;
            OverlayIcon.Opacity = 1;
        });

        try
        {
            await Task.Delay(500, token);
            Dispatcher.UIThread.Invoke(() => { OverlayIcon.Opacity = 0; });
        }
        catch (OperationCanceledException) { }
    }

    public void UpdateFullScreenIcons(bool isFullScreen)
    {
        FullScreenIcon.IsVisible = isFullScreen;
        NoFullScreenIcon.IsVisible = !isFullScreen;
    }

    public void PlayPause()
    {
        if (_mpv.CurrentPath == null) return;
        if (_mpv.EofReached) { _mpv.SeekAbsolute(0); _mpv.Play(); }
        else _mpv.PlayPause();
        ShowControls();
    }

    public void Next() => _playlist.Advance();

    public void Preview()
    {
        if (_mpv.PositionSec > 5) { _mpv.SeekAbsolute(0); return; }
        _playlist.GoPrev();
    }
}

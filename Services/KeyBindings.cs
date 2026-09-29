/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace MediaPlayer.Services;

/// <summary>Atajos de teclado configurables. MainWindow llama Handle(e) en KeyDown.</summary>
public class KeyBindings
{
    private struct Binding
    {
        public Key Key;
        public KeyModifiers Modifiers;
        public Action Callback;
    }

    private readonly List<Binding> _bindings = new List<Binding>();

    public Action TogglePlayPause;
    public Action<double> SeekRelative;  // segundos (positivo = adelante)
    public Action<double> VolumeDelta;   // delta 0..1
    public Action ToggleMute;
    public Action Next;
    public Action Prev;
    public Action CycleLoopMode;
    public Action ToggleFullscreen;
    public Action ExitFullscreen;
    public Action NextAudioTrack;
    public Action TogglePlaylist;
    public Action Quit;

    public KeyBindings()
    {
        Bind(Key.Space, KeyModifiers.None, () => TogglePlayPause?.Invoke());
        Bind(Key.K, KeyModifiers.None, () => TogglePlayPause?.Invoke());
        Bind(Key.N, KeyModifiers.None, () => Next?.Invoke());
        Bind(Key.B, KeyModifiers.None, () => Prev?.Invoke());

        Bind(Key.Left, KeyModifiers.None, () => SeekRelative?.Invoke(-5));
        Bind(Key.Right, KeyModifiers.None, () => SeekRelative?.Invoke(5));
        Bind(Key.Left, KeyModifiers.Shift, () => SeekRelative?.Invoke(-30));
        Bind(Key.Right, KeyModifiers.Shift, () => SeekRelative?.Invoke(30));
        Bind(Key.Left, KeyModifiers.Control, () => SeekRelative?.Invoke(-60));
        Bind(Key.Right, KeyModifiers.Control, () => SeekRelative?.Invoke(60));

        Bind(Key.Up, KeyModifiers.None, () => VolumeDelta?.Invoke(0.05));
        Bind(Key.Down, KeyModifiers.None, () => VolumeDelta?.Invoke(-0.05));
        Bind(Key.OemMinus, KeyModifiers.None, () => VolumeDelta?.Invoke(-0.05));
        Bind(Key.OemPlus, KeyModifiers.None, () => VolumeDelta?.Invoke(0.05));
        Bind(Key.D0, KeyModifiers.None, () => VolumeDelta?.Invoke(0.05));
        Bind(Key.D9, KeyModifiers.None, () => VolumeDelta?.Invoke(-0.05));
        Bind(Key.M, KeyModifiers.None, () => ToggleMute?.Invoke());

        Bind(Key.L, KeyModifiers.None, () => CycleLoopMode?.Invoke());
        Bind(Key.J, KeyModifiers.None, () => NextAudioTrack?.Invoke());

        Bind(Key.F, KeyModifiers.None, () => ToggleFullscreen?.Invoke());
        Bind(Key.Escape, KeyModifiers.None, () => ExitFullscreen?.Invoke());
        Bind(Key.C, KeyModifiers.None, () => TogglePlaylist?.Invoke());
        Bind(Key.Q, KeyModifiers.None, () => Quit?.Invoke());
    }

    public void Bind(Key key, KeyModifiers modifiers, Action callback)
        => _bindings.Add(new Binding { Key = key, Modifiers = modifiers, Callback = callback });

    /// <summary>Devuelve true si matcheó algún binding.</summary>
    public bool Handle(KeyEventArgs e)
    {
        foreach (var b in _bindings)
        {
            if (e.Key == b.Key && e.KeyModifiers == b.Modifiers)
            {
                b.Callback?.Invoke();
                return true;
            }
        }
        return false;
    }
}

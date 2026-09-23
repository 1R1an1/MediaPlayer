using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace MediaPlayer.Services;

/// <summary>
/// Atajos de teclado configurables. Default razonable, el usuario puede reasignar.
/// MainWindow llama a KeyBindings.Handle(e) en su KeyDown; si matchea, ejecuta el
/// Action asociado y devuelve true (handled).
/// </summary>
public class KeyBindings
{
    public delegate void Action();

    // Estructura para registrar bindings: (Key, Modifiers, Callback)
    private readonly List<Binding> _bindings = new List<Binding>();

    private struct Binding
    {
        public Key Key;
        public KeyModifiers Modifiers;
        public System.Action Callback;
    }

    public KeyBindings()
    {
        RegisterDefaults();
    }

    private void RegisterDefaults()
    {
        // Reproducción
        Bind(Key.Space, KeyModifiers.None, () => TogglePlayPause?.Invoke());
        Bind(Key.K, KeyModifiers.None, () => TogglePlayPause?.Invoke());
        Bind(Key.N, KeyModifiers.None, () => Next?.Invoke());
        Bind(Key.B, KeyModifiers.None, () => Prev?.Invoke());

        // Seek
        Bind(Key.Left, KeyModifiers.None, () => SeekRelative?.Invoke(-5));
        Bind(Key.Right, KeyModifiers.None, () => SeekRelative?.Invoke(5));
        Bind(Key.Left, KeyModifiers.Shift, () => SeekRelative?.Invoke(-30));
        Bind(Key.Right, KeyModifiers.Shift, () => SeekRelative?.Invoke(30));
        Bind(Key.Left, KeyModifiers.Control, () => SeekRelative?.Invoke(-60));
        Bind(Key.Right, KeyModifiers.Control, () => SeekRelative?.Invoke(60));

        // Volumen
        Bind(Key.Up, KeyModifiers.None, () => VolumeDelta?.Invoke(0.05));
        Bind(Key.Down, KeyModifiers.None, () => VolumeDelta?.Invoke(-0.05));
        Bind(Key.M, KeyModifiers.None, () => ToggleMute?.Invoke());

        // Modos
        Bind(Key.L, KeyModifiers.None, () => CycleLoopMode?.Invoke());
        Bind(Key.J, KeyModifiers.None, () => NextAudioTrack?.Invoke());

        // Ventana
        Bind(Key.F, KeyModifiers.None, () => ToggleFullscreen?.Invoke());
        Bind(Key.Escape, KeyModifiers.None, () => ExitFullscreen?.Invoke());
        Bind(Key.C, KeyModifiers.None, () => TogglePlaylist?.Invoke());
        Bind(Key.Q, KeyModifiers.None, () => Quit?.Invoke());
    }

    public void Bind(Key key, KeyModifiers modifiers, System.Action callback)
    {
        _bindings.Add(new Binding { Key = key, Modifiers = modifiers, Callback = callback });
    }

    /// <summary>
    /// Llamar desde MainWindow. Devuelve true si matcheó algo.
    /// Compara Key y KeyModifiers exactamente (sin KeyGesture.Matches que a veces
    /// ignora modifiers o tiene comportamientos raros).
    /// </summary>
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

    // ============================================================
    // ==============  Callbacks (los setea MainWindow)  ==========
    // ============================================================
    public Action TogglePlayPause;
    public Action<double> SeekRelative;   // recibe segundos (positivo = adelante, negativo = atrás)
    public Action<double> VolumeDelta;    // recibe delta 0..1
    public Action ToggleMute;
    public Action Next;
    public Action Prev;
    public Action CycleLoopMode;
    public Action ToggleFullscreen;
    public Action ExitFullscreen;
    public Action NextAudioTrack;
    public Action TogglePlaylist;
    public Action Quit;
}

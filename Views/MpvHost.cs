using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MediaPlayer.Views;

/// <summary>
/// Subclass de NativeControlHost que captura el handle del child nativo que
/// Avalonia crea (en Linux/X11 es una DumbWindow child). mpv se reparenta a
/// ese child y dibuja ahí.
///
/// Importante: el handle solo es válido DESPUÉS de que el control se attachea
/// al visual tree y tiene un layout pass con size > 0. PlayerView.InitMpv
/// reintenta hasta que IsReady=true.
/// </summary>
public class MpvHost : NativeControlHost
{
    private IPlatformHandle _handle;

    /// <summary>IntPtr al handle nativo (X11 Window ID en Linux).</summary>
    public IntPtr Handle => _handle?.Handle ?? IntPtr.Zero;

    /// <summary>True si el handle ya está disponible.</summary>
    public bool IsReady => _handle != null;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _handle = base.CreateNativeControlCore(parent);
        Log($"CreateNativeControlCore: child handle = 0x{_handle?.Handle.ToString("x") ?? "null"}, parent = 0x{parent?.Handle.ToString("x") ?? "null"}");
        return _handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Log($"DestroyNativeControlCore: destruyendo handle = 0x{control?.Handle.ToString("x") ?? "null"}");
        base.DestroyNativeControlCore(control);
        _handle = null;
    }

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] MpvHost: {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        try { System.IO.File.AppendAllText("/tmp/mediaplayer.log", line + "\n"); }
        catch { }
    }
}

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using static MediaPlayer.Native.LibMpv;

namespace MediaPlayer.Views;

/// <summary>
/// Control Avalonia que renderiza video de mpv via OpenGL interop.
///
/// En vez de pasarle un wid a mpv (que crea su propia window y genera el
/// airspace problem de X11), usamos la mpv render API: mpv renderiza a un
/// FBO nuestro, y nosotros lo dibujamos como un control más en el árbol
/// visual de Avalonia.
///
/// IMPORTANTE: mpv_render_context_create requiere que el GL context esté
/// "current" en el thread que lo llama. Avalonia solo hace el context current
/// durante OnOpenGlInit y OnOpenGlRender. Por eso la creación del render
/// context se hace DENTRO de esos métodos, nunca desde SetMpvHandle.
/// </summary>
public class MpvGlControl : OpenGlControlBase
{
    private IntPtr _mpvHandle;
    private IntPtr _renderCtx;
    private MpvRenderUpdateCallback _updateCb;
    private MpvGetProcAddressDelegate _getProcCb;
    private bool _renderCtxCreated;
    private bool _mpvHandleSet;

    private mpv_opengl_init_params _initParams;
    private mpv_opengl_fbo _fbo;

    // Tracking de tamaño para detectar resize y forzar re-render del último
    // frame (mpv no re-renderiza solo cuando está pausado, hay que pedirle).
    private int _lastRenderedW, _lastRenderedH;
    private bool _forceRender;

    /// <summary>
    /// Setear el handle de mpv. PlayerView lo llama después de crear el
    /// MpvPlayer. NO crea el render context acá — el GL context no está
    /// current. Pide un frame rendering para que OnOpenGlRender lo cree.
    /// </summary>
    public void SetMpvHandle(IntPtr mpvHandle)
    {
        Log($"SetMpvHandle: 0x{mpvHandle.ToInt64():x}");
        _mpvHandle = mpvHandle;
        _mpvHandleSet = true;
        // Pedir un render para que OnOpenGlRender corra con GL context current
        // y cree el render context si hace falta.
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        Log($"OnOpenGlInit: _mpvHandleSet={_mpvHandleSet}");
        // Si mpv ya está seteado, crear el render context ahora (GL context
        // está current). Si no, OnOpenGlRender se va a encargar.
        if (_mpvHandleSet)
        {
            CreateRenderContext(gl);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Cuando cambia el tamaño del control, forzar un re-render del último
        // frame. Sin esto, si el video está pausado, el FBO nuevo queda en
        // negro hasta que llega un frame nuevo (que nunca llega porque está
        // pausado).
        if (change.Property == BoundsProperty && _renderCtxCreated)
        {
            _forceRender = true;
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        // Crear el render context acá si no se creó todavía. OnOpenGlRender
        // se llama con GL context current, que es lo que mpv necesita.
        if (!_renderCtxCreated && _mpvHandleSet)
        {
            CreateRenderContext(gl);
        }

        if (!_renderCtxCreated || _renderCtx == IntPtr.Zero) return;

        double scale = LayoutHelper.GetLayoutScale(this);
        int w = (int)(Bounds.Width * scale);
        int h = (int)(Bounds.Height * scale);
        if (w <= 0 || h <= 0) return;

        // Detectar cambio de tamaño: forzar re-render del último frame.
        // mpv no re-renderiza solo cuando está pausado, hay que pedirle.
        bool sizeChanged = w != _lastRenderedW || h != _lastRenderedH;

        // Si no cambió el tamaño y no hay flag de force, mirar si mpv tiene
        // un frame nuevo. Si no, salir sin renderizar (ahorra GPU).
        if (!_forceRender && !sizeChanged)
        {
            ulong flags = mpv_render_context_update(_renderCtx);
            if ((flags & MPV_RENDER_UPDATE_FRAME) == 0) return;
        }

        _forceRender = false;
        _lastRenderedW = w;
        _lastRenderedH = h;

        _fbo = new mpv_opengl_fbo { fbo = fb, w = w, h = h, internal_format = 0 };

        var renderParams = new mpv_render_param[3];
        renderParams[0].type = MPV_RENDER_PARAM_OPENGL_FBO;
        renderParams[0].data = Marshal.AllocHGlobal(Marshal.SizeOf<mpv_opengl_fbo>());
        Marshal.StructureToPtr(_fbo, renderParams[0].data, false);

        int flipY = 1;
        renderParams[1].type = MPV_RENDER_PARAM_FLIP_Y;
        renderParams[1].data = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(renderParams[1].data, flipY);

        renderParams[2].type = MPV_RENDER_PARAM_INVALID;
        renderParams[2].data = IntPtr.Zero;

        try
        {
            int err = mpv_render_context_render(_renderCtx, renderParams);
            if (err < 0)
                Log($"OnOpenGlRender: render → err={err}");
            mpv_render_context_report_swap(_renderCtx);
        }
        finally
        {
            Marshal.FreeHGlobal(renderParams[0].data);
            Marshal.FreeHGlobal(renderParams[1].data);
        }
    }

    /// <summary>
    /// Crea el mpv_render_context. DEBE llamarse con GL context current
    /// (es decir, desde OnOpenGlInit o OnOpenGlRender).
    /// </summary>
    private void CreateRenderContext(GlInterface gl)
    {
        if (_renderCtxCreated) return;
        if (_mpvHandle == IntPtr.Zero) return;

        Log("CreateRenderContext: creando...");

        _getProcCb = new MpvGetProcAddressDelegate((ctx, name) =>
        {
            var ptr = gl.GetProcAddress(name);
            return ptr;
        });

        _initParams = new mpv_opengl_init_params
        {
            get_proc_address = Marshal.GetFunctionPointerForDelegate(_getProcCb),
            get_proc_address_ctx = IntPtr.Zero,
        };

        string apiType = MPV_RENDER_API_TYPE_OPENGL;
        var paramsArray = new mpv_render_param[3];
        paramsArray[0].type = MPV_RENDER_PARAM_API_TYPE;
        paramsArray[0].data = Marshal.StringToHGlobalAnsi(apiType);
        paramsArray[1].type = MPV_RENDER_PARAM_OPENGL_INIT_PARAMS;
        paramsArray[1].data = Marshal.AllocHGlobal(Marshal.SizeOf<mpv_opengl_init_params>());
        Marshal.StructureToPtr(_initParams, paramsArray[1].data, false);
        paramsArray[2].type = MPV_RENDER_PARAM_INVALID;
        paramsArray[2].data = IntPtr.Zero;

        try
        {
            int err = mpv_render_context_create(ref _renderCtx, _mpvHandle, paramsArray);
            Log($"CreateRenderContext: mpv_render_context_create → err={err} ctx=0x{_renderCtx.ToInt64():x}");
            if (err < 0) return;
            _renderCtxCreated = true;

            _updateCb = new MpvRenderUpdateCallback(OnMpvUpdate);
            mpv_render_context_set_update_callback(_renderCtx, _updateCb, IntPtr.Zero);
            Log("CreateRenderContext: OK");
        }
        finally
        {
            Marshal.FreeHGlobal(paramsArray[0].data);
            Marshal.FreeHGlobal(paramsArray[1].data);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        Log("OnOpenGlDeinit");
        if (_renderCtxCreated && _renderCtx != IntPtr.Zero)
        {
            mpv_render_context_free(_renderCtx);
            _renderCtx = IntPtr.Zero;
            _renderCtxCreated = false;
        }
    }

    private void OnMpvUpdate(IntPtr cb_ctx)
    {
        //Log("OnMpvUpdate: frame disponible");
        Dispatcher.UIThread.Post(RequestNextFrameRendering);
    }

    private static class LayoutHelper
    {
        public static double GetLayoutScale(Avalonia.Layout.Layoutable control)
        {
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(control);
            return topLevel?.RenderScaling ?? 1.0;
        }
    }

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] MpvGlControl: {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        try { System.IO.File.AppendAllText("/tmp/mediaplayer.log", line + "\n"); }
        catch { }
    }
}

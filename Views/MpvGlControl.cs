using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using static MediaPlayer.Native.LibMpv;

namespace MediaPlayer.Views;

/// <summary>
/// Control Avalonia que renderiza video de mpv via OpenGL interop.
/// mpv_render_context_create requiere GL context current, por eso se crea
/// dentro de OnOpenGlInit/OnOpenGlRender, nunca desde SetMpvHandle.
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

    // Tracking de tamaño: si cambia, forzamos re-render del último frame
    // (mpv no lo hace solo cuando está pausado).
    private int _lastW, _lastH;
    private bool _forceRender;

    public void SetMpvHandle(IntPtr mpvHandle)
    {
        _mpvHandle = mpvHandle;
        _mpvHandleSet = true;
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        if (_mpvHandleSet) CreateRenderContext(gl);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // En resize, forzar re-render del último frame (sino FBO nuevo queda negro).
        if (change.Property == BoundsProperty && _renderCtxCreated)
        {
            _forceRender = true;
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_renderCtxCreated && _mpvHandleSet)
            CreateRenderContext(gl);

        if (!_renderCtxCreated || _renderCtx == IntPtr.Zero) return;

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = (int)(Bounds.Width * scale);
        int h = (int)(Bounds.Height * scale);
        if (w <= 0 || h <= 0) return;

        bool sizeChanged = w != _lastW || h != _lastH;

        // Si no cambió el tamaño y no hay force, solo renderizar si hay frame nuevo.
        if (!_forceRender && !sizeChanged)
        {
            ulong flags = mpv_render_context_update(_renderCtx);
            if ((flags & MPV_RENDER_UPDATE_FRAME) == 0) return;
        }

        _forceRender = false;
        _lastW = w;
        _lastH = h;

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
            mpv_render_context_render(_renderCtx, renderParams);
            mpv_render_context_report_swap(_renderCtx);
        }
        finally
        {
            Marshal.FreeHGlobal(renderParams[0].data);
            Marshal.FreeHGlobal(renderParams[1].data);
        }
    }

    private void CreateRenderContext(GlInterface gl)
    {
        if (_renderCtxCreated || _mpvHandle == IntPtr.Zero) return;

        _getProcCb = new MpvGetProcAddressDelegate((ctx, name) => gl.GetProcAddress(name));

        _initParams = new mpv_opengl_init_params
        {
            get_proc_address = Marshal.GetFunctionPointerForDelegate(_getProcCb),
            get_proc_address_ctx = IntPtr.Zero,
        };

        var p = new mpv_render_param[3];
        p[0].type = MPV_RENDER_PARAM_API_TYPE;
        p[0].data = Marshal.StringToHGlobalAnsi(MPV_RENDER_API_TYPE_OPENGL);
        p[1].type = MPV_RENDER_PARAM_OPENGL_INIT_PARAMS;
        p[1].data = Marshal.AllocHGlobal(Marshal.SizeOf<mpv_opengl_init_params>());
        Marshal.StructureToPtr(_initParams, p[1].data, false);
        p[2].type = MPV_RENDER_PARAM_INVALID;
        p[2].data = IntPtr.Zero;

        try
        {
            int err = mpv_render_context_create(ref _renderCtx, _mpvHandle, p);
            if (err < 0) return;
            _renderCtxCreated = true;

            _updateCb = new MpvRenderUpdateCallback(OnMpvUpdate);
            mpv_render_context_set_update_callback(_renderCtx, _updateCb, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(p[0].data);
            Marshal.FreeHGlobal(p[1].data);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_renderCtxCreated && _renderCtx != IntPtr.Zero)
        {
            mpv_render_context_free(_renderCtx);
            _renderCtx = IntPtr.Zero;
            _renderCtxCreated = false;
        }
    }

    // mpv nos llama desde su thread cuando hay un frame nuevo.
    private void OnMpvUpdate(IntPtr cb_ctx)
        => Dispatcher.UIThread.Post(RequestNextFrameRendering);
}

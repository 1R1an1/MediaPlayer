/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using static MediaPlayer.Native.LibMpv;
using static MediaPlayer.Native.LibGl;
using Avalonia.Controls;

namespace MediaPlayer.Views;

/// <summary>
/// Renderiza video mpv en un thread dedicado con GLX context compartido.
/// El UI thread solo blitea el FBO final.
/// </summary>
public class MpvGlControl : OpenGlControlBase
{
    private IntPtr _mpvHandle;
    private IntPtr _renderCtx;
    private MpvRenderUpdateCallback _updateCb;
    private MpvGetProcAddressDelegate _getProcCb;
    private bool _renderCtxCreated;
    private bool _mpvHandleSet;

    // GLX
    private IntPtr _glxDisplay;
    private IntPtr _glxSharedContext;
    private IntPtr _glxPbuffer;
    private IntPtr _glxFbConfig;

    // Double-buffer: 2 texturas compartidas + 2 FBOs por thread
    private readonly int[] _sharedTexA = new int[1], _sharedTexB = new int[1];
    private readonly int[] _renderFboA = new int[1], _renderFboB = new int[1];
    private readonly int[] _readFboA = new int[1], _readFboB = new int[1];
    private int _sharedW, _sharedH;
    private readonly object _fboLock = new();

    // Swap state: _writeIdx pinta, _readyIdx lee
    private volatile int _writeIdx = 0, _readyIdx = -1;
    private readonly object _swapLock = new();

    // Fence sync por textura
    private readonly IntPtr[] _fences = new IntPtr[2];
    private readonly object _fenceLock = new();

    // Render thread
    private Thread _renderThread;
    private volatile bool _rendering;
    private readonly AutoResetEvent _renderSignal = new(false);

    private int _lastW, _lastH;

    public void SetMpvHandle(IntPtr mpvHandle)
    {
        _mpvHandle = mpvHandle;
        _mpvHandleSet = true;
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _glxDisplay = glXGetCurrentDisplay();
        IntPtr currentCtx = glXGetCurrentContext();

        if (_glxDisplay != IntPtr.Zero && currentCtx != IntPtr.Zero && CreateContextAttribsARB != null)
        {
            CreateSharedContextAndPbuffer(currentCtx);
            if (_glxSharedContext != IntPtr.Zero)
                StartRenderThread();
        }

        if (_mpvHandleSet) CreateRenderContext(gl);
    }

    private void CreateSharedContextAndPbuffer(IntPtr shareContext)
    {
        IntPtr screenPtr = XDefaultScreenOfDisplay(_glxDisplay);
        int screen = XScreenNumberOfScreen(screenPtr);

        int[] fbAttrs = {
            GLX_X_RENDERABLE, 1,
            GLX_DRAWABLE_TYPE, GLX_PBUFFER_BIT,
            GLX_RENDER_TYPE, GLX_RGBA_BIT,
            GLX_RED_SIZE, 8, GLX_GREEN_SIZE, 8, GLX_BLUE_SIZE, 8, GLX_ALPHA_SIZE, 8,
            GLX_DEPTH_SIZE, 24, GLX_DOUBLEBUFFER, 0,
            0
        };

        IntPtr configPtr = glXChooseFBConfig(_glxDisplay, screen, fbAttrs, out int n);
        if (n == 0 || configPtr == IntPtr.Zero) return;

        _glxFbConfig = Marshal.ReadIntPtr(configPtr);
        XFree(configPtr);

        int[] pbAttrs = { GLX_PBUFFER_WIDTH, 1, GLX_PBUFFER_HEIGHT, 1, GLX_PRESERVED_CONTENTS, 1, 0 };
        _glxPbuffer = glXCreatePbuffer(_glxDisplay, _glxFbConfig, pbAttrs);
        if (_glxPbuffer == IntPtr.Zero) return;

        int[] ctxAttrs = {
            GLX_CONTEXT_MAJOR_VERSION_ARB, 3,
            GLX_CONTEXT_MINOR_VERSION_ARB, 0,
            GLX_CONTEXT_PROFILE_MASK_ARB, GLX_CONTEXT_CORE_PROFILE_BIT_ARB,
            0
        };
        _glxSharedContext = CreateContextAttribsARB(_glxDisplay, _glxFbConfig, shareContext, true, ctxAttrs);
    }

    [DllImport("libX11.so.6")]
    private static extern void XFree(IntPtr data);

    private void StartRenderThread()
    {
        _rendering = true;
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "mpv-render" };
        _renderThread.Start();
    }

    private void RenderLoop()
    {
        if (!glXMakeCurrent(_glxDisplay, _glxPbuffer, _glxSharedContext)) return;

        // Pre-asignar render params (se reutilizan en todos los frames)
        IntPtr fboDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<mpv_opengl_fbo>());
        IntPtr flipYPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(flipYPtr, 1); // flipY nunca cambia

        var renderParams = new mpv_render_param[3];
        renderParams[0].type = MPV_RENDER_PARAM_OPENGL_FBO;
        renderParams[0].data = fboDataPtr;
        renderParams[1].type = MPV_RENDER_PARAM_FLIP_Y;
        renderParams[1].data = flipYPtr;
        renderParams[2].type = MPV_RENDER_PARAM_INVALID;
        renderParams[2].data = IntPtr.Zero;

        int lastW = 0, lastH = 0;
        while (_rendering)
        {
            _renderSignal.WaitOne(100);
            if (!_renderCtxCreated || _renderCtx == IntPtr.Zero) continue;

            int w, h;
            lock (_fboLock)
            {
                if (_sharedTexA[0] == 0) continue;
                w = _sharedW;
                h = _sharedH;
            }

            // Recrear FBOs del render thread si cambió el tamaño
            if (w != lastW || h != lastH)
            {
                if (_renderFboA[0] != 0) glDeleteFramebuffers(1, _renderFboA);
                if (_renderFboB[0] != 0) glDeleteFramebuffers(1, _renderFboB);

                glGenFramebuffers(1, _renderFboA);
                glBindFramebuffer(GL_FRAMEBUFFER, _renderFboA[0]);
                glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _sharedTexA[0], 0);

                glGenFramebuffers(1, _renderFboB);
                glBindFramebuffer(GL_FRAMEBUFFER, _renderFboB[0]);
                glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _sharedTexB[0], 0);

                glBindFramebuffer(GL_FRAMEBUFFER, 0);
                lastW = w;
                lastH = h;
                glFlush();
            }

            ulong flags = mpv_render_context_update(_renderCtx);
            if ((flags & MPV_RENDER_UPDATE_FRAME) == 0) continue;

            int writeIdx = _writeIdx;
            int fbo = (writeIdx == 0 ? _renderFboA : _renderFboB)[0];
            if (fbo == 0) continue;

            // Actualizar solo el fbo ID y dimensiones (lo demás ya está pre-seteado)
            Marshal.StructureToPtr(new mpv_opengl_fbo { fbo = fbo, w = w, h = h, internal_format = 0 }, fboDataPtr, false);

            mpv_render_context_render(_renderCtx, renderParams);
            mpv_render_context_report_swap(_renderCtx);

            var newFence = glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
            lock (_fenceLock)
            {
                if (_fences[writeIdx] != IntPtr.Zero) glDeleteSync(_fences[writeIdx]);
                _fences[writeIdx] = newFence;
            }

            lock (_swapLock)
            {
                _readyIdx = writeIdx;
                _writeIdx = 1 - writeIdx;
            }

            Dispatcher.UIThread.Post(RequestNextFrameRendering);
        }

        Marshal.FreeHGlobal(fboDataPtr);
        Marshal.FreeHGlobal(flipYPtr);
        glXMakeCurrent(_glxDisplay, IntPtr.Zero, IntPtr.Zero);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_renderCtxCreated) return;

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = (int)(Bounds.Width * scale);
        int h = (int)(Bounds.Height * scale);
        if (w <= 0 || h <= 0) return;

        if (w != _lastW || h != _lastH || _sharedTexA[0] == 0)
        {
            CreateSharedResources(w, h);
            _lastW = w;
            _lastH = h;
        }

        int readyIdx;
        lock (_swapLock) readyIdx = _readyIdx;
        if (readyIdx < 0) return;

        // Esperar fence (non-blocking)
        IntPtr fence;
        lock (_fenceLock) fence = _fences[readyIdx];
        if (fence != IntPtr.Zero)
        {
            int result = glClientWaitSync(fence, GL_SYNC_FLUSH_COMMANDS_BIT, 0);
            if (result == GL_ALREADY_SIGNALED || result == GL_CONDITION_SATISFIED)
            {
                lock (_fenceLock) if (_fences[readyIdx] == fence) _fences[readyIdx] = IntPtr.Zero;
                glDeleteSync(fence);
            }
        }

        // Blitear: readFbo (textura lista) → fb (FBO de Avalonia)
        int readFbo = (readyIdx == 0 ? _readFboA : _readFboB)[0];
        glBindFramebuffer(GL_READ_FRAMEBUFFER, readFbo);
        glBindFramebuffer(GL_DRAW_FRAMEBUFFER, fb);
        glBlitFramebuffer(0, 0, _sharedW, _sharedH,
                          0, 0, _sharedW, _sharedH,
                          GL_COLOR_BUFFER_BIT, GL_LINEAR);
        glBindFramebuffer(GL_READ_FRAMEBUFFER, 0);
        glBindFramebuffer(GL_DRAW_FRAMEBUFFER, 0);
    }

    private void CreateSharedResources(int w, int h)
    {
        lock (_fboLock)
        {
            if (_sharedTexA[0] != 0) glDeleteTextures(1, _sharedTexA);
            if (_sharedTexB[0] != 0) glDeleteTextures(1, _sharedTexB);
            if (_readFboA[0] != 0) glDeleteFramebuffers(1, _readFboA);
            if (_readFboB[0] != 0) glDeleteFramebuffers(1, _readFboB);

            lock (_swapLock) { _writeIdx = 0; _readyIdx = -1; }

            // Crear texturas compartidas
            glGenTextures(1, _sharedTexA);
            glBindTexture(GL_TEXTURE_2D, _sharedTexA[0]);
            glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, IntPtr.Zero);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);

            glGenTextures(1, _sharedTexB);
            glBindTexture(GL_TEXTURE_2D, _sharedTexB[0]);
            glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, IntPtr.Zero);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            glBindTexture(GL_TEXTURE_2D, 0);

            // FBOs del UI thread pre-attachados
            glGenFramebuffers(1, _readFboA);
            glBindFramebuffer(GL_FRAMEBUFFER, _readFboA[0]);
            glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _sharedTexA[0], 0);

            glGenFramebuffers(1, _readFboB);
            glBindFramebuffer(GL_FRAMEBUFFER, _readFboB[0]);
            glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _sharedTexB[0], 0);

            glBindFramebuffer(GL_FRAMEBUFFER, 0);
            _sharedW = w;
            _sharedH = h;
            glFlush();
        }

        _renderSignal.Set();
    }

    private void CreateRenderContext(GlInterface gl)
    {
        if (_renderCtxCreated || _mpvHandle == IntPtr.Zero) return;

        _getProcCb = new MpvGetProcAddressDelegate((ctx, name) => gl.GetProcAddress(name));

        var initParams = new mpv_opengl_init_params
        {
            get_proc_address = Marshal.GetFunctionPointerForDelegate(_getProcCb),
            get_proc_address_ctx = IntPtr.Zero,
        };

        var p = new mpv_render_param[3];
        p[0].type = MPV_RENDER_PARAM_API_TYPE;
        p[0].data = Marshal.StringToHGlobalAnsi(MPV_RENDER_API_TYPE_OPENGL);
        p[1].type = MPV_RENDER_PARAM_OPENGL_INIT_PARAMS;
        p[1].data = Marshal.AllocHGlobal(Marshal.SizeOf<mpv_opengl_init_params>());
        Marshal.StructureToPtr(initParams, p[1].data, false);
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

    private void OnMpvUpdate(IntPtr cb_ctx) => _renderSignal.Set();

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _rendering = false;
        _renderSignal?.Set();
        _renderThread?.Join(2000);

        lock (_fboLock)
        {
            if (_sharedTexA[0] != 0) glDeleteTextures(1, _sharedTexA);
            if (_sharedTexB[0] != 0) glDeleteTextures(1, _sharedTexB);
            if (_readFboA[0] != 0) glDeleteFramebuffers(1, _readFboA);
            if (_readFboB[0] != 0) glDeleteFramebuffers(1, _readFboB);
        }

        lock (_fenceLock)
        {
            for (int i = 0; i < 2; i++)
            {
                if (_fences[i] != IntPtr.Zero) glDeleteSync(_fences[i]);
                _fences[i] = IntPtr.Zero;
            }
        }

        if (_renderCtxCreated && _renderCtx != IntPtr.Zero)
        {
            mpv_render_context_free(_renderCtx);
            _renderCtx = IntPtr.Zero;
            _renderCtxCreated = false;
        }

        if (_glxPbuffer != IntPtr.Zero)
        {
            glXDestroyPbuffer(_glxDisplay, _glxPbuffer);
            _glxPbuffer = IntPtr.Zero;
        }

        if (_glxSharedContext != IntPtr.Zero)
        {
            glXDestroyContext(_glxDisplay, _glxSharedContext);
            _glxSharedContext = IntPtr.Zero;
        }
    }
}

/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Runtime.InteropServices;

namespace MediaPlayer.Native;

/// <summary>
/// P/Invoke a libGL.so.1 + libX11.so.6 para Linux/X11.
/// Incluye GLX para crear contextos compartidos off-thread.
/// </summary>
internal static class LibGl
{
    private const string GlLib = "libGL.so.1";
    private const string XLib = "libX11.so.6";

    // GLX FBConfig attribs
    public const int GLX_X_RENDERABLE = 0x8012;
    public const int GLX_DRAWABLE_TYPE = 0x8010;
    public const int GLX_RENDER_TYPE = 0x8011;
    public const int GLX_RED_SIZE = 8;
    public const int GLX_GREEN_SIZE = 9;
    public const int GLX_BLUE_SIZE = 10;
    public const int GLX_ALPHA_SIZE = 11;
    public const int GLX_DEPTH_SIZE = 12;
    public const int GLX_DOUBLEBUFFER = 5;
    public const int GLX_PBUFFER_BIT = 0x00000004;
    public const int GLX_RGBA_BIT = 0x00000001;

    // GLX PBuffer attribs
    public const int GLX_PBUFFER_WIDTH = 0x8041;
    public const int GLX_PBUFFER_HEIGHT = 0x8042;
    public const int GLX_PRESERVED_CONTENTS = 0x80DB;

    // GLX context attribs (ARB_create_context)
    public const int GLX_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    public const int GLX_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    public const int GLX_CONTEXT_PROFILE_MASK_ARB = 0x9126;
    public const int GLX_CONTEXT_CORE_PROFILE_BIT_ARB = 0x00000001;

    // GL constants
    public const int GL_READ_FRAMEBUFFER = 0x8CA8;
    public const int GL_DRAW_FRAMEBUFFER = 0x8CA9;
    public const int GL_COLOR_BUFFER_BIT = 0x4000;
    public const int GL_LINEAR = 0x2601;
    public const int GL_TEXTURE_2D = 0x0DE1;
    public const int GL_RGBA = 0x1908;
    public const int GL_RGBA8 = 0x8058;
    public const int GL_UNSIGNED_BYTE = 0x1401;
    public const int GL_TEXTURE_MIN_FILTER = 0x2801;
    public const int GL_TEXTURE_MAG_FILTER = 0x2800;
    public const int GL_FRAMEBUFFER = 0x8D40;
    public const int GL_COLOR_ATTACHMENT0 = 0x8CE0;
    public const int GL_SYNC_GPU_COMMANDS_COMPLETE = 0x9117;
    public const int GL_ALREADY_SIGNALED = 0x911A;
    public const int GL_CONDITION_SATISFIED = 0x911B;
    public const int GL_SYNC_FLUSH_COMMANDS_BIT = 0x00000001;

    // GLX P/Invoke
    [DllImport(GlLib)] public static extern IntPtr glXGetCurrentDisplay();
    [DllImport(GlLib)] public static extern IntPtr glXGetCurrentContext();
    [DllImport(GlLib)] public static extern bool glXMakeCurrent(IntPtr dpy, IntPtr drawable, IntPtr ctx);
    [DllImport(GlLib)] public static extern void glXDestroyContext(IntPtr dpy, IntPtr ctx);
    [DllImport(GlLib)] public static extern IntPtr glXGetProcAddressARB([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(GlLib)] public static extern IntPtr glXChooseFBConfig(IntPtr dpy, int screen, int[] attribs, out int n);
    [DllImport(GlLib)] public static extern IntPtr glXCreatePbuffer(IntPtr dpy, IntPtr config, int[] attribs);
    [DllImport(GlLib)] public static extern void glXDestroyPbuffer(IntPtr dpy, IntPtr pbuffer);

    // X11 P/Invoke (para obtener screen number)
    [DllImport(XLib)] public static extern IntPtr XDefaultScreenOfDisplay(IntPtr dpy);
    [DllImport(XLib)] public static extern int XScreenNumberOfScreen(IntPtr screen);

    // Delegate para glXCreateContextAttribsARB (extensión, se carga dinámicamente)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr glXCreateContextAttribsARBDelegate(IntPtr dpy, IntPtr config, IntPtr share_context, bool direct, int[] attrib_list);

    private static glXCreateContextAttribsARBDelegate _createContextAttribsARB;

    public static glXCreateContextAttribsARBDelegate CreateContextAttribsARB
    {
        get
        {
            if (_createContextAttribsARB == null)
            {
                var ptr = glXGetProcAddressARB("glXCreateContextAttribsARB");
                if (ptr != IntPtr.Zero)
                    _createContextAttribsARB = Marshal.GetDelegateForFunctionPointer<glXCreateContextAttribsARBDelegate>(ptr);
            }
            return _createContextAttribsARB;
        }
    }

    // GL P/Invoke
    [DllImport(GlLib)] public static extern void glGenFramebuffers(int n, int[] framebuffers);
    [DllImport(GlLib)] public static extern void glDeleteFramebuffers(int n, int[] framebuffers);
    [DllImport(GlLib)] public static extern void glBindFramebuffer(int target, int framebuffer);
    [DllImport(GlLib)] public static extern void glGenTextures(int n, int[] textures);
    [DllImport(GlLib)] public static extern void glDeleteTextures(int n, int[] textures);
    [DllImport(GlLib)] public static extern void glBindTexture(int target, int texture);
    [DllImport(GlLib)] public static extern void glTexImage2D(int target, int level, int internalformat, int width, int height, int border, int format, int type, IntPtr data);
    [DllImport(GlLib)] public static extern void glTexParameteri(int target, int pname, int param);
    [DllImport(GlLib)] public static extern void glFramebufferTexture2D(int target, int attachment, int textarget, int texture, int level);
    [DllImport(GlLib)] public static extern void glBlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, int mask, int filter);
    [DllImport(GlLib)] public static extern IntPtr glFenceSync(int condition, int flags);
    [DllImport(GlLib)] public static extern int glClientWaitSync(IntPtr sync, int flags, long timeout);
    [DllImport(GlLib)] public static extern void glDeleteSync(IntPtr sync);
    [DllImport(GlLib)] public static extern void glFlush();
}

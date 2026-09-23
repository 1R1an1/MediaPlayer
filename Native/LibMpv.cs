using System;
using System.Runtime.InteropServices;

namespace MediaPlayer.Native;

/// <summary>
/// P/Invoke crudo a libmpv (libmpv.so.2 en Linux).
/// Solo lo mínimo necesario para reproducir, observar propiedades y mandar comandos.
/// Sin wrappers de terceros, sin dependencias externas además del .so del sistema.
/// </summary>
internal static class LibMpv
{
    internal const string LibName = "libmpv.so.2";

    // ---- Formats ----
    internal const int MPV_FORMAT_NONE = 0;
    internal const int MPV_FORMAT_STRING = 1;
    internal const int MPV_FORMAT_OSD_STRING = 2;
    internal const int MPV_FORMAT_FLAG = 3;
    internal const int MPV_FORMAT_INT64 = 4;
    internal const int MPV_FORMAT_DOUBLE = 5;
    internal const int MPV_FORMAT_NODE = 6;
    internal const int MPV_FORMAT_NODE_ARRAY = 7;
    internal const int MPV_FORMAT_NODE_MAP = 8;
    internal const int MPV_FORMAT_BYTE_ARRAY = 9;

    // ---- Events ----
    internal const int MPV_EVENT_NONE = 0;
    internal const int MPV_EVENT_SHUTDOWN = 1;
    internal const int MPV_EVENT_LOG_MESSAGE = 2;
    internal const int MPV_EVENT_GET_PROPERTY_REPLY = 3;
    internal const int MPV_EVENT_SET_PROPERTY_REPLY = 4;
    internal const int MPV_EVENT_COMMAND_REPLY = 5;
    internal const int MPV_EVENT_START_FILE = 6;
    internal const int MPV_EVENT_END_FILE = 7;
    internal const int MPV_EVENT_FILE_LOADED = 8;
    internal const int MPV_EVENT_IDLE = 11;
    internal const int MPV_EVENT_TICK = 14;
    internal const int MPV_EVENT_CLIENT_MESSAGE = 16;
    internal const int MPV_EVENT_VIDEO_RECONFIG = 17;
    internal const int MPV_EVENT_AUDIO_RECONFIG = 18;
    internal const int MPV_EVENT_SEEK = 19;
    internal const int MPV_EVENT_PLAYBACK_RESTART = 20;
    internal const int MPV_EVENT_PROPERTY_CHANGE = 22;
    internal const int MPV_EVENT_QUEUE_OVERFLOW = 24;

    // ---- End-file reasons ----
    internal const int MPV_END_FILE_REASON_EOF = 0;
    internal const int MPV_END_FILE_REASON_STOP = 1;
    internal const int MPV_END_FILE_REASON_QUIT = 2;
    internal const int MPV_END_FILE_REASON_ERROR = 3;
    internal const int MPV_END_FILE_REASON_REDIRECT = 4;

    // ---- Callbacks ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MpvWakeupCallback(IntPtr ctx);

    // ---- Structs ----
    [StructLayout(LayoutKind.Sequential)]
    public struct mpv_event
    {
        public int event_id;
        public int error;
        public ulong reply_userdata;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct mpv_event_property
    {
        public IntPtr name;
        public int format;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct mpv_event_end_file
    {
        public int reason;
        public int error;
        public int playlist_insert_id;
        public int playlist_insert_num_entries;
    }

    // ---- Core handle ----
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr handle);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr handle);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    // ---- Property setters (overloads resueltos por tipo del 4to arg) ----
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, ref long data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, ref double data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, ref int data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    // ---- Property getters ----
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, ref long data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, ref double data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, ref int data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_get_property_string(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    // ---- Commands ----
    // args: array de punteros a strings UTF8 null-terminated, último elemento IntPtr.Zero.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr handle, IntPtr[] args);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command_string(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string args);

    // ---- Observation & events ----
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(
        IntPtr handle, ulong reply_userdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr handle, double timeout);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(IntPtr handle);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_set_wakeup_callback(
        IntPtr handle, MpvWakeupCallback callback, IntPtr ctx);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_event_name(int event_id);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_client_api_version();

    // ---- Helpers ----
    public static string PtrToStringUtf8AndFree(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        string s = Marshal.PtrToStringUTF8(ptr);
        mpv_free(ptr);
        return s;
    }
}

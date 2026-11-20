/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System.Runtime.InteropServices;

internal static class LibC
{
    private const string LibName = "libc";
    [DllImport(LibName, SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport(LibName, SetLastError = true)] private static extern int dup(int fd);
    [DllImport(LibName, SetLastError = true)] private static extern int dup2(int oldfd, int newfd);
    [DllImport(LibName, SetLastError = true)] private static extern int close(int fd);

    private const int STDERR_FD = 2;
    private static int _saved = -1;

    public static void SilenceStdErr()
    {
        if (_saved >= 0) return;
        _saved = dup(STDERR_FD);                  // guardo el stderr original
        int devnull = open("/dev/null", 1);
        dup2(devnull, STDERR_FD);                 // stderr ahora apunta a /dev/null
        close(devnull);
    }

    public static void RestoreStdErr()
    {
        if (_saved < 0) return;
        dup2(_saved, STDERR_FD);                  // restauro el stderr original
        close(_saved);
        _saved = -1;
    }
}

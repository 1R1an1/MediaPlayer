/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpUtils.Linux;

namespace MediaPlayer.Services;

/// <summary>Carga archivos y carpetas en la playlist.</summary>
public static class MediaLoader
{
    public static readonly string[] SupportedExtensions =
        [".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".wmv", ".mpg", ".mpeg", ".m4v", ".ts", ".mp3", ".flac"];

    public static bool IsMediaFile(string path)
        => Array.IndexOf(SupportedExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>Carga rutas en la playlist, reemplazando el contenido actual.</summary>
    /// <param name="paths">Archivos o carpetas. Las carpetas se expanden recursivamente.</param>
    public static void LoadPaths(params IEnumerable<string> paths)
    {
        if (paths == null) return;
        var files = EnumerateFiles(paths).OrderBy(f => f);
        if (files.Count() < 1) return;
        App.Playlist.AddNew(files);
    }

    /// <summary>Carga archivos en la playlist. sin eliminar la playlist actual</summary>
    /// <param name="paths">Archivos para añadir a la playlist.</param>
    public static void LoadFiles(params IEnumerable<string> files)
    {
        int? count = files?.Count();
        if (count == null || count < 1) return;
        App.Playlist.Add(files.Where(IsMediaFile).OrderBy(f => f));
    }

    private static IEnumerable<string> EnumerateFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;

            string full = Path.GetFullPath(p);
            if (Directory.Exists(full))
            {
                foreach (var f in LinuxKRL.GetReadableFiles(full, IsMediaFile))
                    yield return f;
            }
            else if (File.Exists(full) && IsMediaFile(full))
                yield return full;
        }
    }
}

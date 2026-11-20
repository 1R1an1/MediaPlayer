/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MediaPlayer.Models;
using SharpUtils.Linux;

namespace MediaPlayer.Services;

/// <summary>Utilidades que usa la app para los archivos</summary>
public static class MediaUtils
{
    public static readonly string[] SupportedExtensions =
        [".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".wmv",
         ".m4a", ".m4b", ".mka", ".wma", ".mp3", ".flac", ".ogg", ".opus"];

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

    /// <summary>Formatea segundos como "m:ss" o "h:mm:ss".</summary>
    public static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;

        int hours = (int)(seconds / 3600);
        int minutes = (int)(seconds / 60) % 60;
        int secs = (int)seconds % 60;

        return hours > 0
            ? $"{hours}:{minutes:D2}:{secs:D2}"
            : $"{minutes}:{secs:D2}";
    }

    /// <summary>
    /// Obtiene metadata de un archivo de media. Según <paramref name="mode"/> devuelve
    /// solo el cover (con hash MD5) o todo: cover, duración y artista.
    /// </summary>
    public static async Task<MediaProbe> Probe(string path, ProbeMode mode)
    {
        var result = new MediaProbe();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;

        try
        {
            if (mode == ProbeMode.All || mode == ProbeMode.AllWithoutHash)
            {
                var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v quiet -show_entries format=duration:format_tags=artist:stream_tags=artist -of default=noprint_wrappers=1:nokey=1 \"{path}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                string output = await probe.StandardOutput.ReadToEndAsync();
                await probe.WaitForExitAsync();

                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 1)
                {
                    if (double.TryParse(lines[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
                        result.Duration = TimeSpan.FromSeconds(dur);
                    else
                        result.Artist = lines[0].Trim();
                }
                else if (lines.Length == 2)
                {
                    if (!double.TryParse(lines[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        d = double.Parse(lines[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
                        result.Artist = lines[0].Trim();
                    }
                    else
                        result.Artist = lines[1].Trim();
                    result.Duration = TimeSpan.FromSeconds(d);
                }
            }

            // Cover con ffmpeg
            string tempDir = Directory.CreateTempSubdirectory("mpv_probe_").FullName;

            var extractProbe = Process.Start(new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -show_entries stream=index,codec_type,codec_name:stream_tags=mimetype -of default=noprint_wrappers=1 \"{path}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            var extractOutput = await extractProbe.StandardOutput.ReadToEndAsync();
            await extractProbe.WaitForExitAsync();

            int index = -1;
            string extencion = null;
            bool isVideo = false;

            foreach (var stream in extractOutput.Split("index=", StringSplitOptions.RemoveEmptyEntries))
            {
                string[] lines = stream.Replace("\r", "").Split('\n');
                string mime = lines.FirstOrDefault(x => x.StartsWith("TAG:mimetype=image/"))?["TAG:mimetype=image/".Length..];
                string codec = lines.FirstOrDefault(x => x.StartsWith("codec_name="))?["codec_name=".Length..];
                string type = lines.FirstOrDefault(x => x.StartsWith("codec_type="))?["codec_type=".Length..];

                if (mime != null || (type == "video" && codec is "png" or "mjpeg" or "webp" or "bmp" or "tiff"))
                {
                    index = int.Parse(lines[0]);
                    extencion = $".{(mime ?? codec).Replace("mjpeg", "jpg")}";
                    isVideo = type == "video";
                    break;
                }
            }

            string coverFile = Path.Combine(tempDir, $"cover{extencion ?? ".jpg"}");
            extractProbe = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = index != -1
                    ? isVideo
                        ? $"-i \"{path}\" -map 0:{index} -frames:v 1 \"{coverFile}\" -y -loglevel quiet"
                        : $"-dump_attachment:{index} \"{coverFile}\" -i \"{path}\" -y -loglevel quiet"
                    : $"-i \"{path}\" -map 0:v:0 -frames:v 1 -y -loglevel quiet \"{coverFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            await extractProbe.WaitForExitAsync();

            if (File.Exists(coverFile))
            {
                byte[] bytes = await File.ReadAllBytesAsync(coverFile);
                result.CoverBytes = bytes;

                if (mode == ProbeMode.OnlyCover || mode == ProbeMode.All)
                {
                    using var md5 = MD5.Create();
                    result.CoverHashHex = BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                }
            }

            try { Directory.Delete(tempDir, true); } catch { }
        }
        catch { }
        return result;
    }
}

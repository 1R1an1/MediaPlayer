/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaPlayer.Models;
using MediaPlayer.Services;
using SharpUtils.Linux;
using TermFlow.Components.FullScreen;
using TermFlow.Components.FullScreen.TreeExplorer;
using TermFlow.Core;
using TermFlow.Dev;
using TermFlow.Dev.CanvasExt;

namespace MediaPlayer.TUI;

/// <summary>TUI completa: borde externo, mini-reproductor abajo, cuadro de interfaz arriba.</summary>
public class PlayerTUI
{
    private enum Mode { Abrir, PlaylistMenu, PlaylistSearch }

    private static MpvPlayer _mpv => App.Mpv;
    private static PlaylistService _playlist => App.Playlist;

    // Mini-player state
    private static double _vol = 1, _pos, _dur;
    private static string _title = "", _artist = "";
    private static bool _isPlaying = true, _shuffle, _mute;
    private static LoopMode _loop;

    // Mode state
    private static Mode _mode = Mode.Abrir;
    private static SearchListState? _searchState;
    private static TreeExplorerState? _treeState;
    private static int _menuCursor;
    private static string _statusMsg = "";
    private static CancellationTokenSource _statusCts;

    // Estado preservado al cambiar de modo
    private static string _preservedTreePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static int _preservedPlaylistCursor;

    // Root router: contiene TODOS los bindings del mini-player. Permanente.
    // Los mode-routers (TreeExplorer/Menu/SearchList) se attachan como child cuando hace falta.
    // Children tienen prioridad, así el modo consume sus teclas específicas primero.
    private static InputRouter _rootRouter;
    private static InputRouter _modeRouter;

    // Render/switch sync
    private static readonly SemaphoreSlim _renderSignal = new(0, 1);
    private static int _renderPending;
    private static int _pendingSwitch;
    private static TermCanvas _canvas;
    private static bool _running = true;

    private static ScrollState _abrirScroll;
    private static ScrollState _playlistScroll;

    private static readonly string[] MediaExts =
        [".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".wmv", ".mpg", ".mpeg", ".m4v", ".ts", ".mp3", ".flac"];

    public static void Start(string[] args)
    {
        App.Init(args);
        Engine.Setup();
        Engine.EnterFullScreen(true);

        // Atenuar morados default.
        ThemeColors.Selector = AnsiColor.Dim + AnsiColor.Magenta;
        ThemeColors.Primary = AnsiColor.Dim + AnsiColor.Magenta;

        _canvas = new TermCanvas(true, false, 100, onResize: (_, _) => RequestRender());

        SubscribeEvents();
        BuildRootRouter();
        _modeRouter = BuildAbrirRouter();
        _rootRouter.AddChild(_modeRouter);
        RequestRender();

        // Input loop
        ThreadPool.QueueUserWorkItem(async _ =>
        {
            while (_running)
            {
                var ev = InputReader.ReadInput();
                if (ev.Type != InputEventType.None)
                {
                    _rootRouter.Handle(ev);
                    int sw = _pendingSwitch;
                    _pendingSwitch = 0;
                    if (sw != 0) DoSwitch(sw);
                    RequestRender();
                }
                await Task.Delay(15);
            }
        });

        // Render loop
        while (_running)
        {
            _renderSignal.Wait();
            Interlocked.Exchange(ref _renderPending, 0);
            try { Render(); }
            catch { }
        }

        Engine.ExitFullScreen(true);
    }

    private static void SubscribeEvents()
    {
        _mpv.OnVolumeChanged += v => { _vol = v; RequestRender(); };
        _mpv.IsPlayingChanged += p => { _isPlaying = p; RequestRender(); };
        _mpv.PositionChanged += p => { _pos = p; RequestRender(); };
        _mpv.DurationChanged += d => { _dur = d; RequestRender(); };
        _mpv.MetadataChanged += () => { _title = _mpv.Title ?? ""; _artist = _mpv.Artist ?? ""; RequestRender(); };
        _mpv.MuteChanged += m => { _mute = m; RequestRender(); };
        _playlist.LoopModeChanged += l => { _loop = l; RequestRender(); };
        _playlist.ShuffleChanged += s => { _shuffle = s; RequestRender(); };
        _playlist.CurrentChanged += _ => RequestRender();
    }

    private static void RequestRender()
    {
        if (Interlocked.Exchange(ref _renderPending, 1) == 0)
            _renderSignal.Release();
    }

    private static void RequestSwitch(int code) => _pendingSwitch = code;

    private static void SetStatus(string msg)
    {
        _statusCts?.Cancel();
        _statusMsg = msg;
        if (string.IsNullOrEmpty(msg)) return;
        _statusCts = new CancellationTokenSource();
        var token = _statusCts.Token;
        Task.Delay(TimeSpan.FromSeconds(15), token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
            {
                _statusMsg = "";
                RequestRender();
            }
        }, TaskScheduler.Default);
    }

    private static void Quit()
    {
        _running = false;
        Engine.ExitFullScreen(true);
        Environment.Exit(0);
    }

    // --- Root router (mini-player, TODOS los bindings, permanente) ---

    private static void BuildRootRouter()
    {
        _rootRouter = new InputRouter(false)
            .Bind("Tab", "cambiar modo", () => RequestSwitch(1), ConsoleKey.Tab)
            .Bind("Q", "salir", Quit, ConsoleKey.Q)
            .Bind("Space/K", "play/pause", PlayPause, ConsoleKey.Spacebar, ConsoleKey.K)
            .Bind("←", "seek -5s", () => _mpv.SeekRelative(-5), ConsoleKey.LeftArrow)
            .Bind("→", "seek +5s", () => _mpv.SeekRelative(5), ConsoleKey.RightArrow)
            .Bind("+/0", "vol +5%", () => _mpv.SetVolume01(_vol + 0.05), ConsoleKey.Add, ConsoleKey.D0)
            .Bind("-/9", "vol -5%", () => _mpv.SetVolume01(_vol - 0.05), ConsoleKey.Subtract, ConsoleKey.D9)
            .Bind("M", "mute", () => _mpv.SetMute(!_mpv.IsMuted), ConsoleKey.M)
            .Bind("S", "shuffle", () => _playlist.Shuffle = !_playlist.Shuffle, ConsoleKey.S)
            .Bind("L", "loop", _playlist.CycleLoopMode, ConsoleKey.L)
            .Bind("B", "anterior", Prev, ConsoleKey.B)
            .Bind("N", "siguiente", _playlist.Advance, ConsoleKey.N)
            .BeforeKey(cki =>
            {
                if (_mode == Mode.PlaylistMenu
                    && (cki.Modifiers & ConsoleModifiers.Control) != 0
                    && cki.Key == ConsoleKey.F)
                    RequestSwitch(2);
            });
    }

    // --- Mode router builders (solo el router del modo, se attachan como child del root) ---

    private static InputRouter BuildAbrirRouter()
    {
        _mode = Mode.Abrir;
        _treeState = null;

        var options = new ExplorerOptions { Filter = ExplorerFilter.All, IgnoreSymlinks = true };
        var dataSource = new TreeExplorer.PhysicalDataSource(_preservedTreePath, options);

        var router = TreeExplorer.AddBindings(
            dataSource,
            options,
            null,
            state =>
            {
                _treeState = state;
                _preservedTreePath = state.CurrentNode;
                RequestRender();
            },
            onSuccess: OnTreeConfirm,
            onCancel: () => RequestSwitch(1));

        return router;
    }

    private static InputRouter BuildPlaylistMenuRouter()
    {
        _mode = Mode.PlaylistMenu;

        var items = GetPlaylistTitles();
        int start = items.Count > 0 ? Math.Clamp(_preservedPlaylistCursor, 0, items.Count - 1) : 0;
        _menuCursor = start;

        var router = Menu.AddBindings(
            items,
            start,
            cursor => { _menuCursor = cursor; _preservedPlaylistCursor = cursor; RequestRender(); },
            onSuccess: OnPlaylistConfirm,
            onCancel: () => RequestSwitch(1));

        return router;
    }

    private static InputRouter BuildPlaylistSearchRouter()
    {
        _mode = Mode.PlaylistSearch;
        _searchState = null;

        var items = GetPlaylistTitles();
        int start = items.Count > 0 ? Math.Clamp(_preservedPlaylistCursor, 0, items.Count - 1) : 0;

        var router = SearchList.AddBindings(
            items,
            "  Buscar: » ",
            start,
            state =>
            {
                _searchState = state;
                // NO actualizar _preservedPlaylistCursor aquí: SearchList arranca con
                // Cursor=0 (ApplyFilter("") lo dispara), y pisaría el cursor preservado.
                RequestRender();
            },
            onSuccess: OnPlaylistConfirm,
            onCancel: () => RequestSwitch(3)); // Esc → vuelve a PlaylistMenu

        // Q: salir de la app (sin esto, el Unhandled del LineEdit escribe 'q').
        router.Bind("Q", "salir", Quit, ConsoleKey.Q);

        // Tab: también sale del search (vuelve a PlaylistMenu).
        router.Bind("Tab", "salir búsqueda", () => RequestSwitch(3), ConsoleKey.Tab);

        return router;
    }

    private static void DoSwitch(int code)
    {
        // Quita el modeRouter viejo del root y attacha el nuevo.
        if (_modeRouter != null) _rootRouter.RemoveChild(_modeRouter);

        switch (code)
        {
            case 1: // ciclo Tab
                _modeRouter = _mode == Mode.Abrir ? BuildPlaylistMenuRouter() : BuildAbrirRouter();
                break;
            case 2: // entrar search
                _modeRouter = BuildPlaylistSearchRouter();
                break;
            case 3: // salir search → PlaylistMenu
                _modeRouter = BuildPlaylistMenuRouter();
                break;
            case 4: // después de confirmar en Abrir → PlaylistMenu
                _modeRouter = BuildPlaylistMenuRouter();
                break;
            default:
                return;
        }

        _rootRouter.AddChild(_modeRouter);
        RequestRender();
    }

    // --- Mini-player actions ---

    private static void PlayPause()
    {
        if (_mpv.CurrentPath == null) return;
        if (_mpv.EofReached) { _mpv.SeekAbsolute(0); _mpv.Play(); }
        else _mpv.PlayPause();
    }

    private static void Prev()
    {
        if (_mpv.PositionSec > 5) { _mpv.SeekAbsolute(0); return; }
        _playlist.GoPrev();
    }

    // --- Callbacks ---

    private static void OnPlaylistConfirm(int originalIndex)
    {
        if (originalIndex >= 0 && originalIndex < _playlist.Items.Count)
            _playlist.SetCurrent(originalIndex);
    }

    private static void OnTreeConfirm(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (Directory.Exists(path))
            {
                var files = LinuxKRL.GetReadableFiles(path, IsMediaFile).OrderBy(f => f).ToList();
                if (files.Count > 0)
                {
                    _playlist.AddNew(files);
                    SetStatus($"Cargados {files.Count} archivos");
                }
                else
                    SetStatus("Carpeta sin archivos soportados");
            }
            else if (File.Exists(path) && IsMediaFile(path))
            {
                _playlist.Add(path);
                SetStatus($"Agregado: {Path.GetFileName(path)}");
            }
            else
                SetStatus("Archivo no soportado");
        }
        catch (Exception ex) { SetStatus($"Error: {ex.Message}"); }

        // Después de confirmar en Abrir, ir a PlaylistMenu. Diferido para no tocar el árbol durante Handle().
        RequestSwitch(4);
    }

    public static bool IsMediaFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(MediaExts, ext) >= 0;
    }

    // --- Render ---

    private static void Render()
    {
        int W = Console.WindowWidth, H = Console.WindowHeight;
        if (W < 40 || H < 12) return;

        if (_mode != Mode.Abrir) RefreshTitleIndex();

        _canvas.Resize(W, H);
        _canvas.Clear();

        _canvas.DrawBorder(0, 0, W - 1, H - 1, ThemeColors.Primary);
        _canvas.WriteAt(2, 0, " MediaPlayer ", ThemeColors.Primary);

        int ifaceH = H - 8;
        if (_mode == Mode.Abrir)
            RenderAbrir(1, 1, W - 2, ifaceH);
        else
            RenderPlaylist(1, 1, W - 2, ifaceH);

        if (_mode == Mode.Abrir) _canvas.CursorVisible = false;
        else if (_mode == Mode.PlaylistMenu) _canvas.CursorVisible = false;

        RenderMiniPlayer(1, H - 7, W - 2, 6);

        _canvas.Flush();
    }

    private static void RenderAbrir(int x1, int y1, int width, int height)
    {
        _canvas.DrawBorder(x1, y1, x1 + width - 1, y1 + height - 1, ThemeColors.Dim);
        _canvas.WriteAt(x1 + 2, y1, " Abrir archivo/carpeta ", ThemeColors.Dim);

        var c = _canvas.CreateSubCanvas(x1 + 1, y1 + 1, x1 + width - 2, y1 + height - 2);
        int w = c.Width, h = c.Height;

        if (_treeState == null) return;
        var state = _treeState.Value;

        string path = state.CurrentNode;
        string pathDisplay = path.Length > w - 2 ? "…" + path[^(w - 3)..] : path;
        c.WriteAtAndClear(0, 0, $"{ThemeColors.Primary}{pathDisplay}{ThemeColors.Reset}");
        c.WriteAt(0, 1, ThemeColors.Dim + new string('─', w) + ThemeColors.Reset);

        if (!string.IsNullOrEmpty(_statusMsg))
            c.WriteAtAndClear(0, h - 2, $"{ThemeColors.Warning}{TruncateVisible(_statusMsg, w)}{ThemeColors.Reset}");

        // nonListRows: path(0) + separator(1) + status(h-2) + scrollInd(h-3) = 4
        int reserved = Console.WindowHeight - (h - 4);
        _abrirScroll.Update(state.Cursor, state.Entries.Count, reserved);
        int cursor = _abrirScroll.Cursor;
        int scroll = _abrirScroll.Scroll;
        int visible = Math.Max(1, _abrirScroll.VisibleRows);

        int listY = 2;
        if (state.Entries.Count == 0)
        {
            c.WriteAtAndClear(0, listY, $"  {(state.IsBlocked ? "(carpeta bloqueada)" : "(carpeta vacía)")}", ThemeColors.Dim);
        }
        else
        {
            int end = Math.Min(state.Entries.Count, scroll + visible);
            for (int i = scroll; i < end; i++)
            {
                int y = listY + (i - scroll);
                var entry = state.Entries[i];
                string name = entry.IsDirectory ? entry.Name + "/" : entry.Name;
                string cursorMark = (i == cursor) ? $"{ThemeColors.Selector}{ConsoleGlyphs.Indicator}{ThemeColors.Reset} " : "  ";
                string nameCol = entry.IsDirectory
                    ? (i == cursor ? $"{ThemeColors.Selector}{AnsiColor.Bold}{name}{ThemeColors.Reset}" : $"{ThemeColors.Info}{name}{ThemeColors.Reset}")
                    : (i == cursor ? $"{ThemeColors.Selector}{AnsiColor.Bold}{name}{ThemeColors.Reset}" : name);
                c.WriteAtAndClear(0, y, $"{cursorMark}{nameCol}");
            }
        }

        if (scroll > 0)
            c.WriteAtAndClear(w - 14, listY - 1, $"↑ {scroll} ", ThemeColors.Dim);
        int remaining = state.Entries.Count - (scroll + visible);
        if (remaining > 0)
            c.WriteAtAndClear(w - 14, h - 3, $"↓ {remaining} ", ThemeColors.Dim);
    }

    private static void RenderPlaylist(int x1, int y1, int width, int height)
    {
        int half = width / 2;

        string title = _mode == Mode.PlaylistSearch ? " Playlist (buscar) " : " Playlist ";
        _canvas.DrawBorder(x1, y1, x1 + half - 1, y1 + height - 1, ThemeColors.Dim);
        _canvas.WriteAt(x1 + 2, y1, title, ThemeColors.Dim);

        _canvas.DrawBorder(x1 + half, y1, x1 + width - 1, y1 + height - 1, ThemeColors.Dim);
        _canvas.WriteAt(x1 + half + 2, y1, " Specs ", ThemeColors.Dim);

        var plCanvas = _canvas.CreateSubCanvas(x1 + 1, y1 + 1, x1 + half - 2, y1 + height - 2);
        var spCanvas = _canvas.CreateSubCanvas(x1 + half + 1, y1 + 1, x1 + width - 2, y1 + height - 2);

        if (_mode == Mode.PlaylistSearch)
            RenderPlaylistSearchContent(plCanvas);
        else
            RenderPlaylistMenuContent(plCanvas);

        RenderSpecsContent(spCanvas);
    }

    private static void RenderPlaylistMenuContent(VirtualCanvas c)
    {
        int w = c.Width, h = c.Height;

        int count = _playlist.Items.Count;
        if (count == 0)
        {
            c.WriteAtAndClear(0, 0, "  (vacío — presioná Tab para abrir archivos)", ThemeColors.Dim);
            return;
        }

        // Header
        c.WriteAtAndClear(0, 0, $"{ThemeColors.Primary}Playlist ({count} items){ThemeColors.Reset}  {ThemeColors.Dim}Ctrl+F buscar{ThemeColors.Reset}");
        c.WriteAt(0, 1, ThemeColors.Dim + new string('─', w) + ThemeColors.Reset);

        // nonListRows: header(0) + separator(1) = 2
        int reserved = Console.WindowHeight - (h - 2);
        _playlistScroll.Update(_menuCursor, count, reserved);
        int cursor = _playlistScroll.Cursor;
        int scroll = _playlistScroll.Scroll;
        int visible = Math.Max(1, _playlistScroll.VisibleRows);

        int listY = 2;
        int end = Math.Min(count, scroll + visible);
        for (int i = scroll; i < end; i++)
        {
            int y = listY + (i - scroll);
            var item = _playlist.Items[i];
            string titleText = item.Title ?? Path.GetFileNameWithoutExtension(item.Path);

            string dur = item.DurationText;
            bool isCurrent = item.IsCurrent;
            string marker = isCurrent ? $"{ThemeColors.Success}▶{ThemeColors.Reset} " : "  ";
            string cursorMark = (i == cursor) ? $"{ThemeColors.Selector}{ConsoleGlyphs.Indicator}{ThemeColors.Reset} " : "  ";

            int durLen = dur.Length + 1;
            int titleMax = w - durLen - 4;
            string titleTrim = titleText.Length > titleMax ? titleText[..(Math.Max(0, titleMax - 1))] + "…" : titleText;
            string titleCol = (i == cursor) ? $"{ThemeColors.Selector}{AnsiColor.Bold}{titleTrim}{ThemeColors.Reset}" : titleTrim;
            string durCol = $"{ThemeColors.Dim}{dur}{ThemeColors.Reset}";

            string line = $"{marker}{cursorMark}{titleCol}";
            int pad = w - line.GetVisualLength() - durLen;
            if (pad > 0) line += new string(' ', pad);
            line += durCol;
            c.WriteAt(0, y, line);
        }

        if (scroll > 0)
            c.WriteAtAndClear(w - 14, listY - 1, $"↑ {scroll} ", ThemeColors.Dim);
        int remaining = count - (scroll + visible);
        if (remaining > 0)
            c.WriteAtAndClear(w - 14, h - 1, $"↓ {remaining} ", ThemeColors.Dim);
    }

    private static void RenderPlaylistSearchContent(VirtualCanvas c)
    {
        int w = c.Width, h = c.Height;

        if (_searchState == null) return;
        var state = _searchState.Value;

        c.WriteAtAndClear(0, 0, $"{state.LastPromptLine}{AnsiColor.Bold}{state.Query}{ThemeColors.Reset}");
        c.WriteAt(0, 1, ThemeColors.Dim + new string('─', w) + ThemeColors.Reset);

        // nonListRows: prompt(0) + separator(1) + scrollInd(h-1) = 3
        int reserved = Console.WindowHeight - (h - 3);
        _playlistScroll.Update(state.Cursor, state.Filtered.Count, reserved);
        int cursor = _playlistScroll.Cursor;
        int scroll = _playlistScroll.Scroll;
        int visible = Math.Max(1, _playlistScroll.VisibleRows);

        var titleCache = _titleIndex;
        int listY = 2;
        if (state.Filtered.Count == 0)
        {
            c.WriteAtAndClear(0, listY, "  (sin resultados — Esc para volver)", ThemeColors.Dim);
        }
        else
        {
            int end = Math.Min(state.Filtered.Count, scroll + visible);
            for (int i = scroll; i < end; i++)
            {
                int y = listY + (i - scroll);
                var item = state.Filtered[i];
                titleCache.TryGetValue(item.Text, out int originalIdx);

                string title = item.Text;
                string dur = originalIdx >= 0 ? _playlist.Items[originalIdx].DurationText : "--:--";
                bool isCurrent = originalIdx >= 0 && _playlist.Items[originalIdx].IsCurrent;
                string marker = isCurrent ? $"{ThemeColors.Success}▶{ThemeColors.Reset} " : "  ";
                string cursorMark = (i == cursor) ? $"{ThemeColors.Selector}{ConsoleGlyphs.Indicator}{ThemeColors.Reset} " : "  ";

                int durLen = dur.Length + 1;
                int titleMax = w - durLen - 4;
                string titleTrim = title.Length > titleMax ? title[..(Math.Max(0, titleMax - 1))] + "…" : title;
                string titleCol = (i == cursor) ? $"{ThemeColors.Selector}{AnsiColor.Bold}{titleTrim}{ThemeColors.Reset}" : titleTrim;
                string durCol = $"{ThemeColors.Dim}{dur}{ThemeColors.Reset}";

                string line = $"{marker}{cursorMark}{titleCol}";
                int pad = w - line.GetVisualLength() - durLen;
                if (pad > 0) line += new string(' ', pad);
                line += durCol;
                c.WriteAt(0, y, line);
            }

            if (scroll > 0)
                c.WriteAtAndClear(w - 14, listY - 1, $"↑ {scroll} ", ThemeColors.Dim);
            int remaining = state.Filtered.Count - (scroll + visible);
            if (remaining > 0)
                c.WriteAtAndClear(w - 14, h - 1, $"↓ {remaining} ", ThemeColors.Dim);
        }

        // Cursor real del prompt.
        _canvas.CursorVisible = true;
        int cursorX = c.X + state.PromptLength + state.QueryCursorPos;
        if (cursorX < c.X + w) _canvas.CursorPos = (cursorX, c.Y);
    }

    // Cache de FileInfo.Length por path (el syscall es caro para hacerlo cada render).
    private static string _cachedSizePath;
    private static string _cachedSizeValue = "—";

    private static string GetItemSize(string path)
    {
        if (path == _cachedSizePath) return _cachedSizeValue;
        _cachedSizePath = path;
        try
        {
            if (File.Exists(path)) { _cachedSizeValue = FormatBytes(new FileInfo(path).Length); return _cachedSizeValue; }
        }
        catch { }
        _cachedSizeValue = "—";
        return _cachedSizeValue;
    }

    private static void RenderSpecsContent(VirtualCanvas c)
    {
        int w = c.Width, h = c.Height;

        if (!string.IsNullOrEmpty(_statusMsg))
            c.WriteAtAndClear(0, h - 1, $"{ThemeColors.Warning}{TruncateVisible(_statusMsg, w)}{ThemeColors.Reset}");

        if (_playlist.Items.Count == 0)
        {
            c.WriteAtAndClear(0, 0, "(ningún item)", ThemeColors.Dim);
            return;
        }

        // En modo Search, el item bajo cursor es el del search. En Menu, es _menuCursor.
        int originalIdx;
        if (_mode == Mode.PlaylistSearch && _searchState != null)
        {
            var state = _searchState.Value;
            if (state.Cursor < 0 || state.Cursor >= state.Filtered.Count) return;
            if (!_titleIndex.TryGetValue(state.Filtered[state.Cursor].Text, out originalIdx) || originalIdx < 0)
            {
                c.WriteAtAndClear(0, 0, "(item no encontrado)", ThemeColors.Dim);
                return;
            }
        }
        else
        {
            originalIdx = _menuCursor;
            if (originalIdx < 0 || originalIdx >= _playlist.Items.Count) return;
        }

        var item = _playlist.Items[originalIdx];
        int y = 0;

        void WriteRow(string label, string value)
        {
            if (y >= h - 1) return;
            string lbl = $"{ThemeColors.Dim}{label}{ThemeColors.Reset} ";
            c.WriteAt(0, y, lbl);
            int lblLen = lbl.GetVisualLength();
            string val = value ?? "—";
            int maxVal = w - lblLen - 1;
            if (val.Length > maxVal) val = val[..(Math.Max(0, maxVal - 1))] + "…";
            c.WriteAtAndClear(lblLen, y, val);
            y++;
        }

        WriteRow("Title:", item.Title ?? Path.GetFileNameWithoutExtension(item.Path));
        WriteRow("Artist:", string.IsNullOrWhiteSpace(item.Artist) ? "—" : item.Artist);
        WriteRow("Duration:", item.DurationText);
        WriteRow("Size:", GetItemSize(item.Path));
        WriteRow("Path:", item.Path);

        y++;
        if (y < h - 1)
        {
            string st = item.IsCurrent ? $"{ThemeColors.Success}● Reproduciendo{ThemeColors.Reset}" : $"{ThemeColors.Dim}○ En cola{ThemeColors.Reset}";
            c.WriteAtAndClear(0, y, st);
        }
    }

    private static void RenderMiniPlayer(int x1, int y1, int width, int height)
    {
        _canvas.DrawBorder(x1, y1, x1 + width - 1, y1 + height - 1, ThemeColors.Primary);
        _canvas.WriteAt(x1 + 2, y1, " Mini Player ", ThemeColors.Primary);

        var c = _canvas.CreateSubCanvas(x1 + 1, y1 + 1, x1 + width - 2, y1 + height - 2);
        int w = c.Width;

        string title = string.IsNullOrEmpty(_title) ? (_mpv.CurrentPath != null ? Path.GetFileName(_mpv.CurrentPath) : "— sin archivo —") : _title;
        c.WriteAtAndClear(0, 0, $"{AnsiColor.Bold}{ThemeColors.Bright}{TruncateVisible(title, w)}{ThemeColors.Reset}");

        string artist = string.IsNullOrEmpty(_artist) ? "—" : _artist;
        c.WriteAtAndClear(0, 1, $"{ThemeColors.Dim}{TruncateVisible(artist, w)}{ThemeColors.Reset}");

        string posS = PlaylistItem.FormatTime(_pos), durS = PlaylistItem.FormatTime(_dur);
        int meta = posS.Length + durS.Length + 2;
        int barW = Math.Max(4, w - meta);
        double ratio = _dur > 0 ? Math.Clamp(_pos / _dur, 0, 1) : 0;
        int filled = (int)(ratio * barW);
        string posBar = $"{ThemeColors.Primary}{new string('━', filled)}{ThemeColors.Reset}{ThemeColors.Dim}{new string('━', barW - filled)}{ThemeColors.Reset}";
        c.WriteAt(0, 2, $"{posS} {posBar} {durS}");

        string volS = _mute ? $"{ThemeColors.Dim}Mute{ThemeColors.Reset}" : $"{(int)(_vol * 100)}%";
        int volLabelLen = "vol: ".Length + volS.GetVisualLength() + 1;
        int volBarW = Math.Max(4, w / 2 - volLabelLen - 1);
        int volFilled = (int)(_vol * volBarW);
        string volFilledBar = new string('━', volFilled);
        string volEmptyBar = $"{ThemeColors.Dim}{new string('━', volBarW - volFilled)}{ThemeColors.Reset}";
        string volBar = $"vol: {volS} {volFilledBar}{volEmptyBar}";

        string playIcon = _isPlaying ? "⏸" : "▶";
        string loopStr = _loop switch
        {
            LoopMode.None => $"{ThemeColors.Dim}None{ThemeColors.Reset}",
            LoopMode.Track => $"{ThemeColors.Bright}Track{ThemeColors.Reset}",
            LoopMode.Playlist => $"{ThemeColors.Bright}Playlist{ThemeColors.Reset}",
            _ => $"{ThemeColors.Dim}None{ThemeColors.Reset}"
        };
        string shuffleStr = _shuffle ? $"{ThemeColors.Bright}On{ThemeColors.Reset}" : $"{ThemeColors.Dim}Off{ThemeColors.Reset}";

        c.WriteAtAndClear(0, 3, volBar);
        string right = $"{playIcon} Loop: {loopStr} Shuffle: {shuffleStr}";
        int rightLen = right.GetVisualLength();
        if (rightLen < w)
            c.WriteAtAndClear(w - rightLen, 3, right);
    }

    // --- Utils ---

    /// <summary>Cache de titleToIndex, construida una vez por render.</summary>
    private static Dictionary<string, int> _titleIndex = new(StringComparer.Ordinal);
    private static int _titleIndexCount = -1;

    /// <summary>Refresca _titleIndex solo si cambió el count de la playlist.</summary>
    private static void RefreshTitleIndex()
    {
        if (_titleIndexCount == _playlist.Items.Count) return;
        _titleIndex.Clear();
        for (int j = 0; j < _playlist.Items.Count; j++)
        {
            string title = _playlist.Items[j].Title ?? Path.GetFileNameWithoutExtension(_playlist.Items[j].Path);
            _titleIndex.TryAdd(title, j);
        }
        _titleIndexCount = _playlist.Items.Count;
    }

    private static List<string> GetPlaylistTitles()
    {
        var list = new List<string>(_playlist.Items.Count);
        for (int j = 0; j < _playlist.Items.Count; j++)
            list.Add(_playlist.Items[j].Title ?? Path.GetFileNameWithoutExtension(_playlist.Items[j].Path));
        return list;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return u == 0 ? $"{size} {units[u]}" : $"{size:0.##} {units[u]}";
    }

    private static string TruncateVisible(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        if (max <= 1) return "…";
        return s[..(max - 1)] + "…";
    }
}

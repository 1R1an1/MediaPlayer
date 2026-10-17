/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaPlayer.Services;
using SharpUtils.Linux;
using SharpUtils.Utilities;
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

    /// <summary>Acción pendiente de switch de modo. Null = ninguna.</summary>
    private enum Switch { Tab, EnterSearch, ExitSearch, AfterTreeConfirm }

    private static MpvPlayer _mpv => App.Mpv;
    private static Player _player => App.Player;
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
    private static InputRouter _explorerRouter;

    // Render/switch sync
    private static readonly SemaphoreSlim _renderSignal = new(0, 1);
    private static int _renderPending;
    private static Switch? _pendingSwitch;
    private static TermCanvas _canvas;
    private static bool _running = true;

    private static ScrollState _abrirScroll;
    private static ScrollState _playlistScroll;

    private static VirtualCanvas _abrirCanvas;
    private static VirtualCanvas _plCanvas;
    private static VirtualCanvas _spCanvas;
    private static VirtualCanvas _miniCanvas;

    /// <summary>Obtiene un sub-canvas cacheado, creándolo la 1ra vez y resizeando las siguientes.</summary>
    private static VirtualCanvas GetSubCanvas(VirtualCanvas field, int x1, int y1, int x2, int y2)
    {
        if (field == null)
            return _canvas.CreateSubCanvas(x1, y1, x2, y2);
        field.Resize(x1, y1, x2, y2);
        return field;
    }

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
        BuildAbrirRouter(true);
        _modeRouter = _playlist.Items.Count == 0 ? BuildAbrirRouter(false) : BuildPlaylistMenuRouter();
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
                    if (_rootRouter.Handle(ev))
                        RequestRender();
                    DoSwitch();
                }
                await Task.Delay(15);
            }
        });

        // Render loop
        while (_running)
        {
            _renderSignal.Wait();
            Interlocked.Exchange(ref _renderPending, 0);
            Render();
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

    private static void SetStatus(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return;
        _statusCts?.Cancel();
        _statusMsg = msg;
        _statusCts = new();
        Task.Run(async () =>
        {
            try { await Task.Delay(15_000, _statusCts.Token); } catch { return; }
            _statusMsg = "";
            RequestRender();
        }, _statusCts.Token);
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
            .Bind("Tab", "cambiar modo", () => _pendingSwitch = Switch.Tab, ConsoleKey.Tab)
            .Bind("Q", "salir", Quit, ConsoleKey.Q)
            .Bind("Space/K", "play/pause", _player.PlayPause, ConsoleKey.Spacebar, ConsoleKey.K)
            .Bind("←", "seek -5s", () => _player.SeekRelative(-5), ConsoleKey.LeftArrow)
            .Bind("→", "seek +5s", () => _player.SeekRelative(5), ConsoleKey.RightArrow)
            .Bind("+/0", "vol +5%", () => _player.VolumeDelta(0.05), ConsoleKey.Add, ConsoleKey.D0)
            .Bind("-/9", "vol -5%", () => _player.VolumeDelta(-0.05), ConsoleKey.Subtract, ConsoleKey.D9)
            .Bind("M", "mute", _player.ToggleMute, ConsoleKey.M)
            .Bind("S", "shuffle", _player.ToggleShuffle, ConsoleKey.S)
            .Bind("L", "loop", _player.CycleLoopMode, ConsoleKey.L)
            .Bind("B", "anterior", _player.Preview, ConsoleKey.B)
            .Bind("N", "siguiente", _player.Next, ConsoleKey.N)
            .BeforeKey(cki =>
            {
                if (_mode == Mode.PlaylistMenu && (cki.Modifiers & ConsoleModifiers.Control) != 0 && cki.Key == ConsoleKey.F)
                    _pendingSwitch = Switch.EnterSearch;
            });
    }

    // --- Mode router builders ---

    private static InputRouter BuildAbrirRouter(bool create)
    {
        _mode = Mode.Abrir;

        if (create)
            _explorerRouter = TreeExplorer.AddBindings(_preservedTreePath, new ExplorerOptions { Filter = ExplorerFilter.All, IgnoreSymlinks = true }, null, state =>
                {
                    _treeState = state;
                    _preservedTreePath = state.CurrentNode;
                    RequestRender();
                },
                onSuccess: OnTreeConfirm,
                onCancel: Quit);

        return _explorerRouter;
    }

    private static InputRouter BuildPlaylistMenuRouter()
    {
        _mode = Mode.PlaylistMenu;

        var items = _cachedTitles;
        int start = items.Count > 0 ? Math.Clamp(_preservedPlaylistCursor, 0, items.Count - 1) : 0;
        _menuCursor = start;

        var router = Menu.AddBindings(items, start, cursor => { _menuCursor = cursor; _preservedPlaylistCursor = cursor; RequestRender(); },
            onSuccess: OnPlaylistConfirm, onCancel: Quit);

        return router;
    }

    private static InputRouter BuildPlaylistSearchRouter()
    {
        _mode = Mode.PlaylistSearch;
        _searchState = null;

        var items = _cachedTitles;
        int start = items.Count > 0 ? Math.Clamp(_preservedPlaylistCursor, 0, items.Count - 1) : 0;

        var router = SearchList.AddBindings(items, "  Buscar: » ", start, state =>
            {
                _searchState = state;
                if (state.Filtered.Count > 0)
                {
                    int cursorInFiltered = state.Cursor;
                    if (cursorInFiltered >= 0 && cursorInFiltered < state.Filtered.Count)
                    {
                        string text = state.Filtered[cursorInFiltered].Text;
                        if (_titleIndex.TryGetValue(text, out int originalIdx) && originalIdx >= 0)
                            _preservedPlaylistCursor = originalIdx;
                    }
                }
                RequestRender();
            },
            onSuccess: OnPlaylistConfirm,
            onCancel: () => _pendingSwitch = Switch.ExitSearch);

        // Tab: también sale del search (vuelve a PlaylistMenu).
        router.Bind("Tab", "salir búsqueda", () => _pendingSwitch = Switch.ExitSearch, ConsoleKey.Tab);

        return router;
    }

    private static void DoSwitch()
    {
        if (_pendingSwitch == null) return;

        if (_modeRouter != null) _rootRouter.RemoveChild(_modeRouter);

        _modeRouter = _pendingSwitch switch
        {
            Switch.Tab => _mode == Mode.Abrir ? BuildPlaylistMenuRouter() : BuildAbrirRouter(false),
            Switch.EnterSearch => BuildPlaylistSearchRouter(),
            Switch.ExitSearch => BuildPlaylistMenuRouter(),
            Switch.AfterTreeConfirm => BuildPlaylistMenuRouter(),
            _ => _modeRouter
        };
        _pendingSwitch = null;

        _rootRouter.AddChild(_modeRouter);
        RequestRender();
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
                var files = LinuxKRL.GetReadableFiles(path, MediaUtils.IsMediaFile).OrderBy(f => f).ToList();
                if (files.Count > 0)
                {
                    _playlist.AddNew(files);
                    SetStatus($"Cargados {files.Count} archivos");
                }
                else
                    SetStatus("Carpeta sin archivos soportados");
            }
            else if (File.Exists(path) && MediaUtils.IsMediaFile(path))
            {
                _playlist.Add(path);
                SetStatus($"Agregado: {Path.GetFileName(path)}");
            }
            else
                SetStatus("Archivo no soportado");
        }
        catch (Exception ex) { SetStatus($"Error: {ex.Message}"); }

        // Después de confirmar en Abrir, ir a PlaylistMenu. Diferido para no tocar el árbol durante Handle().
        _pendingSwitch = Switch.AfterTreeConfirm;
    }

    // --- Render ---

    private static void Render()
    {
        int W = Console.WindowWidth, H = Console.WindowHeight;
        if (W < 40 || H < 12) return;

        if (_mode != Mode.Abrir) RefreshTitleIndex();

        _canvas.Resize(W, H);

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

        _abrirCanvas = GetSubCanvas(_abrirCanvas, x1 + 1, y1 + 1, x1 + width - 2, y1 + height - 2);
        int w = _abrirCanvas.Width, h = _abrirCanvas.Height;

        if (_treeState == null) return;
        var state = _treeState.Value;

        string path = state.CurrentNode;
        string pathDisplay = path.Length > w - 2 ? "…" + path[^(w - 3)..] : path;
        _abrirCanvas.WriteAtAndClear(0, 0, $"{ThemeColors.Primary}{pathDisplay}{ThemeColors.Reset}");
        _abrirCanvas.WriteAt(0, 1, ThemeColors.Dim + new string('─', w) + ThemeColors.Reset);

        if (!string.IsNullOrEmpty(_statusMsg))
            _abrirCanvas.WriteAtAndClear(0, h - 2, $"{ThemeColors.Warning}{TruncateVisible(_statusMsg, w)}{ThemeColors.Reset}");

        // nonListRows: path(0) + separator(1) + status(h-2) + scrollInd(h-1) = 2
        _abrirScroll.Update(state.Cursor, state.Entries.Count, Console.WindowHeight - (h - 2));
        int visible = Math.Max(1, _abrirScroll.VisibleRows);

        int listY = 2;
        if (state.Entries.Count == 0)
            _abrirCanvas.WriteAtAndClear(0, listY, $"  {(state.IsBlocked ? "(carpeta bloqueada)" : "(carpeta vacía)")}", ThemeColors.Dim);
        else
        {
            _abrirCanvas.DrawList(state.Entries, 0, listY, visible, _abrirScroll.Scroll, false, (entry, i) =>
            {
                string name = entry.IsDirectory ? entry.Name + "/" : entry.Name;
                string cursorMark = (i == _abrirScroll.Cursor) ? $"{ThemeColors.Selector}{ConsoleGlyphs.Indicator}{ThemeColors.Reset} " : "  ";
                string nameCol = entry.IsDirectory
                    ? (i == _abrirScroll.Cursor ? $"{ThemeColors.Selector}{AnsiColor.Bold}{name}{ThemeColors.Reset}" : $"{ThemeColors.Info}{name}{ThemeColors.Reset}")
                    : (i == _abrirScroll.Cursor ? $"{ThemeColors.Selector}{AnsiColor.Bold}{name}{ThemeColors.Reset}" : name);
                return $"{cursorMark}{nameCol}";
            });
        }

        if (_abrirScroll.Scroll > 0)
            _abrirCanvas.WriteAt(w / 2 - 14, listY - 1, $"↑ {_abrirScroll.Scroll} ", ThemeColors.Dim);
        int remaining = state.Entries.Count - (_abrirScroll.Scroll + visible);
        if (remaining > 0)
            _abrirCanvas.WriteAtAndClear(w / 2 - 14, h - 1, $"↓ {remaining} ", ThemeColors.Dim);
    }

    private static void RenderPlaylist(int x1, int y1, int width, int height)
    {
        int half = width / 2;

        string title = _mode == Mode.PlaylistSearch ? " Playlist (buscar) " : " Playlist ";
        _canvas.DrawBorder(x1, y1, x1 + half - 1, y1 + height - 1, ThemeColors.Dim);
        _canvas.WriteAt(x1 + 2, y1, title, ThemeColors.Dim);

        _canvas.DrawBorder(x1 + half, y1, x1 + width - 1, y1 + height - 1, ThemeColors.Dim);
        _canvas.WriteAt(x1 + half + 2, y1, " Specs ", ThemeColors.Dim);

        _plCanvas = GetSubCanvas(_plCanvas, x1 + 1, y1 + 1, x1 + half - 2, y1 + height - 2);
        _spCanvas = GetSubCanvas(_spCanvas, x1 + half + 1, y1 + 1, x1 + width - 2, y1 + height - 2);

        if (_mode == Mode.PlaylistSearch && _searchState != null)
            RenderPlaylistSearch(_plCanvas, _searchState.Value);
        else
            RenderPlaylistMenu(_plCanvas);

        RenderSpecsContent(_spCanvas);
    }

    /// <summary>Render de Playlist en modo Menu (sin prompt, cursor directo en _menuCursor).</summary>
    private static void RenderPlaylistMenu(VirtualCanvas c)
    {
        int w = c.Width, h = c.Height;

        if (_playlist.Items.Count == 0)
        {
            c.WriteAtAndClear(0, 0, "  (vacío — presioná Tab para abrir archivos)", ThemeColors.Dim);
            return;
        }

        c.WriteAtAndClear(0, 0, $"{ThemeColors.Primary}Playlist ({_playlist.Items.Count} items){ThemeColors.Reset}  {ThemeColors.Dim}Ctrl+F buscar{ThemeColors.Reset}");
        c.WriteAt(0, 1, ThemeColors.Dim + new string('─', w) + ThemeColors.Reset);

        _playlistScroll.Update(_menuCursor, _playlist.Items.Count, Console.WindowHeight - (h - 2));
        int visible = Math.Max(1, _playlistScroll.VisibleRows);

        // DrawList se encarga del bucle + WriteAtAndClear + limpiar filas no usadas.
        c.DrawList(_playlist.Items, 0, 2, visible, _playlistScroll.Scroll, false, (item, i) =>
            FormatPlaylistLine(item.Title, item.DurationText, item.IsCurrent, i == _playlistScroll.Cursor, w));

        if (_playlistScroll.Scroll > 0)
            c.WriteAt(w - 14, 1, $"↑ {_playlistScroll.Scroll} ", ThemeColors.Dim);
        int remaining = _playlist.Items.Count - (_playlistScroll.Scroll + visible);
        if (remaining > 0)
            c.WriteAt(w - 14, h - 1, $"↓ {remaining} ", ThemeColors.Dim);
    }

    /// <summary>Render de Playlist en modo Search (con prompt + cursor real).</summary>
    private static void RenderPlaylistSearch(VirtualCanvas c, SearchListState state)
    {
        int w = c.Width, h = c.Height;

        c.WriteAtAndClear(0, 0, $"{state.LastPromptLine}{AnsiColor.Bold}{state.Query}{ThemeColors.Reset}");
        c.WriteAt(0, 1, ThemeColors.Dim + new string('─', w) + ThemeColors.Reset);

        _playlistScroll.Update(state.Cursor, state.Filtered.Count, Console.WindowHeight - (h - 3));
        int visible = Math.Max(1, _playlistScroll.VisibleRows);

        if (state.Filtered.Count == 0)
        {
            c.WriteAtAndClear(0, 2, "  (sin resultados — Esc para volver)", ThemeColors.Dim);

            // limpia los elementos anteiores
            c.ClearArea(0, 3, w - 1, h - 1);
        }
        else
        {
            // DrawList recibe los filtered. El formatter resuelve originalIdx vía _titleIndex.
            c.DrawList(state.Filtered, 0, 2, visible, _playlistScroll.Scroll, false, (item, i) =>
            {
                _titleIndex.TryGetValue(item.Text, out int originalIdx);
                string dur = originalIdx >= 0 ? _playlist.Items[originalIdx].DurationText : "--:--";
                bool isCurrent = originalIdx >= 0 && _playlist.Items[originalIdx].IsCurrent;
                return FormatPlaylistLine(item.Text, dur, isCurrent, i == _playlistScroll.Cursor, w);
            });

            if (_playlistScroll.Scroll > 0)
                c.WriteAtAndClear(w - 14, 1, $"↑ {_playlistScroll.Scroll} ", ThemeColors.Dim);
            int remaining = state.Filtered.Count - (_playlistScroll.Scroll + visible);
            if (remaining > 0)
                c.WriteAtAndClear(w - 14, h - 1, $"↓ {remaining} ", ThemeColors.Dim);
        }

        // Cursor real del prompt.
        _canvas.CursorVisible = true;
        int cursorX = c.X + state.PromptLength + state.QueryCursorPos;
        if (cursorX < c.X + w) _canvas.CursorPos = (cursorX, c.Y);
    }

    /// <summary>Formatea una línea de playlist: marker + cursorMark + title (padded) + dur.</summary>
    private static string FormatPlaylistLine(string title, string dur, bool isCurrent, bool isCursor, int w)
    {
        int durLen = dur.Length + 1;
        string titleTrim = TruncateVisible(title, w - durLen - 5);
        string titleCol = isCursor ? $"{ThemeColors.Selector}{AnsiColor.Bold}{titleTrim}{ThemeColors.Reset}" : titleTrim;
        string line = $"{(isCurrent ? $"{ThemeColors.Success}▶{ThemeColors.Reset} " : "  ")}{(isCursor ? $"{ThemeColors.Selector}{ConsoleGlyphs.Indicator}{ThemeColors.Reset} " : "  ")}{titleCol}";
        int pad = w - line.GetRealWidth() - durLen;
        if (pad > 0) line += new string(' ', pad);
        return line + $"{ThemeColors.Dim}{dur}{ThemeColors.Reset}";
    }

    // Cache de FileInfo.Length por path (el syscall es caro para hacerlo cada render).
    private static string _cachedSizePath, _cachedSizeValue;
    private static string GetItemSize(string path)
    {
        if (path == _cachedSizePath) return _cachedSizeValue;
        _cachedSizePath = path;
        return _cachedSizeValue = File.Exists(path) ? FileUtils.FormatFileSize(new FileInfo(path).Length) : null;
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
            c.WriteAt(0, y, label, ThemeColors.Dim);
            c.WriteAtAndClear(label.Length, y, TruncateVisible(string.IsNullOrWhiteSpace(value) ? "—" : value, w - label.Length - 1));
            y++;
        }

        WriteRow("Title: ", item.Title);
        WriteRow("Artist: ", item.Artist);
        WriteRow("Duration: ", item.DurationText);
        WriteRow("Size: ", GetItemSize(item.Path));
        WriteRow("Path: ", item.Path);

        y++;
        if (y < h - 1)
            c.WriteAtAndClear(0, y, $"{(item.IsCurrent ? $"{ThemeColors.Success}● Reproduciendo" : $"{ThemeColors.Dim}○ En cola")}{ThemeColors.Reset}");
    }

    private static void RenderMiniPlayer(int x1, int y1, int width, int height)
    {
        _canvas.DrawBorder(x1, y1, x1 + width - 1, y1 + height - 1, ThemeColors.Primary);
        _canvas.WriteAt(x1 + 2, y1, " Player ", ThemeColors.Primary);

        _miniCanvas = GetSubCanvas(_miniCanvas, x1 + 1, y1 + 1, x1 + width - 2, y1 + height - 2);
        int w = _miniCanvas.Width;

        string title = string.IsNullOrEmpty(_title) ? (_mpv.CurrentPath != null ? Path.GetFileName(_mpv.CurrentPath) : "— sin archivo —") : _title;
        _miniCanvas.WriteAtAndClear(0, 0, $"{ThemeColors.Bright}{TruncateVisible(title, w)}{ThemeColors.Reset}");

        string artist = string.IsNullOrEmpty(_artist) ? "—" : _artist;
        _miniCanvas.WriteAtAndClear(0, 1, $"{ThemeColors.Dim}{TruncateVisible(artist, w)}{ThemeColors.Reset}");

        string posS = MediaUtils.FormatTime(_pos), durS = MediaUtils.FormatTime(_dur);
        int metaLen = posS.Length + durS.Length + 2;
        string posB = new string('━', (int)(_pos / _dur * (w - metaLen))), durB = new string('━', w - metaLen - posB.Length);
        _miniCanvas.WriteAt(0, 2, $"{posS} {ThemeColors.Primary}{posB}{ThemeColors.Reset}{ThemeColors.Dim}{durB}{ThemeColors.Reset} {durS}");

        _miniCanvas.ClearLine(3);
        string volS = _mute ? $"{ThemeColors.Dim}Mute{ThemeColors.Reset}" : $"{(int)(_vol * 100)}%";
        int volBarW = w / 2 - volS.GetVisualLength() + 5 - 1;
        string volBF = new string('━', (int)(_vol * volBarW)), volBUF = new string('━', volBarW - volBF.Length);
        _miniCanvas.WriteAt(0, 3, $"vol: {volS} {volBF}{ThemeColors.Dim}{volBUF}{ThemeColors.Reset}");

        string right = $"{(_isPlaying ? "⏸" : "▶")} Loop: {(_loop == LoopMode.None ? $"{ThemeColors.Dim}None" : _loop == LoopMode.Playlist ? $"{ThemeColors.Bright}Playlist" : $"{ThemeColors.Bright}Track")}{ThemeColors.Reset} Shuffle: {(_shuffle ? $"{ThemeColors.Bright}On" : $"{ThemeColors.Dim}Off")}{ThemeColors.Reset}";
        int rightLen = right.GetVisualLength();
        if (rightLen < w)
            _miniCanvas.WriteAt(w - rightLen, 3, right);
    }

    // --- Utils ---

    private static Dictionary<string, int> _titleIndex = new(StringComparer.Ordinal);
    private static int _titleIndexCount = -1;
    private static List<string> _cachedTitles = new();

    private static void RefreshTitleIndex()
    {
        if (_titleIndexCount == _playlist.Items.Count) return;
        _titleIndex.Clear();
        _cachedTitles.Clear();
        for (int j = 0; j < _playlist.Items.Count; j++)
        {
            string title = _playlist.Items[j].Title;
            _titleIndex.TryAdd(title, j);
            _cachedTitles.Add(title);
        }
        _titleIndexCount = _playlist.Items.Count;
    }

    private static string TruncateVisible(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        if (s.GetRealWidth() <= max) return s;
        if (max <= 1) return "…";
        return s.Truncate(max - 1) + "…";
    }
}

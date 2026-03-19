#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BBKMapEditor
{
    /// <summary>
    /// BBK RPG 地图编辑器主窗口（对齐 MapEditor.cs 格式与渲染逻辑）
    /// 菜单: Tools → BBK RPG → 地图编辑器  (Ctrl+Shift+M)
    /// </summary>
    public class MapEditorWindow : EditorWindow
    {
        // ══════════════════════════════════════════════════════════════════════
        //  常量
        // ══════════════════════════════════════════════════════════════════════
        private const int TILE_W = 16;   // BBK tile 原始尺寸
        private const int TILE_H = 16;
        private const int TILE_COUNT = 127;  // 每个 til 文件的 tile 数
        private const int SIDEBAR_WIDTH = 220;
        private const int TOOLBAR_HEIGHT = 28;
        private const int STATUS_HEIGHT = 22;
        private const int UNDO_MAX = 30;
        private const int ZOOM_MIN = 1;
        private const int ZOOM_MAX = 6;

        // 叠加色（与 MapEditor.cs 保持一致）
        private static readonly Color WalkColor = new Color(0f, 1f, 0f, 0.25f);
        private static readonly Color BlockColor = new Color(1f, 0f, 0f, 0.18f);
        private static readonly Color GridColor = new Color(0.5f, 0.5f, 1f, 0.35f);
        private static readonly Color EventColor = new Color(1f, 1f, 0f, 1f);
        private static readonly Color BlackColor = Color.black;
        private static readonly Color WhiteColor = Color.white;

        // ── 3×5 像素字模（事件号显示，与 MapEditor.cs 相同）──────────────
        private static readonly byte[,] _font3x5 = new byte[10, 5]
        {
            { 0b111, 0b101, 0b101, 0b101, 0b111 }, // 0
            { 0b010, 0b110, 0b010, 0b010, 0b111 }, // 1
            { 0b111, 0b001, 0b111, 0b100, 0b111 }, // 2
            { 0b111, 0b001, 0b111, 0b001, 0b111 }, // 3
            { 0b101, 0b101, 0b111, 0b001, 0b001 }, // 4
            { 0b111, 0b100, 0b111, 0b001, 0b111 }, // 5
            { 0b111, 0b100, 0b111, 0b101, 0b111 }, // 6
            { 0b111, 0b001, 0b001, 0b001, 0b001 }, // 7
            { 0b111, 0b101, 0b111, 0b101, 0b111 }, // 8
            { 0b111, 0b101, 0b111, 0b001, 0b111 }, // 9
        };

        // ══════════════════════════════════════════════════════════════════════
        //  状态字段
        // ══════════════════════════════════════════════════════════════════════

        // 地图数据
        private MapData _map;
        private string _mapFilePath = "";
        private bool _isDirty = false;

        // 多 .til 文件（与 MapEditor.cs 对齐）
        private Color[][] _allTiles;          // 合并后的全局 tile 像素数组
        private int _loadedTilCount;
        private string[] _loadedTilPaths;

        // 地图渲染缓存（用 Color[] canvas 渲染，与 MapEditor.cs 一致）
        private Texture2D _mapRenderTexture;
        private bool _mapRenderDirty = true;

        // 显示选项（与 MapEditor.cs 一致）
        private bool _showWalkable = true;
        private bool _showEvents = true;
        private bool _showGrid = false;
        private float _zoom = 2f;

        // 编辑工具
        private EditTool _currentTool = EditTool.Paint;
        private int _selectedTile = 0;    // tile索引 0~126
        private bool _paintWalkable = true; // 画笔时设置的可行走值
        private int _selectedEventId = 0;

        // 视口
        private Vector2 _scrollOffset = Vector2.zero;
        private Vector2 _paletteScroll = Vector2.zero;
        private Rect _mapViewRect;

        // 拖拽绘制
        private bool _isPainting = false;
        private bool _isErasing = false;
        private Vector2Int _lastPaintedCell = new Vector2Int(-1, -1);

        // 矩形选区
        private bool _isSelecting = false;
        private Vector2Int _selectStart = Vector2Int.zero;
        private Vector2Int _selectEnd = Vector2Int.zero;

        // Undo/Redo（存储 RawData 快照）
        private readonly Stack<byte[]> _undoStack = new Stack<byte[]>();
        private readonly Stack<byte[]> _redoStack = new Stack<byte[]>();

        // 悬停
        private Vector2Int _hoverCell = new Vector2Int(-1, -1);

        // 新建地图对话框
        private bool _showNewMapDialog = false;
        private int _newMapWidth = 20;
        private int _newMapHeight = 15;
        private string _newMapName = "新地图";
        private int _newTilIndex = 1;

        // 状态栏
        private string _statusMessage = "就绪 - 打开或新建地图以开始编辑";

        private enum EditTool { Paint, Erase, Fill, FillRect, Eyedropper, WalkToggle, Event }

        // ══════════════════════════════════════════════════════════════════════
        //  菜单 & 生命周期
        // ══════════════════════════════════════════════════════════════════════
        [MenuItem("Tools/BBK RPG/地图编辑器 %#m")]
        public static void ShowWindow()
        {
            var win = GetWindow<MapEditorWindow>("BBK 地图编辑器");
            win.minSize = new Vector2(900, 600);
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("BBK 地图编辑器");
            wantsMouseMove = true;
        }

        private void OnDisable() => ClearRenderCache();

        // ══════════════════════════════════════════════════════════════════════
        //  OnGUI 入口
        // ══════════════════════════════════════════════════════════════════════
        private void OnGUI()
        {
            DrawToolbar();
            DrawInfoBar();
            DrawMainLayout();
            DrawStatusBar();
            HandleKeyboardShortcuts();
            if (_showNewMapDialog) DrawNewMapDialog();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  工具栏
        // ══════════════════════════════════════════════════════════════════════
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar, GUILayout.Height(TOOLBAR_HEIGHT));

            // 文件
            if (GUILayout.Button("新建", EditorStyles.toolbarButton, GUILayout.Width(44)))
                _showNewMapDialog = true;
            if (GUILayout.Button("打开", EditorStyles.toolbarButton, GUILayout.Width(44)))
                OpenMapFile();

            GUI.enabled = _map != null;
            if (GUILayout.Button("保存", EditorStyles.toolbarButton, GUILayout.Width(44)))
                SaveMapFile(false);
            if (GUILayout.Button("另存为", EditorStyles.toolbarButton, GUILayout.Width(54)))
                SaveMapFile(true);
            GUI.enabled = true;

            DrawToolbarSeparator();

            // Til 文件
            if (GUILayout.Button("打开 .til", EditorStyles.toolbarButton, GUILayout.Width(64)))
                OpenTilFile();

            EditorGUI.BeginDisabledGroup(_allTiles == null);
            if (GUILayout.Button("追加 Til", EditorStyles.toolbarButton, GUILayout.Width(60)))
                AppendTilFile();
            EditorGUI.EndDisabledGroup();

            if (_loadedTilCount > 0)
            {
                var tilStyle = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = Color.green } };
                GUILayout.Label(
                    $"✓ Til×{_loadedTilCount}（{_allTiles?.Length ?? 0} tiles）",
                    tilStyle, GUILayout.Width(120));
            }
            else
            {
                var tilStyle = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = new Color(1f, 0.6f, 0.2f) } };
                GUILayout.Label("未加载 .til", tilStyle, GUILayout.Width(72));
            }

            DrawToolbarSeparator();

            // Undo/Redo
            GUI.enabled = _map != null && _undoStack.Count > 0;
            if (GUILayout.Button("↩ 撤销", EditorStyles.toolbarButton, GUILayout.Width(54)))
                DoUndo();
            GUI.enabled = _map != null && _redoStack.Count > 0;
            if (GUILayout.Button("↪ 重做", EditorStyles.toolbarButton, GUILayout.Width(54)))
                DoRedo();
            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            // 叠加开关（与 MapEditor.cs 对齐）
            bool w2 = GUILayout.Toggle(_showWalkable, "行走", EditorStyles.toolbarButton, GUILayout.Width(40));
            bool e2 = GUILayout.Toggle(_showEvents, "事件", EditorStyles.toolbarButton, GUILayout.Width(40));
            bool g2 = GUILayout.Toggle(_showGrid, "网格", EditorStyles.toolbarButton, GUILayout.Width(40));
            if (w2 != _showWalkable || e2 != _showEvents || g2 != _showGrid)
            {
                _showWalkable = w2; _showEvents = e2; _showGrid = g2;
                _mapRenderDirty = true;
            }

            GUILayout.Space(4);
            GUILayout.Label("缩放:", EditorStyles.miniLabel, GUILayout.Width(34));
            float newZoom = EditorGUILayout.Slider(_zoom, ZOOM_MIN, ZOOM_MAX, GUILayout.Width(100));
            if (Math.Abs(newZoom - _zoom) > 0.01f) { _zoom = newZoom; Repaint(); }

            // 地图标题
            if (_map != null)
            {
                DrawToolbarSeparator();
                var nameStyle = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Bold };
                GUILayout.Label(
                    $"📍 {_map.MapName}  ({_map.Width}×{_map.Height})  TilIdx={_map.TilIndex}" +
                    (_isDirty ? " *" : ""), nameStyle);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ── 信息栏（与 MapEditor.cs DrawInfoBar 对齐）─────────────────────────
        private void DrawInfoBar()
        {
            if (_map == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                $"文件: {Path.GetFileName(_mapFilePath)}   " +
                $"Type={_map.ResType}  Index={_map.ResIndex}  " +
                $"名称={_map.MapName}  TilIndex={_map.TilIndex}  " +
                $"地图={_map.Width}×{_map.Height} 格  " +
                $"像素={_map.Width * TILE_W}×{_map.Height * TILE_H}");

            if (_loadedTilCount > 0 && _loadedTilPaths != null)
            {
                EditorGUILayout.LabelField(
                    $"Til 文件：{_loadedTilCount} 张，共 {_allTiles?.Length ?? 0} 个 tile",
                    EditorStyles.miniLabel);
                for (int i = 0; i < _loadedTilCount; i++)
                    EditorGUILayout.LabelField(
                        $"  [{i}] {Path.GetFileName(_loadedTilPaths[i])}",
                        EditorStyles.miniLabel);
            }
            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  主体布局（左 + 中 + 右）
        // ══════════════════════════════════════════════════════════════════════
        private void DrawMainLayout()
        {
            float infoH = _map != null ? (22 + (_loadedTilCount > 0 ? (1 + _loadedTilCount) * 18 : 0)) : 0;
            float totalH = position.height - TOOLBAR_HEIGHT - STATUS_HEIGHT - infoH - 4;

            EditorGUILayout.BeginHorizontal(GUILayout.Height(totalH));
            DrawLeftPanel(totalH);
            DrawMapView(totalH);
            DrawRightPanel(totalH);
            EditorGUILayout.EndHorizontal();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  左侧工具面板
        // ══════════════════════════════════════════════════════════════════════
        private void DrawLeftPanel(float height)
        {
            EditorGUILayout.BeginVertical(GUI.skin.box,
                GUILayout.Width(SIDEBAR_WIDTH), GUILayout.Height(height));

            GUILayout.Label("🔧 编辑工具", EditorStyles.boldLabel);
            GUILayout.Space(2);

            DrawToolButton(EditTool.Paint, "✏️ 画笔", "绘制 Tile");
            DrawToolButton(EditTool.Erase, "🗑 橡皮", "清除（tile=0）");
            DrawToolButton(EditTool.Fill, "🪣 填充", "洪水填充");
            DrawToolButton(EditTool.FillRect, "▭ 矩形填充", "拖拽填充矩形");
            DrawToolButton(EditTool.Eyedropper, "💉 吸管", "拾取 Tile 索引");
            DrawToolButton(EditTool.WalkToggle, "🚶 行走切换", "切换格子可行走");
            DrawToolButton(EditTool.Event, "⚡ 事件", "设置事件号");

            DrawSeparator();

            // 当前 Tile
            GUILayout.Label("📌 当前 Tile", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"索引: {_selectedTile}", GUILayout.Width(70));
            GUILayout.Label($"0x{_selectedTile:X2}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            int newTile = EditorGUILayout.IntField("Tile 索引", _selectedTile);
            if (EditorGUI.EndChangeCheck())
                _selectedTile = Mathf.Clamp(newTile, 0, 126);

            // 画笔可行走设置
            if (_currentTool == EditTool.Paint || _currentTool == EditTool.Fill ||
                _currentTool == EditTool.FillRect)
            {
                _paintWalkable = EditorGUILayout.Toggle("可行走", _paintWalkable);
            }

            // 事件号
            if (_currentTool == EditTool.Event)
            {
                _selectedEventId = Mathf.Clamp(
                    EditorGUILayout.IntField("事件号", _selectedEventId), 0, 255);
            }

            // Tile 预览
            DrawTilePreview(_selectedTile);

            DrawSeparator();

            // 悬停信息
            GUILayout.Label("🔍 鼠标位置", EditorStyles.boldLabel);
            if (_map != null && _map.InBounds(_hoverCell.x, _hoverCell.y))
            {
                int tileIdx = _map.GetTileIndex(_hoverCell.x, _hoverCell.y);
                bool walkable = _map.GetWalkable(_hoverCell.x, _hoverCell.y);
                int eventId = _map.GetEventId(_hoverCell.x, _hoverCell.y);

                GUILayout.Label($"坐标: ({_hoverCell.x}, {_hoverCell.y})");
                GUILayout.Label($"Tile: {tileIdx}  {(walkable ? "✅可走" : "🚫阻挡")}");
                if (eventId > 0)
                {
                    var evStyle = new GUIStyle(EditorStyles.label)
                    { normal = { textColor = new Color(1f, 0.8f, 0.2f) } };
                    GUILayout.Label($"事件号: {eventId}", evStyle);
                }
            }
            else GUILayout.Label("—");

            DrawSeparator();

            // 地图属性
            if (_map != null)
            {
                GUILayout.Label("🗺 地图属性", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                string newName = EditorGUILayout.TextField("名称", _map.MapName);
                if (EditorGUI.EndChangeCheck()) { _map.MapName = newName; _isDirty = true; }

                EditorGUI.BeginChangeCheck();
                int newTilIdx = EditorGUILayout.IntField("TilIndex", _map.TilIndex);
                if (EditorGUI.EndChangeCheck()) { _map.TilIndex = newTilIdx; _isDirty = true; }

                GUILayout.Label($"尺寸: {_map.Width} × {_map.Height}", EditorStyles.miniLabel);
                GUILayout.Space(2);

                if (GUILayout.Button("全图填充当前 Tile", GUILayout.Height(22)))
                {
                    PushUndo();
                    _map.Fill(_selectedTile, _paintWalkable);
                    _isDirty = true; _mapRenderDirty = true;
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  中央地图视图（对齐 MapEditor.cs DrawMapCanvas）
        // ══════════════════════════════════════════════════════════════════════
        private void DrawMapView(float height)
        {
            if (_map == null)
            {
                EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.Height(height));
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginVertical();
                GUILayout.Label("BBK RPG 地图编辑器",
                    new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, alignment = TextAnchor.MiddleCenter });
                GUILayout.Space(12);
                if (GUILayout.Button("  📂 打开地图文件  ", GUILayout.Width(160), GUILayout.Height(30)))
                    OpenMapFile();
                GUILayout.Space(6);
                if (GUILayout.Button("  ➕ 新建地图  ", GUILayout.Width(160), GUILayout.Height(30)))
                    _showNewMapDialog = true;
                EditorGUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
                return;
            }

            if (_mapRenderDirty) RebuildMapTexture();

            float dispW = _map.Width * TILE_W * _zoom;
            float dispH = _map.Height * TILE_H * _zoom;

            _scrollOffset = EditorGUILayout.BeginScrollView(_scrollOffset,
                GUILayout.ExpandWidth(true), GUILayout.Height(height));

            _mapViewRect = GUILayoutUtility.GetRect(dispW, dispH);

            if (_mapRenderTexture != null)
                EditorGUI.DrawPreviewTexture(_mapViewRect, _mapRenderTexture, null, ScaleMode.ScaleToFit);
            else
                EditorGUI.DrawRect(_mapViewRect, new Color(0.15f, 0.15f, 0.2f));

            // 叠加网格（Editor 层绘制，缩放准确）
            if (_showGrid)
                DrawGridOverlay();

            // 叠加矩形选区
            if (_isSelecting && _currentTool == EditTool.FillRect)
                DrawSelectionRect();

            // 悬停高亮
            if (_map.InBounds(_hoverCell.x, _hoverCell.y))
                DrawHoverHighlight();

            HandleMapMouseEvents(_mapViewRect);

            EditorGUILayout.EndScrollView();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  右侧调色板（Tile 调色板，从 _allTiles 渲染）
        // ══════════════════════════════════════════════════════════════════════
        private void DrawRightPanel(float height)
        {
            EditorGUILayout.BeginVertical(GUI.skin.box,
                GUILayout.Width(SIDEBAR_WIDTH), GUILayout.Height(height));

            GUILayout.Label("🎨 Tile 调色板", EditorStyles.boldLabel);

            if (_allTiles == null || _allTiles.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "请先加载 .til 文件\n（工具栏 → 打开 .til）", MessageType.Info);
                if (GUILayout.Button("打开 .til"))
                    OpenTilFile();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
                return;
            }

            int totalTiles = _allTiles.Length;
            GUILayout.Label($"共 {totalTiles} 个 Tile（{_loadedTilCount} 张 til）",
                EditorStyles.miniLabel);
            GUILayout.Space(2);

            // 每行显示数量
            int cellPx = Mathf.RoundToInt(TILE_W * 1.5f) + 2; // 26px
            int displayCols = Mathf.Max(1, (SIDEBAR_WIDTH - 16) / cellPx);

            _paletteScroll = EditorGUILayout.BeginScrollView(_paletteScroll);
            EditorGUILayout.BeginVertical();

            for (int row = 0; row * displayCols < totalTiles; row++)
            {
                EditorGUILayout.BeginHorizontal();
                for (int col = 0; col < displayCols; col++)
                {
                    int idx = row * displayCols + col;
                    if (idx >= totalTiles) break;

                    bool isSelected = (idx == _selectedTile);
                    Rect cellRect = GUILayoutUtility.GetRect(cellPx, cellPx,
                        GUILayout.Width(cellPx), GUILayout.Height(cellPx));

                    if (isSelected)
                        EditorGUI.DrawRect(cellRect, new Color(1f, 0.8f, 0.2f, 0.85f));
                    else if (cellRect.Contains(Event.current.mousePosition))
                        EditorGUI.DrawRect(cellRect, new Color(0.5f, 0.8f, 1f, 0.4f));

                    DrawTileFromAllTiles(idx, cellRect);

                    if (Event.current.type == EventType.MouseDown &&
                        cellRect.Contains(Event.current.mousePosition))
                    {
                        _selectedTile = idx;
                        if (_currentTool == EditTool.Erase) _currentTool = EditTool.Paint;
                        Repaint();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  状态栏
        // ══════════════════════════════════════════════════════════════════════
        private void DrawStatusBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar, GUILayout.Height(STATUS_HEIGHT));
            GUILayout.Label(_statusMessage, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (_map != null)
                GUILayout.Label(
                    $"Undo:{_undoStack.Count}/{UNDO_MAX}  工具:{ToolName(_currentTool)}  " +
                    $"Tile:{_selectedTile}  缩放:{_zoom:F1}x",
                    EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  地图渲染（对齐 MapEditor.cs RebuildMapTex 逻辑）
        // ══════════════════════════════════════════════════════════════════════
        private void RebuildMapTexture()
        {
            if (_map == null) return;

            int pixW = _map.Width * TILE_W;
            int pixH = _map.Height * TILE_H;

            // canvas：图像坐标（左上原点），与 MapEditor.cs 一致
            Color[] canvas = new Color[pixW * pixH];

            for (int ty = 0; ty < _map.Height; ty++)
            {
                for (int tx = 0; tx < _map.Width; tx++)
                {
                    int tileIdx = _map.GetTileIndex(tx, ty);
                    bool canW = _map.GetWalkable(tx, ty);
                    int evtN = _map.GetEventId(tx, ty);

                    Color[] tpx = (_allTiles != null && tileIdx < _allTiles.Length)
                        ? _allTiles[tileIdx] : null;

                    int baseX = tx * TILE_W;
                    int baseY = ty * TILE_H;

                    for (int py = 0; py < TILE_H; py++)
                        for (int px = 0; px < TILE_W; px++)
                        {
                            int ci = (baseY + py) * pixW + (baseX + px);

                            // tile 本体（无贴图时用色块）
                            Color c = tpx != null
                                ? tpx[py * TILE_W + px]
                                : GetFallbackColor(tileIdx);

                            // 叠加行走标记
                            if (_showWalkable)
                                c = AlphaBlend(c, canW ? WalkColor : BlockColor);

                            canvas[ci] = c;
                        }

                    // 叠加网格线（写入 canvas）
                    if (_showGrid)
                        DrawGridBorderToCanvas(canvas, pixW, pixH, baseX, baseY);

                    // 叠加事件号
                    if (_showEvents && evtN != 0)
                        DrawEventNumberToCanvas(canvas, pixW, pixH, baseX, baseY, evtN);
                }
            }

            // 翻转 Y（图像坐标 → Unity 左下原点）
            Color[] flipped = new Color[pixW * pixH];
            for (int y = 0; y < pixH; y++)
                Array.Copy(canvas, y * pixW, flipped, (pixH - 1 - y) * pixW, pixW);

            if (_mapRenderTexture != null &&
                (_mapRenderTexture.width != pixW || _mapRenderTexture.height != pixH))
            {
                DestroyImmediate(_mapRenderTexture); _mapRenderTexture = null;
            }
            if (_mapRenderTexture == null)
                _mapRenderTexture = new Texture2D(pixW, pixH, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };

            _mapRenderTexture.SetPixels(flipped);
            _mapRenderTexture.Apply();
            _mapRenderDirty = false;
        }

        // ── Canvas 绘制辅助（与 MapEditor.cs 保持一致）──────────────────────
        private static Color AlphaBlend(Color base_c, Color overlay)
        {
            float a = overlay.a;
            return new Color(
                base_c.r * (1f - a) + overlay.r * a,
                base_c.g * (1f - a) + overlay.g * a,
                base_c.b * (1f - a) + overlay.b * a, 1f);
        }

        private static void SetPx(Color[] canvas, int cw, int ch, int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= cw || y >= ch) return;
            canvas[y * cw + x] = AlphaBlend(canvas[y * cw + x], c);
        }

        private static void DrawGridBorderToCanvas(
            Color[] canvas, int cw, int ch, int ox, int oy)
        {
            for (int px = 0; px < TILE_W; px++)
            {
                SetPx(canvas, cw, ch, ox + px, oy, GridColor);
                SetPx(canvas, cw, ch, ox + px, oy + TILE_H - 1, GridColor);
            }
            for (int py = 0; py < TILE_H; py++)
            {
                SetPx(canvas, cw, ch, ox, oy + py, GridColor);
                SetPx(canvas, cw, ch, ox + TILE_W - 1, oy + py, GridColor);
            }
        }

        private static void DrawEventNumberToCanvas(
            Color[] canvas, int cw, int ch, int ox, int oy, int num)
        {
            string s = num.ToString();
            int drawX = ox + 1, drawY = oy + 1;
            foreach (char ch2 in s)
            {
                int d = ch2 - '0';
                if (d < 0 || d > 9) continue;
                for (int row = 0; row < 5; row++)
                    for (int col = 0; col < 3; col++)
                        if ((_font3x5[d, row] & (0b100 >> col)) != 0)
                            SetPx(canvas, cw, ch, drawX + col, drawY + row, EventColor);
                drawX += 4;
            }
        }

        // ── Editor 层叠加覆盖绘制 ────────────────────────────────────────────
        private void DrawGridOverlay()
        {
            // 网格已在 canvas 里渲染，不需要重复叠加
            // 如果 _showGrid=false 但用户希望编辑时有网格参考，可在这里单独画
        }

        private void DrawSelectionRect()
        {
            int x1 = Mathf.Min(_selectStart.x, _selectEnd.x);
            int y1 = Mathf.Min(_selectStart.y, _selectEnd.y);
            int x2 = Mathf.Max(_selectStart.x, _selectEnd.x);
            int y2 = Mathf.Max(_selectStart.y, _selectEnd.y);
            Rect sel = CellToScreenRect(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
            EditorGUI.DrawRect(sel, new Color(0.3f, 0.7f, 1f, 0.25f));
            Handles.color = new Color(0.3f, 0.7f, 1f, 0.9f);
            Handles.DrawSolidRectangleWithOutline(sel, Color.clear, new Color(0.3f, 0.7f, 1f, 0.9f));
        }

        private void DrawHoverHighlight()
        {
            Rect hr = CellToScreenRect(_hoverCell.x, _hoverCell.y, 1, 1);
            EditorGUI.DrawRect(hr, new Color(1f, 1f, 1f, 0.2f));
            Handles.color = Color.white;
            Handles.DrawSolidRectangleWithOutline(hr, Color.clear, Color.white);
        }

        private Rect CellToScreenRect(int cx, int cy, int w, int h)
        {
            float tileDisp = TILE_W * _zoom;
            return new Rect(
                _mapViewRect.x + cx * tileDisp,
                _mapViewRect.y + cy * tileDisp,
                w * tileDisp, h * tileDisp);
        }

        // ── Tile 预览 ────────────────────────────────────────────────────────
        private void DrawTilePreview(int tileIdx)
        {
            GUILayout.Space(4);
            Rect r = GUILayoutUtility.GetRect(48, 48, GUILayout.Width(48));
            EditorGUI.DrawRect(r, new Color(0.1f, 0.1f, 0.1f));
            DrawTileFromAllTiles(tileIdx, r);
        }

        private void DrawTileFromAllTiles(int tileIdx, Rect rect)
        {
            if (_allTiles != null && tileIdx < _allTiles.Length && _allTiles[tileIdx] != null)
            {
                Color[] px = _allTiles[tileIdx];
                // 转换为 Texture2D 绘制（小尺寸，每次 DrawTileInRect 时只在调色板格子调用）
                var tex = new Texture2D(TILE_W, TILE_H, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Point };
                // 翻转 Y 以适配 Unity 坐标
                Color[] flipped = new Color[TILE_W * TILE_H];
                for (int y = 0; y < TILE_H; y++)
                    for (int x = 0; x < TILE_W; x++)
                        flipped[(TILE_H - 1 - y) * TILE_W + x] = px[y * TILE_W + x];
                tex.SetPixels(flipped);
                tex.Apply();
                GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit, false);
                DestroyImmediate(tex);
            }
            else
            {
                EditorGUI.DrawRect(
                    new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2),
                    GetFallbackColor(tileIdx));
            }
        }

        private static Color GetFallbackColor(int idx)
        {
            if (idx == 0) return new Color(0.15f, 0.15f, 0.2f);
            float h = (idx * 137.508f) % 360f / 360f;
            return Color.HSVToRGB(h, 0.55f, 0.65f);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  鼠标事件处理
        // ══════════════════════════════════════════════════════════════════════
        private void HandleMapMouseEvents(Rect viewRect)
        {
            Event e = Event.current;
            if (!viewRect.Contains(e.mousePosition)) return;

            Vector2Int cell = ScreenToCell(e.mousePosition, viewRect);
            _hoverCell = cell;

            if (_map != null && _map.InBounds(cell.x, cell.y))
            {
                int ti = _map.GetTileIndex(cell.x, cell.y);
                bool wk = _map.GetWalkable(cell.x, cell.y);
                int ev = _map.GetEventId(cell.x, cell.y);
                _statusMessage = $"({cell.x},{cell.y}) Tile={ti}  {(wk ? "可走" : "阻挡")}" +
                                 (ev != 0 ? $"  事件={ev}" : "");
            }

            bool inBounds = _map != null && _map.InBounds(cell.x, cell.y);

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0) HandlePrimaryClick(cell, inBounds, e);
                    else if (e.button == 1) HandleRightClick(cell, inBounds);
                    break;
                case EventType.MouseDrag:
                    if (e.button == 0) HandleMouseDrag(cell, inBounds);
                    break;
                case EventType.MouseUp:
                    FinishPainting(cell);
                    break;
                case EventType.MouseMove:
                    Repaint();
                    break;
                case EventType.ScrollWheel:
                    _zoom = Mathf.Clamp(_zoom - e.delta.y * 0.2f, ZOOM_MIN, ZOOM_MAX);
                    e.Use(); Repaint();
                    break;
            }
        }

        private void HandlePrimaryClick(Vector2Int cell, bool inBounds, Event e)
        {
            if (!inBounds) return;
            switch (_currentTool)
            {
                case EditTool.Paint:
                    PushUndo(); _isPainting = true;
                    PaintCell(cell); break;
                case EditTool.Erase:
                    PushUndo(); _isErasing = true;
                    _map.SetTileIndex(cell.x, cell.y, 0);
                    _isDirty = true; _mapRenderDirty = true; break;
                case EditTool.Fill:
                    PushUndo();
                    FloodFill(cell.x, cell.y, _selectedTile, _paintWalkable);
                    _isDirty = true; _mapRenderDirty = true; break;
                case EditTool.FillRect:
                    _isSelecting = true; _selectStart = cell; _selectEnd = cell; break;
                case EditTool.Eyedropper:
                    _selectedTile = _map.GetTileIndex(cell.x, cell.y);
                    _paintWalkable = _map.GetWalkable(cell.x, cell.y);
                    _currentTool = EditTool.Paint;
                    _statusMessage = $"已拾取 Tile {_selectedTile}"; break;
                case EditTool.WalkToggle:
                    PushUndo();
                    _map.SetWalkable(cell.x, cell.y, !_map.GetWalkable(cell.x, cell.y));
                    _isDirty = true; _mapRenderDirty = true; break;
                case EditTool.Event:
                    PushUndo();
                    _map.SetEventId(cell.x, cell.y, _selectedEventId);
                    _isDirty = true; _mapRenderDirty = true; break;
            }
            _lastPaintedCell = cell;
            e.Use(); Repaint();
        }

        private void HandleRightClick(Vector2Int cell, bool inBounds)
        {
            if (!inBounds) return;
            _selectedTile = _map.GetTileIndex(cell.x, cell.y);
            _paintWalkable = _map.GetWalkable(cell.x, cell.y);
            _statusMessage = $"已拾取 Tile {_selectedTile}  {(_paintWalkable ? "可走" : "阻挡")}";
            Repaint();
        }

        private void HandleMouseDrag(Vector2Int cell, bool inBounds)
        {
            if (!inBounds || cell == _lastPaintedCell) return;
            if (_isPainting) { PaintCell(cell); _lastPaintedCell = cell; Repaint(); }
            else if (_isErasing) { _map.SetTileIndex(cell.x, cell.y, 0); _isDirty = true; _mapRenderDirty = true; _lastPaintedCell = cell; Repaint(); }
            else if (_isSelecting) { _selectEnd = cell; Repaint(); }
        }

        private void FinishPainting(Vector2Int cell)
        {
            if (_isSelecting && _currentTool == EditTool.FillRect)
            {
                _isSelecting = false;
                PushUndo();
                int x1 = Mathf.Min(_selectStart.x, _selectEnd.x);
                int y1 = Mathf.Min(_selectStart.y, _selectEnd.y);
                int x2 = Mathf.Max(_selectStart.x, _selectEnd.x);
                int y2 = Mathf.Max(_selectStart.y, _selectEnd.y);
                _map.FillRect(x1, y1, x2 - x1 + 1, y2 - y1 + 1, _selectedTile, _paintWalkable);
                _isDirty = true; _mapRenderDirty = true;
            }
            _isPainting = false; _isErasing = false;
            Repaint();
        }

        private void PaintCell(Vector2Int cell)
        {
            if (_map == null || !_map.InBounds(cell.x, cell.y)) return;
            _map.SetTileIndex(cell.x, cell.y, _selectedTile);
            _map.SetWalkable(cell.x, cell.y, _paintWalkable);
            _isDirty = true; _mapRenderDirty = true;
        }

        // ── 洪水填充 ──────────────────────────────────────────────────────────
        private void FloodFill(int startX, int startY, int newTileIdx, bool newWalkable)
        {
            int oldTile = _map.GetTileIndex(startX, startY);
            if (oldTile == newTileIdx) return;

            var queue = new Queue<Vector2Int>();
            var visited = new HashSet<int>();
            queue.Enqueue(new Vector2Int(startX, startY));

            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                int key = p.y * _map.Width + p.x;
                if (!_map.InBounds(p.x, p.y) || visited.Contains(key)) continue;
                if (_map.GetTileIndex(p.x, p.y) != oldTile) continue;

                visited.Add(key);
                _map.SetTileIndex(p.x, p.y, newTileIdx);
                _map.SetWalkable(p.x, p.y, newWalkable);
                queue.Enqueue(new Vector2Int(p.x + 1, p.y));
                queue.Enqueue(new Vector2Int(p.x - 1, p.y));
                queue.Enqueue(new Vector2Int(p.x, p.y + 1));
                queue.Enqueue(new Vector2Int(p.x, p.y - 1));
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Til 文件加载（ParseTilToArray + LoadAllTils + AppendTilFile，
        //  与 MapEditor.cs 完全相同）
        // ══════════════════════════════════════════════════════════════════════
        private void OpenTilFile()
        {
            string tilPath = EditorUtility.OpenFilePanelWithFilters(
                "选择 til 文件", "",
                new[] { "TIL 文件", "til", "所有文件", "*" });
            if (string.IsNullOrEmpty(tilPath)) return;

            string tilDir = Path.GetDirectoryName(tilPath);
            LoadAllTils(tilDir, tilPath);
            _mapRenderDirty = true;
            Repaint();
        }

        private void AppendTilFile()
        {
            string tilPath = EditorUtility.OpenFilePanelWithFilters(
                "选择要追加的 til 文件", "",
                new[] { "TIL 文件", "til", "所有文件", "*" });
            if (string.IsNullOrEmpty(tilPath)) return;

            byte[] buf;
            try { buf = File.ReadAllBytes(tilPath); }
            catch (Exception ex) { _statusMessage = $"读取失败：{ex.Message}"; return; }

            Color[][] newTiles = ParseTilToArray(buf, Path.GetFileName(tilPath));
            if (newTiles == null) return;

            int oldLen = _allTiles?.Length ?? 0;
            var merged = new Color[oldLen + newTiles.Length][];
            if (_allTiles != null) Array.Copy(_allTiles, merged, oldLen);
            Array.Copy(newTiles, 0, merged, oldLen, newTiles.Length);
            _allTiles = merged;
            _loadedTilCount++;
            var paths = new List<string>(_loadedTilPaths ?? new string[0]) { tilPath };
            _loadedTilPaths = paths.ToArray();

            _statusMessage = $"已追加 {Path.GetFileName(tilPath)}，共 {_allTiles.Length} 个 tile";
            _mapRenderDirty = true;
            Repaint();
        }

        private void LoadAllTils(string tilDir, string firstTilPath)
        {
            string fname = Path.GetFileNameWithoutExtension(firstTilPath);
            string ext = Path.GetExtension(firstTilPath);
            int dashIdx = fname.LastIndexOf('-');
            string prefix = dashIdx >= 0 ? fname.Substring(0, dashIdx + 1) : fname;
            int startNo = 1;
            if (dashIdx >= 0) int.TryParse(fname.Substring(dashIdx + 1), out startNo);

            var tilPaths = new List<string>();
            for (int n = startNo; n <= startNo + 99; n++)
            {
                string p = Path.Combine(tilDir, $"{prefix}{n}{ext}");
                if (!File.Exists(p)) break;
                tilPaths.Add(p);
            }
            if (tilPaths.Count == 0) { _statusMessage = $"未找到任何 til 文件（起始:{firstTilPath}）"; return; }

            var allList = new List<Color[]>();
            var pathList = new List<string>();
            int loaded = 0;

            foreach (string tp in tilPaths)
            {
                byte[] buf;
                try { buf = File.ReadAllBytes(tp); }
                catch (Exception ex) { _statusMessage = $"读取 {Path.GetFileName(tp)} 失败：{ex.Message}"; break; }

                Color[][] tt = ParseTilToArray(buf, Path.GetFileName(tp));
                if (tt == null) break;

                foreach (var t in tt) allList.Add(t);
                pathList.Add(tp);
                loaded++;
            }

            _allTiles = allList.ToArray();
            _loadedTilCount = loaded;
            _loadedTilPaths = pathList.ToArray();

            if (loaded > 0)
            {
                _statusMessage = $"已加载 {loaded} 张 til，共 {_allTiles.Length} 个 tile";
                _mapRenderDirty = true;
            }
        }

        /// <summary>解析单个 .til 文件 → Color[][]，与 MapEditor.cs 完全一致</summary>
        private Color[][] ParseTilToArray(byte[] buf, string fileName = "")
        {
            if (buf.Length < 6) { _statusMessage = $"{fileName} 文件过小"; return null; }

            int iw = buf[2] & 0xFF;
            int ih = buf[3] & 0xFF;
            int num = buf[4] & 0xFF;
            int mode = buf[5] & 0xFF;

            if (iw != TILE_W || ih != TILE_H)
            { _statusMessage = $"{fileName} tile 尺寸非预期（{iw}×{ih}）"; return null; }
            if (mode != 1 && mode != 2)
            { _statusMessage = $"{fileName} 未知 mode={mode}"; return null; }

            int rowBytes = (iw + 7) / 8;
            int tileBytes = rowBytes * ih * mode;
            int dataLen = num * tileBytes;

            if (buf.Length < 6 + dataLen)
            { _statusMessage = $"{fileName} 数据长度不足（需{6 + dataLen}，实{buf.Length}）"; return null; }

            byte[] pixData = new byte[dataLen];
            Array.Copy(buf, 6, pixData, 0, dataLen);

            var tiles = new Color[num][];
            int iData = 0;

            if (mode == 1)
            {
                for (int t = 0; t < num; t++)
                {
                    Color[] px = new Color[iw * ih];
                    int iOfTmp = 0, cnt = 0;
                    for (int y = 0; y < ih; y++)
                    {
                        for (int x = 0; x < iw; x++)
                        {
                            px[iOfTmp++] = ((pixData[iData] << cnt) & 0x80) != 0
                                ? BlackColor : WhiteColor;
                            if (++cnt >= 8) { cnt = 0; ++iData; }
                        }
                        if (cnt != 0) { cnt = 0; ++iData; }
                    }
                    tiles[t] = px;
                }
            }
            else
            {
                Color transp = new Color(1f, 0f, 1f, 0f);
                for (int t = 0; t < num; t++)
                {
                    Color[] px = new Color[iw * ih];
                    int iOfTmp = 0, cnt = 0;
                    for (int y = 0; y < ih; y++)
                    {
                        for (int x = 0; x < iw; x++)
                        {
                            bool hi2 = ((pixData[iData] << cnt) & 0x80) != 0;
                            bool lo = ((pixData[iData] << cnt << 1) & 0x80) != 0;
                            px[iOfTmp++] = hi2 ? transp : (lo ? BlackColor : WhiteColor);
                            cnt += 2;
                            if (cnt >= 8) { cnt = 0; ++iData; }
                        }
                        if (cnt > 0 && cnt <= 7) { cnt = 0; ++iData; }
                        if (iData % 2 != 0) ++iData;
                    }
                    tiles[t] = px;
                }
            }
            return tiles;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  文件 IO
        // ══════════════════════════════════════════════════════════════════════
        private void OpenMapFile()
        {
            if (_isDirty && _map != null)
                if (!EditorUtility.DisplayDialog("未保存的更改", "当前有未保存的更改，继续？", "继续", "取消"))
                    return;

            string path = EditorUtility.OpenFilePanelWithFilters(
                "选择地图文件", "",
                new[] { "地图文件", "map", "所有文件", "*" });
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                _map = MapData.Load(path);
                _mapFilePath = path;
                _isDirty = false;
                _mapRenderDirty = true;
                _undoStack.Clear(); _redoStack.Clear();
                _statusMessage = $"已加载: {Path.GetFileName(path)} ({_map.Width}×{_map.Height})";

                // 自动加载同目录 ../til/1-N.til
                string tilDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), "..", "til"));
                string tilPath = Path.Combine(tilDir, $"1-{_map.TilIndex}.til");
                if (File.Exists(tilPath))
                    LoadAllTils(tilDir, tilPath);
                else
                {
                    _statusMessage += $"\n（未找到 til 文件，请手动加载）";
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("加载失败", ex.Message, "确定");
                _statusMessage = $"加载失败: {ex.Message}";
            }
            Repaint();
        }

        private void SaveMapFile(bool saveAs)
        {
            if (_map == null) return;
            string path = _mapFilePath;
            if (saveAs || string.IsNullOrEmpty(path))
            {
                path = EditorUtility.SaveFilePanel("保存地图",
                    string.IsNullOrEmpty(_mapFilePath) ? "" : Path.GetDirectoryName(_mapFilePath),
                    _map.MapName, "map");
                if (string.IsNullOrEmpty(path)) return;
            }
            try
            {
                _map.Save(path);
                _mapFilePath = path;
                _isDirty = false;
                _statusMessage = $"已保存: {Path.GetFileName(path)}";
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("保存失败", ex.Message, "确定");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Undo / Redo
        // ══════════════════════════════════════════════════════════════════════
        private void PushUndo()
        {
            if (_map == null) return;
            if (_undoStack.Count >= UNDO_MAX) _undoStack.TryPop(out _);
            _undoStack.Push(_map.SnapshotRaw());
            _redoStack.Clear();
        }

        private void DoUndo()
        {
            if (_map == null || _undoStack.Count == 0) return;
            _redoStack.Push(_map.SnapshotRaw());
            _map.RestoreRaw(_undoStack.Pop());
            _isDirty = true; _mapRenderDirty = true; Repaint();
        }

        private void DoRedo()
        {
            if (_map == null || _redoStack.Count == 0) return;
            _undoStack.Push(_map.SnapshotRaw());
            _map.RestoreRaw(_redoStack.Pop());
            _isDirty = true; _mapRenderDirty = true; Repaint();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  键盘快捷键
        // ══════════════════════════════════════════════════════════════════════
        private void HandleKeyboardShortcuts()
        {
            if (Event.current.type != EventType.KeyDown) return;
            bool ctrl = Event.current.control || Event.current.command;

            if (ctrl && Event.current.keyCode == KeyCode.Z) { DoUndo(); Event.current.Use(); }
            if (ctrl && Event.current.keyCode == KeyCode.Y) { DoRedo(); Event.current.Use(); }
            if (ctrl && Event.current.keyCode == KeyCode.S) { SaveMapFile(false); Event.current.Use(); }
            if (ctrl && Event.current.keyCode == KeyCode.O) { OpenMapFile(); Event.current.Use(); }
            if (!ctrl) switch (Event.current.keyCode)
                {
                    case KeyCode.B: _currentTool = EditTool.Paint; Repaint(); Event.current.Use(); break;
                    case KeyCode.E: _currentTool = EditTool.Erase; Repaint(); Event.current.Use(); break;
                    case KeyCode.G: _currentTool = EditTool.Fill; Repaint(); Event.current.Use(); break;
                    case KeyCode.R: _currentTool = EditTool.FillRect; Repaint(); Event.current.Use(); break;
                    case KeyCode.I: _currentTool = EditTool.Eyedropper; Repaint(); Event.current.Use(); break;
                    case KeyCode.W: _currentTool = EditTool.WalkToggle; Repaint(); Event.current.Use(); break;
                    case KeyCode.Tab: _showGrid = !_showGrid; _mapRenderDirty = true; Repaint(); Event.current.Use(); break;
                }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  新建地图对话框
        // ══════════════════════════════════════════════════════════════════════
        private void DrawNewMapDialog()
        {
            Rect dr = new Rect(position.width / 2 - 155, position.height / 2 - 120, 310, 240);
            EditorGUI.DrawRect(dr, new Color(0.2f, 0.2f, 0.2f, 0.98f));
            Handles.DrawSolidRectangleWithOutline(dr, Color.clear, new Color(0.6f, 0.6f, 0.6f));
            GUILayout.BeginArea(dr);
            GUILayout.Space(10);
            GUILayout.Label("  ➕ 新建地图", EditorStyles.boldLabel);
            DrawSeparator();
            GUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(10);
            EditorGUILayout.BeginVertical();

            _newMapName = EditorGUILayout.TextField("地图名称", _newMapName);
            _newMapWidth = Mathf.Clamp(EditorGUILayout.IntField("宽度", _newMapWidth), 1, 255);
            _newMapHeight = Mathf.Clamp(EditorGUILayout.IntField("高度", _newMapHeight), 1, 255);
            _newTilIndex = Mathf.Clamp(EditorGUILayout.IntField("TilIndex", _newTilIndex), 1, 255);
            GUILayout.Label($"共 {_newMapWidth * _newMapHeight} 格  {_newMapWidth * _newMapHeight * 2} bytes",
                EditorStyles.miniLabel);

            GUILayout.Space(14);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("创建", GUILayout.Height(26)))
            {
                if (_isDirty && _map != null)
                    if (!EditorUtility.DisplayDialog("未保存", "确定创建新地图？", "确定", "取消"))
                        goto cancel;
                _map = new MapData(_newMapWidth, _newMapHeight, _newMapName, 0, 0, _newTilIndex);
                _mapFilePath = "";
                _isDirty = false;
                _mapRenderDirty = true;
                _undoStack.Clear(); _redoStack.Clear();
                _statusMessage = $"已创建: {_newMapName} ({_newMapWidth}×{_newMapHeight})";
                _showNewMapDialog = false;
            }
        cancel:
            if (GUILayout.Button("取消", GUILayout.Height(26)))
                _showNewMapDialog = false;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            GUILayout.Space(10);
            EditorGUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  工具辅助
        // ══════════════════════════════════════════════════════════════════════
        private Vector2Int ScreenToCell(Vector2 screenPos, Rect viewRect)
        {
            if (_map == null) return new Vector2Int(-1, -1);
            float tileDisp = TILE_W * _zoom;
            return new Vector2Int(
                (int)((screenPos.x - viewRect.x) / tileDisp),
                (int)((screenPos.y - viewRect.y) / tileDisp));
        }

        private void DrawToolButton(EditTool tool, string label, string tooltip)
        {
            bool sel = _currentTool == tool;
            var style = new GUIStyle(GUI.skin.button)
            { fontStyle = sel ? FontStyle.Bold : FontStyle.Normal, alignment = TextAnchor.MiddleLeft };
            if (sel) style.normal.background = MakeTex(2, 2, new Color(0.3f, 0.6f, 1f, 0.5f));
            if (GUILayout.Button(new GUIContent(label, tooltip), style, GUILayout.Height(22)))
                _currentTool = tool;
        }

        private static void DrawToolbarSeparator()
        {
            GUILayout.Space(4);
            Rect r = GUILayoutUtility.GetRect(1, TOOLBAR_HEIGHT,
                GUILayout.Width(1), GUILayout.ExpandWidth(false));
            EditorGUI.DrawRect(new Rect(r.x, r.y + 2, 1, r.height - 4),
                new Color(0.5f, 0.5f, 0.5f, 0.5f));
            GUILayout.Space(4);
        }

        private static void DrawSeparator()
        {
            GUILayout.Space(2);
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 1),
                new Color(0.5f, 0.5f, 0.5f, 0.3f));
            GUILayout.Space(2);
        }

        private static string ToolName(EditTool t) => t switch
        {
            EditTool.Paint => "画笔 [B]",
            EditTool.Erase => "橡皮 [E]",
            EditTool.Fill => "填充 [G]",
            EditTool.FillRect => "矩形填充 [R]",
            EditTool.Eyedropper => "吸管 [I]",
            EditTool.WalkToggle => "行走切换 [W]",
            EditTool.Event => "事件",
            _ => t.ToString()
        };

        private void ClearRenderCache()
        {
            if (_mapRenderTexture) { DestroyImmediate(_mapRenderTexture); _mapRenderTexture = null; }
        }

        private static Texture2D _cachedTex;
        private static Color _cachedTexColor = Color.clear;
        private static Texture2D MakeTex(int w, int h, Color col)
        {
            if (_cachedTex != null && _cachedTexColor == col) return _cachedTex;
            _cachedTex = new Texture2D(w, h);
            var px = new Color[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = col;
            _cachedTex.SetPixels(px); _cachedTex.Apply();
            _cachedTexColor = col;
            return _cachedTex;
        }
    }
}
#endif
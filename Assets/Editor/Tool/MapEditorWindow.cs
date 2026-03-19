#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BBKMapEditor
{
    /// <summary>
    /// BBK RPG 地图编辑器主窗口
    /// 菜单: Tools → BBK RPG → 地图编辑器
    /// </summary>
    public class MapEditorWindow : EditorWindow
    {
        // ── 常量 ─────────────────────────────────────────────────────────────
        private const int TILE_SIZE = 16;   // 每个 tile 的显示像素
        private const int TILE_SIZE_MIN = 8;
        private const int TILE_SIZE_MAX = 32;
        private const int PALETTE_COLS = 16;  // 调色板每行数量
        private const int SIDEBAR_WIDTH = 220;
        private const int TOOLBAR_HEIGHT = 28;
        private const int STATUS_HEIGHT = 22;
        private const int UNDO_MAX = 30;

        // ── 状态 ─────────────────────────────────────────────────────────────
        private MapData _map;
        private string _mapFilePath = "";
        private bool _isDirty = false;

        // 渲染
        private Texture2D _tilesTexture;       // Tiles.bmp 加载后的纹理
        private string _tilesTexturePath = "";
        private Texture2D _mapRenderTexture;   // 地图渲染缓存
        private bool _mapRenderDirty = true;
        private int _tileSize = TILE_SIZE;

        // 工具
        private EditTool _currentTool = EditTool.Paint;
        private int _selectedTile = 129;  // 默认地板 tile
        private int _selectedEventId = 0;
        private bool _showEventLayer = true;
        private bool _showGrid = true;

        // 视口
        private Vector2 _scrollOffset = Vector2.zero;
        private Vector2 _paletteScroll = Vector2.zero;
        private Rect _mapViewRect;

        // 拖拽绘制
        private bool _isPainting = false;
        private bool _isErasing = false;
        private Vector2Int _lastPaintedCell = new Vector2Int(-1, -1);

        // 矩形选区（用于 Fill Rect）
        private bool _isSelecting = false;
        private Vector2Int _selectStart = Vector2Int.zero;
        private Vector2Int _selectEnd = Vector2Int.zero;

        // Undo/Redo
        private readonly Stack<ushort[]> _undoStack = new Stack<ushort[]>();
        private readonly Stack<ushort[]> _redoStack = new Stack<ushort[]>();

        // 悬停信息
        private Vector2Int _hoverCell = new Vector2Int(-1, -1);

        // 新建地图对话框
        private bool _showNewMapDialog = false;
        private int _newMapWidth = 20;
        private int _newMapHeight = 15;
        private string _newMapName = "新地图";

        // 状态栏
        private string _statusMessage = "就绪 - 打开或新建地图以开始编辑";

        // ── 工具类型 ──────────────────────────────────────────────────────────
        private enum EditTool { Paint, Erase, Fill, FillRect, Eyedropper, Event }

        // ── 菜单 & 生命周期 ───────────────────────────────────────────────────
        [MenuItem("Tools/BBK RPG/地图编辑器 %#m")]
        public static void ShowWindow()
        {
            var win = GetWindow<MapEditorWindow>("BBK 地图编辑器");
            win.minSize = new Vector2(860, 580);
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("BBK 地图编辑器");
            wantsMouseMove = true;
        }

        private void OnDisable()
        {
            if (_mapRenderTexture) DestroyImmediate(_mapRenderTexture);
        }

        // ── 主 GUI ────────────────────────────────────────────────────────────
        private void OnGUI()
        {
            DrawToolbar();
            DrawMainLayout();
            DrawStatusBar();
            HandleKeyboardShortcuts();

            if (_showNewMapDialog)
                DrawNewMapDialog();
        }

        // ── 工具栏 ────────────────────────────────────────────────────────────
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar, GUILayout.Height(TOOLBAR_HEIGHT));

            // 文件操作
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

            GUILayout.Space(8);
            DrawToolbarSeparator();
            GUILayout.Space(8);

            // 加载 Tiles
            if (GUILayout.Button("加载 Tiles.bmp", EditorStyles.toolbarButton, GUILayout.Width(100)))
                LoadTilesTexture();
            if (_tilesTexture != null)
            {
                var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.green } };
                GUILayout.Label($"✓ {Path.GetFileName(_tilesTexturePath)}", style, GUILayout.Width(120));
            }
            else
            {
                var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 0.6f, 0.2f) } };
                GUILayout.Label("未加载 Tiles", style, GUILayout.Width(80));
            }

            GUILayout.Space(8);
            DrawToolbarSeparator();
            GUILayout.Space(8);

            // Undo / Redo
            GUI.enabled = _map != null && _undoStack.Count > 0;
            if (GUILayout.Button("↩ 撤销", EditorStyles.toolbarButton, GUILayout.Width(54)))
                DoUndo();
            GUI.enabled = _map != null && _redoStack.Count > 0;
            if (GUILayout.Button("↪ 重做", EditorStyles.toolbarButton, GUILayout.Width(54)))
                DoRedo();
            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            // 视图控件
            _showGrid = GUILayout.Toggle(_showGrid, "网格", EditorStyles.toolbarButton, GUILayout.Width(44));
            _showEventLayer = GUILayout.Toggle(_showEventLayer, "事件层", EditorStyles.toolbarButton, GUILayout.Width(54));

            GUILayout.Space(4);
            GUILayout.Label("缩放:", EditorStyles.miniLabel, GUILayout.Width(34));
            int newTileSize = EditorGUILayout.IntSlider(_tileSize, TILE_SIZE_MIN, TILE_SIZE_MAX,
                GUILayout.Width(100));
            if (newTileSize != _tileSize) { _tileSize = newTileSize; _mapRenderDirty = true; }

            // 地图名称显示
            if (_map != null)
            {
                GUILayout.Space(8);
                DrawToolbarSeparator();
                GUILayout.Space(8);
                var nameStyle = new GUIStyle(EditorStyles.miniLabel)
                { fontStyle = FontStyle.Bold };
                GUILayout.Label($"📍 {_map.MapName} ({_map.Width}×{_map.Height})" +
                    (_isDirty ? " *" : ""), nameStyle);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ── 主体布局 ──────────────────────────────────────────────────────────
        private void DrawMainLayout()
        {
            float totalH = position.height - TOOLBAR_HEIGHT - STATUS_HEIGHT;

            EditorGUILayout.BeginHorizontal(GUILayout.Height(totalH));

            // 左侧工具面板
            DrawLeftPanel(totalH);

            // 中央地图视图
            DrawMapView(totalH);

            // 右侧调色板
            DrawRightPanel(totalH);

            EditorGUILayout.EndHorizontal();
        }

        // ── 左侧工具面板 ──────────────────────────────────────────────────────
        private void DrawLeftPanel(float height)
        {
            EditorGUILayout.BeginVertical(
                GUI.skin.box,
                GUILayout.Width(SIDEBAR_WIDTH),
                GUILayout.Height(height));

            GUILayout.Label("🔧 编辑工具", EditorStyles.boldLabel);
            GUILayout.Space(4);

            // 工具选择
            DrawToolButton(EditTool.Paint, "✏️ 画笔", "逐格绘制 Tile");
            DrawToolButton(EditTool.Erase, "🗑 橡皮", "擦除（设为 Tile 0）");
            DrawToolButton(EditTool.Fill, "🪣 填充", "洪水填充同色区域");
            DrawToolButton(EditTool.FillRect, "▭ 矩形填充", "拖拽填充矩形区域");
            DrawToolButton(EditTool.Eyedropper, "💉 吸管", "从地图拾取 Tile");
            DrawToolButton(EditTool.Event, "⚡ 事件", "设置事件 ID");

            GUILayout.Space(8);
            DrawSeparator();
            GUILayout.Space(4);

            // 当前选中 Tile
            GUILayout.Label("📌 当前选中 Tile", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"索引: {_selectedTile}", GUILayout.Width(80));
            GUILayout.Label($"0x{_selectedTile:X2}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            // 手动输入
            EditorGUI.BeginChangeCheck();
            int newTile = EditorGUILayout.IntField("Tile 索引", _selectedTile);
            if (EditorGUI.EndChangeCheck())
                _selectedTile = Mathf.Clamp(newTile, 0, 255);

            if (_currentTool == EditTool.Event)
            {
                GUILayout.Space(4);
                _selectedEventId = EditorGUILayout.IntField("事件 ID", _selectedEventId);
                _selectedEventId = Mathf.Clamp(_selectedEventId, 0, 255);
            }

            // Tile 预览
            DrawTilePreview(_selectedTile);

            GUILayout.Space(8);
            DrawSeparator();
            GUILayout.Space(4);

            // 悬停信息
            GUILayout.Label("🔍 鼠标位置", EditorStyles.boldLabel);
            if (_map != null && _map.InBounds(_hoverCell.x, _hoverCell.y))
            {
                int tile = _map.GetBaseTile(_hoverCell.x, _hoverCell.y);
                int eventId = _map.GetEventId(_hoverCell.x, _hoverCell.y);
                GUILayout.Label($"坐标: ({_hoverCell.x}, {_hoverCell.y})");
                GUILayout.Label($"Tile: {tile} (0x{tile:X2})");
                if (eventId > 0)
                {
                    var evStyle = new GUIStyle(EditorStyles.label)
                    { normal = { textColor = new Color(1f, 0.8f, 0.2f) } };
                    GUILayout.Label($"事件 ID: {eventId}", evStyle);
                }
            }
            else
            {
                GUILayout.Label("—");
            }

            GUILayout.Space(8);
            DrawSeparator();
            GUILayout.Space(4);

            // 地图属性（可编辑）
            if (_map != null)
            {
                GUILayout.Label("🗺 地图属性", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                string newName = EditorGUILayout.TextField("名称", _map.MapName);
                if (EditorGUI.EndChangeCheck()) { _map.MapName = newName; _isDirty = true; }

                GUILayout.Label($"尺寸: {_map.Width} × {_map.Height}");

                GUILayout.Space(4);
                if (GUILayout.Button("调整地图大小…"))
                    ShowResizeDialog();

                GUILayout.Space(4);
                if (GUILayout.Button("全图填充当前 Tile"))
                {
                    PushUndo();
                    _map.Fill((ushort)_selectedTile);
                    _isDirty = true;
                    _mapRenderDirty = true;
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        // ── 地图视图 ──────────────────────────────────────────────────────────
        private void DrawMapView(float height)
        {
            if (_map == null)
            {
                EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.Height(height));
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginVertical();
                GUILayout.Label("BBK RPG 地图编辑器", new GUIStyle(EditorStyles.boldLabel)
                { fontSize = 18, alignment = TextAnchor.MiddleCenter });
                GUILayout.Space(12);
                if (GUILayout.Button("  📂 打开地图文件  ", GUILayout.Width(160), GUILayout.Height(30)))
                    OpenMapFile();
                GUILayout.Space(8);
                if (GUILayout.Button("  ➕ 新建地图  ", GUILayout.Width(160), GUILayout.Height(30)))
                    _showNewMapDialog = true;
                EditorGUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
                return;
            }

            // 渲染地图缓存
            if (_mapRenderDirty)
                RebuildMapTexture();

            // 可滚动视口
            int mapPixW = _map.Width * _tileSize;
            int mapPixH = _map.Height * _tileSize;

            _scrollOffset = EditorGUILayout.BeginScrollView(
                _scrollOffset,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(height));

            // 地图绘制区域
            _mapViewRect = GUILayoutUtility.GetRect(mapPixW, mapPixH);

            // 绘制地图纹理
            if (_mapRenderTexture != null)
                GUI.DrawTexture(_mapViewRect, _mapRenderTexture, ScaleMode.StretchToFill, false);
            else
                EditorGUI.DrawRect(_mapViewRect, new Color(0.15f, 0.15f, 0.2f));

            // 绘制网格
            if (_showGrid && _tileSize >= 8)
                DrawGrid();

            // 绘制事件标记
            if (_showEventLayer)
                DrawEventMarkers();

            // 绘制矩形选区
            if (_isSelecting && _currentTool == EditTool.FillRect)
                DrawSelectionRect();

            // 绘制悬停高亮
            if (_map.InBounds(_hoverCell.x, _hoverCell.y))
                DrawHoverHighlight();

            // 处理鼠标事件
            HandleMapMouseEvents(_mapViewRect);

            EditorGUILayout.EndScrollView();
        }

        // ── 右侧调色板 ────────────────────────────────────────────────────────
        private void DrawRightPanel(float height)
        {
            EditorGUILayout.BeginVertical(
                GUI.skin.box,
                GUILayout.Width(SIDEBAR_WIDTH),
                GUILayout.Height(height));

            GUILayout.Label("🎨 Tile 调色板", EditorStyles.boldLabel);

            if (_tilesTexture == null)
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox("请先加载 Tiles.bmp\n（工具栏 → 加载 Tiles.bmp）", MessageType.Info);
                GUILayout.Space(8);
                if (GUILayout.Button("加载 Tiles.bmp"))
                    LoadTilesTexture();
            }
            else
            {
                // Tile 总数
                int tilesPerRow = _tilesTexture.width / 16;   // 假设每个 tile 16x16
                int tilesPerCol = _tilesTexture.height / 16;
                int totalTiles = tilesPerRow * tilesPerCol;

                GUILayout.Label($"共 {totalTiles} 个 Tile（{tilesPerRow}×{tilesPerCol}）",
                    EditorStyles.miniLabel);
                GUILayout.Space(4);

                _paletteScroll = EditorGUILayout.BeginScrollView(_paletteScroll);

                // 绘制调色板格子
                int displayCols = Mathf.Max(1,
                    (int)((SIDEBAR_WIDTH - 20) / (TILE_SIZE + 2)));
                float cellSize = TILE_SIZE + 2;

                EditorGUILayout.BeginVertical();
                for (int row = 0; row * displayCols < totalTiles; row++)
                {
                    EditorGUILayout.BeginHorizontal();
                    for (int col = 0; col < displayCols; col++)
                    {
                        int idx = row * displayCols + col;
                        if (idx >= totalTiles) break;

                        bool isSelected = (idx == _selectedTile);
                        Rect cellRect = GUILayoutUtility.GetRect(
                            cellSize, cellSize,
                            GUILayout.Width(cellSize),
                            GUILayout.Height(cellSize));

                        // 选中高亮
                        if (isSelected)
                            EditorGUI.DrawRect(cellRect, new Color(1f, 0.8f, 0.2f, 0.8f));
                        else if (cellRect.Contains(Event.current.mousePosition))
                            EditorGUI.DrawRect(cellRect, new Color(0.5f, 0.8f, 1f, 0.4f));

                        // 绘制 tile 图像
                        DrawTileInRect(idx, cellRect);

                        // 点击选择
                        if (Event.current.type == EventType.MouseDown &&
                            cellRect.Contains(Event.current.mousePosition))
                        {
                            _selectedTile = idx;
                            if (_currentTool == EditTool.Erase)
                                _currentTool = EditTool.Paint;
                            Repaint();
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndScrollView();
            }

            // 无 Tile 贴图时的颜色代码调色板
            if (_tilesTexture == null)
            {
                GUILayout.Space(4);
                DrawSeparator();
                GUILayout.Space(4);
                GUILayout.Label("数值调色板（快速选择）", EditorStyles.miniLabel);
                int[] commonTiles = { 0, 60, 63, 64, 67, 68, 69, 102, 129, 166, 180 };
                string[] labels = { "0", "60\n空", "63\n-", "64\n-", "67\n|", "68\n|",
                                      "69\n#", "102\n+", "129\n.", "166\no", "180\nW" };
                EditorGUILayout.BeginHorizontal();
                for (int i = 0; i < commonTiles.Length; i++)
                {
                    bool sel = _selectedTile == commonTiles[i];
                    var style = sel
                        ? new GUIStyle(GUI.skin.button)
                        {
                            normal = { background = MakeTex(2, 2, new Color(1f, 0.8f, 0.2f)) },
                            fontStyle = FontStyle.Bold,
                            fontSize = 8
                        }
                        : new GUIStyle(GUI.skin.button) { fontSize = 8 };
                    if (GUILayout.Button(labels[i], style, GUILayout.Width(28), GUILayout.Height(32)))
                        _selectedTile = commonTiles[i];
                    if (i == 4) { EditorGUILayout.EndHorizontal(); EditorGUILayout.BeginHorizontal(); }
                }
                EditorGUILayout.EndHorizontal();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        // ── 状态栏 ────────────────────────────────────────────────────────────
        private void DrawStatusBar()
        {
            EditorGUILayout.BeginHorizontal(
                EditorStyles.toolbar,
                GUILayout.Height(STATUS_HEIGHT));

            GUILayout.Label(_statusMessage, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            if (_map != null)
            {
                GUILayout.Label(
                    $"Undo: {_undoStack.Count}/{UNDO_MAX}  |  " +
                    $"工具: {ToolName(_currentTool)}  |  " +
                    $"当前Tile: {_selectedTile}",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ── 鼠标事件处理 ─────────────────────────────────────────────────────
        private void HandleMapMouseEvents(Rect viewRect)
        {
            Event e = Event.current;
            if (!viewRect.Contains(e.mousePosition)) return;

            Vector2Int cell = ScreenToCell(e.mousePosition, viewRect);
            _hoverCell = cell;

            // 更新状态栏
            if (_map != null && _map.InBounds(cell.x, cell.y))
            {
                int tile = _map.GetBaseTile(cell.x, cell.y);
                int evId = _map.GetEventId(cell.x, cell.y);
                _statusMessage = $"({cell.x}, {cell.y}) Tile={tile} (0x{tile:X2})" +
                    (evId > 0 ? $"  事件={evId}" : "");
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
                    _tileSize = Mathf.Clamp(_tileSize - (int)e.delta.y, TILE_SIZE_MIN, TILE_SIZE_MAX);
                    _mapRenderDirty = true;
                    e.Use();
                    break;
            }
        }

        private void HandlePrimaryClick(Vector2Int cell, bool inBounds, Event e)
        {
            if (!inBounds) return;

            switch (_currentTool)
            {
                case EditTool.Paint:
                    PushUndo();
                    _isPainting = true;
                    PaintCell(cell);
                    break;

                case EditTool.Erase:
                    PushUndo();
                    _isErasing = true;
                    _map.SetBaseTile(cell.x, cell.y, 0);
                    _isDirty = true;
                    _mapRenderDirty = true;
                    break;

                case EditTool.Fill:
                    PushUndo();
                    FloodFill(cell.x, cell.y, _selectedTile);
                    _isDirty = true;
                    _mapRenderDirty = true;
                    break;

                case EditTool.FillRect:
                    _isSelecting = true;
                    _selectStart = cell;
                    _selectEnd = cell;
                    break;

                case EditTool.Eyedropper:
                    _selectedTile = _map.GetBaseTile(cell.x, cell.y);
                    _currentTool = EditTool.Paint;
                    _statusMessage = $"已拾取 Tile {_selectedTile}";
                    break;

                case EditTool.Event:
                    PushUndo();
                    _map.SetEventId(cell.x, cell.y, _selectedEventId);
                    _isDirty = true;
                    _mapRenderDirty = true;
                    break;
            }

            _lastPaintedCell = cell;
            e.Use();
            Repaint();
        }

        private void HandleRightClick(Vector2Int cell, bool inBounds)
        {
            if (!inBounds) return;
            // 右键 = 吸管
            _selectedTile = _map.GetBaseTile(cell.x, cell.y);
            _statusMessage = $"已拾取 Tile {_selectedTile}";
            Repaint();
        }

        private void HandleMouseDrag(Vector2Int cell, bool inBounds)
        {
            if (!inBounds || cell == _lastPaintedCell) return;

            if (_isPainting)
            {
                PaintCell(cell);
                _lastPaintedCell = cell;
                Repaint();
            }
            else if (_isErasing)
            {
                _map.SetBaseTile(cell.x, cell.y, 0);
                _isDirty = true;
                _mapRenderDirty = true;
                _lastPaintedCell = cell;
                Repaint();
            }
            else if (_isSelecting)
            {
                _selectEnd = cell;
                Repaint();
            }
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
                _map.FillRect(x1, y1, x2 - x1 + 1, y2 - y1 + 1, (ushort)_selectedTile);
                _isDirty = true;
                _mapRenderDirty = true;
            }
            _isPainting = false;
            _isErasing = false;
            Repaint();
        }

        private void PaintCell(Vector2Int cell)
        {
            if (_map == null || !_map.InBounds(cell.x, cell.y)) return;
            _map.SetBaseTile(cell.x, cell.y, _selectedTile);
            _isDirty = true;
            _mapRenderDirty = true;
        }

        // ── 洪水填充 ──────────────────────────────────────────────────────────
        private void FloodFill(int startX, int startY, int newTile)
        {
            int oldTile = _map.GetBaseTile(startX, startY);
            if (oldTile == newTile) return;

            var queue = new Queue<Vector2Int>();
            queue.Enqueue(new Vector2Int(startX, startY));
            var visited = new HashSet<int>();

            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                int key = p.y * _map.Width + p.x;
                if (!_map.InBounds(p.x, p.y) || visited.Contains(key)) continue;
                if (_map.GetBaseTile(p.x, p.y) != oldTile) continue;

                visited.Add(key);
                _map.SetBaseTile(p.x, p.y, newTile);
                queue.Enqueue(new Vector2Int(p.x + 1, p.y));
                queue.Enqueue(new Vector2Int(p.x - 1, p.y));
                queue.Enqueue(new Vector2Int(p.x, p.y + 1));
                queue.Enqueue(new Vector2Int(p.x, p.y - 1));
            }
        }

        // ── 地图纹理渲染 ──────────────────────────────────────────────────────
        private void RebuildMapTexture()
        {
            if (_map == null) return;

            int texW = _map.Width * _tileSize;
            int texH = _map.Height * _tileSize;

            if (_mapRenderTexture == null ||
                _mapRenderTexture.width != texW ||
                _mapRenderTexture.height != texH)
            {
                if (_mapRenderTexture) DestroyImmediate(_mapRenderTexture);
                _mapRenderTexture = new Texture2D(texW, texH, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Point };
            }

            Color[] pixels = new Color[texW * texH];

            for (int y = 0; y < _map.Height; y++)
            {
                for (int x = 0; x < _map.Width; x++)
                {
                    int tileIdx = _map.GetBaseTile(x, y);
                    Color[] tilePixels = GetTilePixels(tileIdx);

                    // 翻转 Y（Texture2D 从下往上，地图从上往下）
                    int py = (_map.Height - 1 - y) * _tileSize;
                    int px = x * _tileSize;

                    for (int ty = 0; ty < _tileSize; ty++)
                        for (int tx = 0; tx < _tileSize; tx++)
                        {
                            int dst = (py + ty) * texW + (px + tx);
                            pixels[dst] = tilePixels[ty * _tileSize + tx];
                        }
                }
            }

            _mapRenderTexture.SetPixels(pixels);
            _mapRenderTexture.Apply();
            _mapRenderDirty = false;
        }

        private Color[] GetTilePixels(int tileIndex)
        {
            Color[] result = new Color[_tileSize * _tileSize];

            if (_tilesTexture != null)
            {
                // 从 Tiles.bmp 中裁取
                int srcTileW = 16, srcTileH = 16; // BBK tile 原始大小
                int cols = _tilesTexture.width / srcTileW;
                int tx = (tileIndex % cols) * srcTileW;
                int ty = _tilesTexture.height - (tileIndex / cols + 1) * srcTileH;

                ty = Mathf.Max(0, ty);
                if (tx + srcTileW <= _tilesTexture.width && ty >= 0)
                {
                    Color[] src = _tilesTexture.GetPixels(tx, ty, srcTileW, srcTileH);
                    // 缩放到 _tileSize
                    for (int py = 0; py < _tileSize; py++)
                        for (int px = 0; px < _tileSize; px++)
                        {
                            int srcX = px * srcTileW / _tileSize;
                            int srcY = py * srcTileH / _tileSize;
                            result[py * _tileSize + px] = src[srcY * srcTileW + srcX];
                        }
                    return result;
                }
            }

            // 无贴图时用颜色代表 tile 类型
            Color c = GetTileColor(tileIndex);
            for (int i = 0; i < result.Length; i++) result[i] = c;
            return result;
        }

        private Color GetTileColor(int idx)
        {
            if (idx == 0) return new Color(0f, 0f, 0f, 0f);    // 透明
            if (idx == 60) return new Color(0.2f, 0.2f, 0.35f, 1f);   // 空/天空
            if (idx == 69) return new Color(0.4f, 0.25f, 0.1f, 1f);   // 墙
            if (idx == 129) return new Color(0.55f, 0.45f, 0.3f, 1f);   // 地板
            if (idx == 166) return new Color(0.3f, 0.55f, 0.3f, 1f);   // 其他
            if (idx == 180) return new Color(0.3f, 0.5f, 0.9f, 1f);   // 水
            // 通用：用 HSV 映射
            float h = (idx * 137.508f) % 360f / 360f;
            return Color.HSVToRGB(h, 0.6f, 0.7f);
        }

        // ── 绘制辅助 ──────────────────────────────────────────────────────────
        private void DrawGrid()
        {
            Handles.color = new Color(0f, 0f, 0f, 0.25f);
            for (int x = 0; x <= _map.Width; x++)
            {
                float px = _mapViewRect.x + x * _tileSize;
                Handles.DrawLine(
                    new Vector3(px, _mapViewRect.y),
                    new Vector3(px, _mapViewRect.yMax));
            }
            for (int y = 0; y <= _map.Height; y++)
            {
                float py = _mapViewRect.y + y * _tileSize;
                Handles.DrawLine(
                    new Vector3(_mapViewRect.x, py),
                    new Vector3(_mapViewRect.xMax, py));
            }
        }

        private void DrawEventMarkers()
        {
            if (_map == null) return;
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = Mathf.Max(6, _tileSize / 3),
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(1f, 0.9f, 0.2f) }
            };

            for (int y = 0; y < _map.Height; y++)
            {
                for (int x = 0; x < _map.Width; x++)
                {
                    int evId = _map.GetEventId(x, y);
                    if (evId <= 0) continue;

                    Rect cellRect = new Rect(
                        _mapViewRect.x + x * _tileSize,
                        _mapViewRect.y + y * _tileSize,
                        _tileSize, _tileSize);

                    EditorGUI.DrawRect(cellRect, new Color(1f, 0.8f, 0f, 0.25f));
                    if (_tileSize >= 10)
                        GUI.Label(cellRect, evId.ToString(), style);
                }
            }
        }

        private void DrawSelectionRect()
        {
            int x1 = Mathf.Min(_selectStart.x, _selectEnd.x);
            int y1 = Mathf.Min(_selectStart.y, _selectEnd.y);
            int x2 = Mathf.Max(_selectStart.x, _selectEnd.x);
            int y2 = Mathf.Max(_selectStart.y, _selectEnd.y);

            Rect sel = new Rect(
                _mapViewRect.x + x1 * _tileSize,
                _mapViewRect.y + y1 * _tileSize,
                (x2 - x1 + 1) * _tileSize,
                (y2 - y1 + 1) * _tileSize);

            EditorGUI.DrawRect(sel, new Color(0.3f, 0.7f, 1f, 0.25f));
            Handles.color = new Color(0.3f, 0.7f, 1f, 0.9f);
            Handles.DrawSolidRectangleWithOutline(sel, Color.clear, new Color(0.3f, 0.7f, 1f, 0.9f));
        }

        private void DrawHoverHighlight()
        {
            Rect hr = new Rect(
                _mapViewRect.x + _hoverCell.x * _tileSize,
                _mapViewRect.y + _hoverCell.y * _tileSize,
                _tileSize, _tileSize);
            EditorGUI.DrawRect(hr, new Color(1f, 1f, 1f, 0.2f));
            Handles.color = Color.white;
            Handles.DrawSolidRectangleWithOutline(hr, Color.clear, Color.white);
        }

        private void DrawTilePreview(int tileIdx)
        {
            GUILayout.Space(4);
            Rect previewRect = GUILayoutUtility.GetRect(48, 48, GUILayout.Width(48));
            EditorGUI.DrawRect(previewRect, new Color(0.1f, 0.1f, 0.1f));

            if (_tilesTexture != null)
            {
                int srcW = 16, srcH = 16;
                int cols = _tilesTexture.width / srcW;
                float u1 = (float)(tileIdx % cols) * srcW / _tilesTexture.width;
                float v1 = 1f - (float)(tileIdx / cols + 1) * srcH / _tilesTexture.height;
                float u2 = u1 + (float)srcW / _tilesTexture.width;
                float v2 = v1 + (float)srcH / _tilesTexture.height;
                GUI.DrawTextureWithTexCoords(_mapViewRect.width > 0 ? previewRect : previewRect,
                    _tilesTexture, new Rect(u1, v1, u2 - u1, v2 - v1));
            }
            else
            {
                EditorGUI.DrawRect(previewRect, GetTileColor(tileIdx));
            }
        }

        private void DrawTileInRect(int tileIdx, Rect rect)
        {
            if (_tilesTexture != null)
            {
                int srcW = 16, srcH = 16;
                int cols = _tilesTexture.width / srcW;
                float u1 = (float)(tileIdx % cols) * srcW / _tilesTexture.width;
                float v1 = 1f - (float)(tileIdx / cols + 1) * srcH / _tilesTexture.height;
                float u2 = u1 + (float)srcW / _tilesTexture.width;
                float v2 = v1 + (float)srcH / _tilesTexture.height;
                GUI.DrawTextureWithTexCoords(rect, _tilesTexture,
                    new Rect(u1, v1, u2 - u1, v2 - v1));
            }
            else
            {
                EditorGUI.DrawRect(
                    new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2),
                    GetTileColor(tileIdx));
            }
        }

        private void DrawToolButton(EditTool tool, string label, string tooltip)
        {
            bool selected = _currentTool == tool;
            var style = new GUIStyle(GUI.skin.button)
            {
                fontStyle = selected ? FontStyle.Bold : FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft
            };
            if (selected)
            {
                style.normal.background =
                    MakeTex(2, 2, new Color(0.3f, 0.6f, 1f, 0.5f));
            }

            if (GUILayout.Button(new GUIContent(label, tooltip), style, GUILayout.Height(24)))
                _currentTool = tool;
        }

        private void DrawToolbarSeparator()
        {
            Rect r = GUILayoutUtility.GetRect(1, TOOLBAR_HEIGHT,
                GUILayout.Width(1), GUILayout.ExpandWidth(false));
            EditorGUI.DrawRect(new Rect(r.x, r.y + 2, 1, r.height - 4),
                new Color(0.5f, 0.5f, 0.5f, 0.5f));
        }

        private static void DrawSeparator()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.3f));
        }

        // ── 键盘快捷键 ────────────────────────────────────────────────────────
        private void HandleKeyboardShortcuts()
        {
            if (Event.current.type != EventType.KeyDown) return;

            bool ctrl = Event.current.control || Event.current.command;

            if (ctrl && Event.current.keyCode == KeyCode.Z) { DoUndo(); Event.current.Use(); }
            if (ctrl && Event.current.keyCode == KeyCode.Y) { DoRedo(); Event.current.Use(); }
            if (ctrl && Event.current.keyCode == KeyCode.S) { SaveMapFile(false); Event.current.Use(); }
            if (ctrl && Event.current.keyCode == KeyCode.O) { OpenMapFile(); Event.current.Use(); }

            // 工具快捷键
            if (!ctrl)
            {
                switch (Event.current.keyCode)
                {
                    case KeyCode.B: _currentTool = EditTool.Paint; Repaint(); Event.current.Use(); break;
                    case KeyCode.E: _currentTool = EditTool.Erase; Repaint(); Event.current.Use(); break;
                    case KeyCode.G: _currentTool = EditTool.Fill; Repaint(); Event.current.Use(); break;
                    case KeyCode.R: _currentTool = EditTool.FillRect; Repaint(); Event.current.Use(); break;
                    case KeyCode.I: _currentTool = EditTool.Eyedropper; Repaint(); Event.current.Use(); break;
                    case KeyCode.Tab: _showGrid = !_showGrid; Repaint(); Event.current.Use(); break;
                }
            }
        }

        // ── Undo / Redo ───────────────────────────────────────────────────────
        private void PushUndo()
        {
            if (_map == null) return;
            if (_undoStack.Count >= UNDO_MAX) _undoStack.TryPop(out _);
            var snapshot = new ushort[_map.Tiles.Length];
            Array.Copy(_map.Tiles, snapshot, _map.Tiles.Length);
            _undoStack.Push(snapshot);
            _redoStack.Clear();
        }

        private void DoUndo()
        {
            if (_map == null || _undoStack.Count == 0) return;
            // 保存当前到 redo
            var cur = new ushort[_map.Tiles.Length];
            Array.Copy(_map.Tiles, cur, _map.Tiles.Length);
            _redoStack.Push(cur);
            // 恢复
            Array.Copy(_undoStack.Pop(), _map.Tiles, _map.Tiles.Length);
            _isDirty = true;
            _mapRenderDirty = true;
            Repaint();
        }

        private void DoRedo()
        {
            if (_map == null || _redoStack.Count == 0) return;
            var cur = new ushort[_map.Tiles.Length];
            Array.Copy(_map.Tiles, cur, _map.Tiles.Length);
            _undoStack.Push(cur);
            Array.Copy(_redoStack.Pop(), _map.Tiles, _map.Tiles.Length);
            _isDirty = true;
            _mapRenderDirty = true;
            Repaint();
        }

        // ── 文件操作 ──────────────────────────────────────────────────────────
        private void OpenMapFile()
        {
            if (_isDirty && _map != null)
            {
                if (!EditorUtility.DisplayDialog("未保存的更改",
                    "当前地图有未保存的更改，是否继续打开新文件？", "继续", "取消"))
                    return;
            }

            string path = EditorUtility.OpenFilePanel("打开 BBK 地图文件", "", "map");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                _map = MapData.Load(path);
                _mapFilePath = path;
                _isDirty = false;
                _mapRenderDirty = true;
                _undoStack.Clear();
                _redoStack.Clear();
                _statusMessage = $"已加载: {Path.GetFileName(path)} ({_map.Width}×{_map.Height})";
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
                string defaultName = string.IsNullOrEmpty(_mapFilePath)
                    ? _map.MapName
                    : Path.GetFileNameWithoutExtension(_mapFilePath);
                path = EditorUtility.SaveFilePanel("保存地图", "", defaultName, "map");
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

        private void LoadTilesTexture()
        {
            string path = EditorUtility.OpenFilePanel(
                "加载 Tiles.bmp（Tile 贴图集）", "", "bmp,png,jpg");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Point };
                tex.LoadImage(bytes);

                if (_tilesTexture) DestroyImmediate(_tilesTexture);
                _tilesTexture = tex;
                _tilesTexturePath = path;
                _mapRenderDirty = true;
                _statusMessage = $"已加载 Tiles: {Path.GetFileName(path)} ({tex.width}×{tex.height})";
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("加载失败", $"无法加载贴图:\n{ex.Message}", "确定");
            }
            Repaint();
        }

        // ── 对话框 ────────────────────────────────────────────────────────────
        private void DrawNewMapDialog()
        {
            Rect dialogRect = new Rect(
                position.width / 2 - 150,
                position.height / 2 - 110,
                300, 220);
            EditorGUI.DrawRect(dialogRect, new Color(0.2f, 0.2f, 0.2f, 0.98f));
            Handles.color = new Color(0.5f, 0.5f, 0.5f);
            Handles.DrawSolidRectangleWithOutline(dialogRect, Color.clear, new Color(0.6f, 0.6f, 0.6f));

            GUILayout.BeginArea(dialogRect);
            GUILayout.Space(12);
            GUILayout.Label("  ➕ 新建地图", EditorStyles.boldLabel);
            DrawSeparator();
            GUILayout.Space(8);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);
            EditorGUILayout.BeginVertical();

            _newMapName = EditorGUILayout.TextField("地图名称", _newMapName);
            _newMapWidth = EditorGUILayout.IntField("宽度（Tiles）", _newMapWidth);
            _newMapHeight = EditorGUILayout.IntField("高度（Tiles）", _newMapHeight);
            _newMapWidth = Mathf.Clamp(_newMapWidth, 1, 255);
            _newMapHeight = Mathf.Clamp(_newMapHeight, 1, 255);

            GUILayout.Space(4);
            GUILayout.Label($"共 {_newMapWidth * _newMapHeight} 个 Tile  " +
                $"（{_newMapWidth * _newMapHeight * 2} bytes）",
                EditorStyles.miniLabel);

            GUILayout.Space(16);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("创建", GUILayout.Height(28)))
            {
                if (_isDirty && _map != null)
                {
                    if (!EditorUtility.DisplayDialog("未保存的更改",
                        "当前有未保存的更改，确定创建新地图？", "确定", "取消"))
                        goto cancel;
                }
                _map = new MapData(_newMapWidth, _newMapHeight, _newMapName);
                _mapFilePath = "";
                _isDirty = false;
                _mapRenderDirty = true;
                _undoStack.Clear();
                _redoStack.Clear();
                _statusMessage = $"已创建地图: {_newMapName} ({_newMapWidth}×{_newMapHeight})";
                _showNewMapDialog = false;
            }
        cancel:
            if (GUILayout.Button("取消", GUILayout.Height(28)))
                _showNewMapDialog = false;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            GUILayout.Space(12);
            EditorGUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void ShowResizeDialog()
        {
            if (_map == null) return;
            // 使用简单 IntField 对话框
            _newMapWidth = _map.Width;
            _newMapHeight = _map.Height;
            bool ok = EditorUtility.DisplayDialog("调整地图大小",
                $"当前: {_map.Width} × {_map.Height}\n\n请在控制台中使用 BBKMapEditor.ResizeMap(w, h) 命令，\n或通过代码调用 map.Resize(newW, newH)。",
                "确定");
        }

        // ── 工具方法 ──────────────────────────────────────────────────────────
        private Vector2Int ScreenToCell(Vector2 screenPos, Rect viewRect)
        {
            if (_map == null) return new Vector2Int(-1, -1);
            int cx = (int)((screenPos.x - viewRect.x) / _tileSize);
            int cy = (int)((screenPos.y - viewRect.y) / _tileSize);
            return new Vector2Int(cx, cy);
        }

        private static string ToolName(EditTool t) => t switch
        {
            EditTool.Paint => "画笔 [B]",
            EditTool.Erase => "橡皮 [E]",
            EditTool.Fill => "填充 [G]",
            EditTool.FillRect => "矩形填充 [R]",
            EditTool.Eyedropper => "吸管 [I]",
            EditTool.Event => "事件",
            _ => t.ToString()
        };

        private static Texture2D _cachedTex;
        private static Color _cachedTexColor = Color.clear;
        private static Texture2D MakeTex(int w, int h, Color col)
        {
            if (_cachedTex != null && _cachedTexColor == col) return _cachedTex;
            _cachedTex = new Texture2D(w, h);
            var px = new Color[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = col;
            _cachedTex.SetPixels(px);
            _cachedTex.Apply();
            _cachedTexColor = col;
            return _cachedTex;
        }
    }
}
#endif
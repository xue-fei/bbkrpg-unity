using I18N.CJK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// GUT 游戏流程事件关系图
/// 加载所有 .gut 文件，解析 StartChapter / GutEvent / Goto / If / EnterFight 等
/// 关键指令，自动构建并可视化跨章节的事件跳转关系图。
///
/// 操作：
///   左键拖拽节点   — 移动节点（自动固定）
///   右键/中键拖拽  — 平移画布
///   滚轮           — 缩放画布
///   左键单击节点   — 选中，右侧面板显示详情
///   右键空白       — 取消选中
/// </summary>
public class GutFlowGraph : EditorWindow
{
    // ══════════════════════════════════════════════════════════════
    //  数据模型
    // ══════════════════════════════════════════════════════════════

    private class GutNode
    {
        public string Id;
        public int FileType, FileIndex, SlotId;
        public string SceneName = "";
        public string FilePath = "";
        public List<string> Instructions = new List<string>();

        // 布局（世界坐标）
        public Vector2 Pos;          // 中心点
        public Vector2 Velocity;
        public bool Pinned;
        public float W = NODE_W, H = NODE_H;

        public Rect WorldRect => new Rect(Pos.x - W * 0.5f, Pos.y - H * 0.5f, W, H);
        public List<GutEdge> OutEdges = new List<GutEdge>();
    }

    private class GutEdge
    {
        public string SourceId, TargetId;
        public EdgeKind Kind;
        public string Label;
    }

    private enum EdgeKind { Chapter, Goto, Branch, EnterFight }

    // ══════════════════════════════════════════════════════════════
    //  状态
    // ══════════════════════════════════════════════════════════════

    private Dictionary<string, GutNode> _nodes = new Dictionary<string, GutNode>();
    private List<GutEdge> _edges = new List<GutEdge>();
    private GutNode _selected;
    private string _statusMsg = "点击「加载目录」开始";
    private bool _loaded;

    // 画布变换
    private Vector2 _pan = Vector2.zero;
    private float _zoom = 1f;

    // 交互状态
    private bool _isPanning;
    private Vector2 _panStart;    // 开始平移时的鼠标位置
    private Vector2 _panOrigin;   // 开始平移时的 _pan 值
    private GutNode _dragging;
    private Vector2 _dragNodeOrigin;
    private Vector2 _dragMouseOrigin;

    // 力导向
    private bool _simRunning;
    private int _simIter;
    private const int SIM_MAX_ITER = 300;

    // UI
    private string _filterText = "";
    private bool _showEdgeLabels = true;
    private bool _showInstructions = true;
    private Vector2 _detailScroll;

    // 节点尺寸常量
    private const float NODE_W = 168f;
    private const float NODE_H = 64f;
    private const float NODE_PAD = 8f;

    // ── 颜色 ──────────────────────────────────────────────────────
    static readonly Color C_BG = new Color(0.08f, 0.09f, 0.12f, 1f);
    static readonly Color C_GRID = new Color(0.14f, 0.16f, 0.20f, 1f);
    static readonly Color C_NODE_NORM = new Color(0.14f, 0.18f, 0.26f, 1f);
    static readonly Color C_NODE_ENTRY = new Color(0.09f, 0.22f, 0.18f, 1f);
    static readonly Color C_NODE_SEL = new Color(0.18f, 0.40f, 0.65f, 1f);
    static readonly Color C_NODE_GHOST = new Color(0.20f, 0.20f, 0.22f, 1f);
    static readonly Color C_BORDER = new Color(0.28f, 0.50f, 0.85f, 1f);
    static readonly Color C_BORDER_SEL = new Color(0.55f, 0.88f, 1.00f, 1f);
    static readonly Color C_BAR_NORM = new Color(0.28f, 0.52f, 0.88f, 1f);
    static readonly Color C_BAR_ENTRY = new Color(0.18f, 0.75f, 0.52f, 1f);
    static readonly Color C_TEXT_MAIN = new Color(0.90f, 0.93f, 1.00f, 1f);
    static readonly Color C_TEXT_NAME = new Color(0.98f, 0.88f, 0.35f, 1f);
    static readonly Color C_TEXT_SUB = new Color(0.52f, 0.62f, 0.78f, 1f);
    static readonly Color C_EDGE_CHAP = new Color(0.25f, 0.80f, 0.48f, 0.90f);
    static readonly Color C_EDGE_GOTO = new Color(0.55f, 0.58f, 0.85f, 0.80f);
    static readonly Color C_EDGE_BRAN = new Color(0.92f, 0.65f, 0.18f, 0.85f);
    static readonly Color C_EDGE_FGHT = new Color(0.92f, 0.22f, 0.22f, 0.90f);
    static readonly Color C_PANEL_BG = new Color(0.09f, 0.11f, 0.15f, 1f);
    static readonly Color C_STATUS_BG = new Color(0.06f, 0.07f, 0.09f, 1f);

    private static Encoding _enc;

    // ══════════════════════════════════════════════════════════════
    //  菜单
    // ══════════════════════════════════════════════════════════════

    [MenuItem("工具/GUT 流程关系图")]
    static void Open()
    {
        _enc = _enc ?? new CP936();
        var w = GetWindow<GutFlowGraph>("GUT 流程关系图");
        w.minSize = new Vector2(800, 500);
    }

    void OnEnable() { _enc = _enc ?? new CP936(); EditorApplication.update += OnUpdate; }
    void OnDisable() { EditorApplication.update -= OnUpdate; }

    // ── 定时驱动力导向（不依赖 Repaint 触发）
    void OnUpdate()
    {
        if (!_simRunning) return;
        for (int i = 0; i < 4; i++) // 每帧多跑几步加速收敛
        {
            TickForce();
            _simIter++;
            if (_simIter >= SIM_MAX_ITER) { _simRunning = false; break; }
        }
        Repaint();
    }

    // ══════════════════════════════════════════════════════════════
    //  OnGUI
    // ══════════════════════════════════════════════════════════════

    void OnGUI()
    {
        _enc = _enc ?? new CP936();

        float tbH = EditorStyles.toolbar.fixedHeight;
        float stH = 18f;
        float panelW = (_selected != null) ? 256f : 0f;

        Rect canvasRect = new Rect(0, tbH, position.width - panelW, position.height - tbH - stH);
        Rect statusRect = new Rect(0, position.height - stH, position.width, stH);
        Rect panelRect = new Rect(position.width - panelW, tbH, panelW, position.height - tbH - stH);

        DrawToolbar();
        DrawCanvas(canvasRect);
        if (_selected != null) DrawDetailPanel(panelRect);
        DrawStatusBar(statusRect);

        // 画布输入（在 DrawCanvas 之后，避免 GUILayout 区域干扰）
        HandleCanvasEvents(canvasRect);
    }

    // ══════════════════════════════════════════════════════════════
    //  工具栏
    // ══════════════════════════════════════════════════════════════

    void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        if (GUILayout.Button("加载目录", EditorStyles.toolbarButton, GUILayout.Width(66)))
            LoadDirectory();

        GUILayout.Space(4);

        if (GUILayout.Button("重置视图", EditorStyles.toolbarButton, GUILayout.Width(60)))
        { _pan = Vector2.zero; _zoom = 1f; Repaint(); }

        if (GUILayout.Button("自动布局", EditorStyles.toolbarButton, GUILayout.Width(60)))
            DoAutoLayout();

        if (GUILayout.Button("适应窗口", EditorStyles.toolbarButton, GUILayout.Width(60)))
            FitView();

        GUILayout.Space(6);

        _showEdgeLabels = GUILayout.Toggle(_showEdgeLabels, "边标签", EditorStyles.toolbarButton, GUILayout.Width(48));
        _showInstructions = GUILayout.Toggle(_showInstructions, "指令", EditorStyles.toolbarButton, GUILayout.Width(38));

        GUILayout.Space(8);
        GUILayout.Label("搜索:", EditorStyles.miniLabel, GUILayout.Width(34));
        string nf = EditorGUILayout.TextField(_filterText, EditorStyles.toolbarTextField, GUILayout.Width(110));
        if (nf != _filterText) { _filterText = nf; Repaint(); }

        if (!string.IsNullOrEmpty(_filterText))
            if (GUILayout.Button("×", EditorStyles.toolbarButton, GUILayout.Width(20)))
            { _filterText = ""; Repaint(); }

        GUILayout.FlexibleSpace();

        if (_loaded)
        {
            string simInfo = _simRunning ? $" 布局中({_simIter}/{SIM_MAX_ITER})" : "";
            GUI.color = C_TEXT_SUB;
            GUILayout.Label($"节点 {_nodes.Count}  边 {_edges.Count}{simInfo}  ×{_zoom:F2}",
                            EditorStyles.miniLabel, GUILayout.Width(200));
            GUI.color = Color.white;
        }

        EditorGUILayout.EndHorizontal();
    }

    // ══════════════════════════════════════════════════════════════
    //  画布绘制（全部手动坐标变换，不用 GUI.matrix）
    // ══════════════════════════════════════════════════════════════

    void DrawCanvas(Rect canvas)
    {
        if (Event.current.type != EventType.Repaint) return;

        // 背景
        EditorGUI.DrawRect(canvas, C_BG);

        // 网格
        DrawGrid(canvas);

        // 裁剪
        GUI.BeginClip(canvas);

        // 边
        foreach (var e in _edges) DrawEdge(e, canvas);

        // 节点
        foreach (var node in _nodes.Values) DrawNode(node, canvas);

        GUI.EndClip();

        // 空状态提示
        if (!_loaded)
        {
            var s = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            { fontSize = 12, normal = { textColor = C_TEXT_SUB } };
            GUI.Label(canvas, "点击工具栏「加载目录」，选择含 .gut 文件的文件夹", s);
        }
    }

    // 世界→屏幕（相对 canvas 左上角）
    Vector2 W2S(Vector2 world, Rect canvas) =>
        world * _zoom + _pan + canvas.size * 0.5f;

    // 屏幕→世界
    Vector2 S2W(Vector2 screen, Rect canvas) =>
        (screen - canvas.size * 0.5f - _pan) / _zoom;

    // ── 网格 ──────────────────────────────────────────────────────
    void DrawGrid(Rect canvas)
    {
        float step = 40f * _zoom;
        Vector2 origin = _pan + canvas.size * 0.5f;
        float ox = ((origin.x % step) + step) % step;
        float oy = ((origin.y % step) + step) % step;

        Handles.BeginGUI();
        Handles.color = C_GRID;
        for (float x = ox; x < canvas.width; x += step)
            Handles.DrawLine(new Vector3(x, 0), new Vector3(x, canvas.height));
        for (float y = oy; y < canvas.height; y += step)
            Handles.DrawLine(new Vector3(0, y), new Vector3(canvas.width, y));
        Handles.EndGUI();
    }

    // ── 节点 ──────────────────────────────────────────────────────
    void DrawNode(GutNode node, Rect canvas)
    {
        if (!MatchesFilter(node)) return;

        Vector2 sc = W2S(node.Pos, canvas);  // 屏幕中心
        float sw = node.W * _zoom;
        float sh = node.H * _zoom;
        Rect sr = new Rect(sc.x - sw * 0.5f, sc.y - sh * 0.5f, sw, sh);

        // 剔除视锥外的节点
        if (sr.xMax < 0 || sr.yMax < 0 || sr.xMin > canvas.width || sr.yMin > canvas.height)
            return;

        bool isSel = node == _selected;
        bool isEntry = node.SlotId == 0 && node.FilePath != "";
        bool isGhost = node.FilePath == "";

        Color bgCol = isGhost ? C_NODE_GHOST : (isSel ? C_NODE_SEL : (isEntry ? C_NODE_ENTRY : C_NODE_NORM));
        Color brCol = isSel ? C_BORDER_SEL : C_BORDER;
        Color barCol = isEntry ? C_BAR_ENTRY : C_BAR_NORM;

        // 阴影
        EditorGUI.DrawRect(new Rect(sr.x + 3, sr.y + 3, sr.width, sr.height), new Color(0, 0, 0, 0.35f));

        // 主体
        EditorGUI.DrawRect(sr, bgCol);

        // 顶色条
        float barH = Mathf.Max(3f, 4f * _zoom);
        EditorGUI.DrawRect(new Rect(sr.x, sr.y, sr.width, barH), barCol);

        // 边框
        DrawRect1px(sr, brCol);

        // 文字（只在足够大时绘制）
        if (_zoom > 0.35f)
        {
            float tx = sr.x + NODE_PAD * _zoom;
            float tw = sr.width - NODE_PAD * _zoom * 2;
            float ty = sr.y + barH + 2 * _zoom;

            // ID行
            int fs0 = Mathf.Clamp((int)(9 * _zoom), 7, 11);
            var s0 = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = C_TEXT_MAIN },
                fontStyle = FontStyle.Bold,
                fontSize = fs0,
                clipping = TextClipping.Clip
            };
            string idTxt = isGhost
                ? $"[{node.FileType}-{node.FileIndex}] 未加载"
                : (isEntry ? $"[{node.FileType}-{node.FileIndex}] 入口" : $"[{node.FileType}-{node.FileIndex}] 槽{node.SlotId}");
            float lineH0 = fs0 + 3;
            GUI.Label(new Rect(tx, ty, tw, lineH0), idTxt, s0);
            ty += lineH0;

            // 场景名
            if (!string.IsNullOrEmpty(node.SceneName) && _zoom > 0.45f)
            {
                int fs1 = Mathf.Clamp((int)(10 * _zoom), 8, 13);
                var s1 = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = C_TEXT_NAME },
                    fontStyle = FontStyle.Bold,
                    fontSize = fs1,
                    clipping = TextClipping.Clip
                };
                string name = node.SceneName.Length > 14 ? node.SceneName.Substring(0, 14) + "…" : node.SceneName;
                float lineH1 = fs1 + 2;
                GUI.Label(new Rect(tx, ty, tw, lineH1), name, s1);
                ty += lineH1;
            }

            // 指令摘要
            if (_showInstructions && _zoom > 0.60f && node.Instructions.Count > 0)
            {
                int fs2 = Mathf.Clamp((int)(8 * _zoom), 7, 10);
                var s2 = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = C_TEXT_SUB }, fontSize = fs2, clipping = TextClipping.Clip };
                float lineH2 = fs2 + 2;
                int maxLines = Mathf.FloorToInt((sr.yMax - ty - 2) / lineH2);
                for (int i = 0; i < Mathf.Min(maxLines, node.Instructions.Count); i++)
                {
                    GUI.Label(new Rect(tx, ty, tw, lineH2), node.Instructions[i], s2);
                    ty += lineH2;
                }
            }
        }

        // 固定图钉（小圆点）
        if (node.Pinned && _zoom > 0.5f)
        {
            Handles.BeginGUI();
            Handles.color = new Color(1f, 0.7f, 0.2f, 0.9f);
            Handles.DrawSolidDisc(new Vector3(sr.xMax - 7, sr.y + 7, 0), Vector3.forward, 4f);
            Handles.EndGUI();
        }
    }

    // ── 边 ────────────────────────────────────────────────────────
    void DrawEdge(GutEdge edge, Rect canvas)
    {
        if (!_nodes.TryGetValue(edge.SourceId, out GutNode src)) return;
        if (!_nodes.TryGetValue(edge.TargetId, out GutNode dst)) return;
        if (!MatchesFilter(src) || !MatchesFilter(dst)) return;
        if (src == dst) return;

        Color c = EdgeColor(edge.Kind);

        Vector2 sWorld = src.Pos;
        Vector2 dWorld = dst.Pos;

        // 出发点：右侧中点；终点：左侧中点（若目标在左则改用底/顶）
        bool leftward = dWorld.x < sWorld.x - src.W * 0.5f;
        Vector2 fromW = leftward
            ? new Vector2(sWorld.x, sWorld.y + src.H * 0.5f)
            : new Vector2(sWorld.x + src.W * 0.5f, sWorld.y);
        Vector2 toW = leftward
            ? new Vector2(dWorld.x, dWorld.y - dst.H * 0.5f)
            : new Vector2(dWorld.x - dst.W * 0.5f, dWorld.y);

        Vector2 from = W2S(fromW, canvas);
        Vector2 to = W2S(toW, canvas);

        float dist = Vector2.Distance(from, to);
        float tLen = Mathf.Clamp(dist * 0.45f, 30f * _zoom, 150f * _zoom);
        Vector3 tan1 = leftward
            ? new Vector3(from.x, from.y + tLen, 0)
            : new Vector3(from.x + tLen, from.y, 0);
        Vector3 tan2 = leftward
            ? new Vector3(to.x, to.y - tLen, 0)
            : new Vector3(to.x - tLen, to.y, 0);

        Handles.BeginGUI();
        Handles.DrawBezier(
            new Vector3(from.x, from.y, 0), new Vector3(to.x, to.y, 0),
            tan1, tan2, c, null, 1.6f);

        // 箭头
        Vector2 dir = (to - new Vector2(tan2.x, tan2.y)).normalized;
        DrawArrow(to, dir, c, 5.5f * Mathf.Clamp(_zoom, 0.4f, 1.5f));
        Handles.EndGUI();

        // 边标签
        if (_showEdgeLabels && !string.IsNullOrEmpty(edge.Label) && _zoom > 0.55f)
        {
            Vector2 mid = BezierMid(from, tan1, tan2, to);
            var ls = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = Mathf.Clamp((int)(8 * _zoom), 7, 10),
                normal = { textColor = c },
                alignment = TextAnchor.MiddleCenter
            };
            GUI.Label(new Rect(mid.x - 28, mid.y - 9, 56, 16), edge.Label, ls);
        }
    }

    void DrawArrow(Vector2 tip, Vector2 dir, Color c, float size)
    {
        if (dir.sqrMagnitude < 0.001f) return;
        Vector2 r = new Vector2(-dir.y, dir.x);
        Handles.color = c;
        Handles.DrawAAConvexPolygon(
            new Vector3(tip.x, tip.y, 0),
            new Vector3(tip.x - dir.x * size + r.x * size * 0.5f,
                        tip.y - dir.y * size + r.y * size * 0.5f, 0),
            new Vector3(tip.x - dir.x * size - r.x * size * 0.5f,
                        tip.y - dir.y * size - r.y * size * 0.5f, 0));
    }

    Vector2 BezierMid(Vector2 p0, Vector3 p1, Vector3 p2, Vector2 p3)
    {
        float t = 0.5f, u = 1 - t;
        return u * u * u * p0
            + 3 * u * u * t * new Vector2(p1.x, p1.y)
            + 3 * u * t * t * new Vector2(p2.x, p2.y)
            + t * t * t * p3;
    }

    void DrawRect1px(Rect r, Color c)
    {
        Handles.BeginGUI();
        Handles.color = c;
        Handles.DrawPolyLine(
            new Vector3(r.xMin, r.yMin), new Vector3(r.xMax, r.yMin),
            new Vector3(r.xMax, r.yMax), new Vector3(r.xMin, r.yMax),
            new Vector3(r.xMin, r.yMin));
        Handles.EndGUI();
    }

    Color EdgeColor(EdgeKind k) => k switch
    {
        EdgeKind.Chapter => C_EDGE_CHAP,
        EdgeKind.Goto => C_EDGE_GOTO,
        EdgeKind.Branch => C_EDGE_BRAN,
        EdgeKind.EnterFight => C_EDGE_FGHT,
        _ => C_EDGE_GOTO
    };

    // ══════════════════════════════════════════════════════════════
    //  详情面板
    // ══════════════════════════════════════════════════════════════

    void DrawDetailPanel(Rect panel)
    {
        EditorGUI.DrawRect(panel, C_PANEL_BG);
        // 左分割线
        EditorGUI.DrawRect(new Rect(panel.x, panel.y, 1, panel.height), C_BORDER);

        GUILayout.BeginArea(new Rect(panel.x + 8, panel.y + 8, panel.width - 16, panel.height - 16));

        var titleSt = new GUIStyle(EditorStyles.boldLabel)
        { normal = { textColor = C_TEXT_MAIN }, fontSize = 12 };
        var keySt = new GUIStyle(EditorStyles.miniLabel)
        { normal = { textColor = C_TEXT_SUB } };
        var valSt = new GUIStyle(EditorStyles.miniLabel)
        { normal = { textColor = C_TEXT_MAIN }, wordWrap = true };

        GUILayout.Label("节点详情", titleSt);
        GUILayout.Space(4);

        void Row(string k, string v)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(k, keySt, GUILayout.Width(64));
            GUILayout.Label(v, valSt);
            GUILayout.EndHorizontal();
        }

        if (_selected == null) { GUILayout.EndArea(); return; }

        Row("ID", _selected.Id);
        Row("文件", $"{_selected.FileType}-{_selected.FileIndex}");
        Row("槽号", _selected.SlotId == 0 ? "入口块" : _selected.SlotId.ToString());
        if (!string.IsNullOrEmpty(_selected.SceneName)) Row("场景", _selected.SceneName);
        if (!string.IsNullOrEmpty(_selected.FilePath)) Row("路径", Path.GetFileName(_selected.FilePath));

        GUILayout.Space(6);
        GUILayout.Label("── 指令摘要 ──", keySt);
        _detailScroll = GUILayout.BeginScrollView(_detailScroll, GUILayout.Height(120));
        foreach (var ins in _selected.Instructions)
            GUILayout.Label(ins, valSt);
        GUILayout.EndScrollView();

        GUILayout.Space(6);
        GUILayout.Label("── 出边 ──", keySt);
        foreach (var edge in _selected.OutEdges)
        {
            GUI.color = EdgeColor(edge.Kind);
            string kl = edge.Kind switch
            {
                EdgeKind.Chapter => "[场景]",
                EdgeKind.Goto => "[跳转]",
                EdgeKind.Branch => "[分支]",
                EdgeKind.EnterFight => "[战斗]",
                _ => "→"
            };
            if (GUILayout.Button($"{kl} {edge.TargetId}", EditorStyles.miniButton))
            {
                if (_nodes.TryGetValue(edge.TargetId, out GutNode t))
                {
                    _selected = t;
                    _pan = -t.Pos * _zoom; // 居中
                    Repaint();
                }
            }
            GUI.color = Color.white;
        }

        GUILayout.Space(4);
        if (GUILayout.Button("固定/取消固定", EditorStyles.miniButton))
        { _selected.Pinned = !_selected.Pinned; Repaint(); }

        GUILayout.EndArea();
    }

    // ══════════════════════════════════════════════════════════════
    //  状态栏
    // ══════════════════════════════════════════════════════════════

    void DrawStatusBar(Rect r)
    {
        EditorGUI.DrawRect(r, C_STATUS_BG);
        var s = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = C_TEXT_SUB } };
        GUI.Label(new Rect(r.x + 6, r.y + 1, r.width - 12, r.height - 2), _statusMsg, s);
    }

    // ══════════════════════════════════════════════════════════════
    //  输入处理
    // ══════════════════════════════════════════════════════════════

    void HandleCanvasEvents(Rect canvas)
    {
        Event e = Event.current;

        // 只处理在 canvas 区域内的事件
        bool inCanvas = canvas.Contains(e.mousePosition);
        if (!inCanvas && e.type != EventType.MouseUp) return;

        // 局部坐标（相对 canvas 左上角）
        Vector2 localPos = e.mousePosition - canvas.position;

        switch (e.type)
        {
            case EventType.ScrollWheel:
                if (!inCanvas) break;
                {
                    Vector2 worldBefore = S2W(localPos, canvas);
                    float delta = -e.delta.y * 0.06f;
                    _zoom = Mathf.Clamp(_zoom + delta, 0.15f, 4f);
                    Vector2 worldAfter = S2W(localPos, canvas);
                    _pan += (worldAfter - worldBefore) * _zoom;
                    e.Use(); Repaint();
                }
                break;

            case EventType.MouseDown:
                if (!inCanvas) break;
                if (e.button == 0)
                {
                    // 命中测试
                    GutNode hit = HitTest(localPos, canvas);
                    if (hit != null)
                    {
                        _dragging = hit;
                        _dragNodeOrigin = hit.Pos;
                        _dragMouseOrigin = localPos;
                        _selected = hit;
                    }
                    else
                    {
                        _selected = null;
                    }
                    e.Use(); Repaint();
                }
                else if (e.button == 1 || e.button == 2)
                {
                    _isPanning = true;
                    _panStart = localPos;
                    _panOrigin = _pan;
                    e.Use();
                }
                break;

            case EventType.MouseDrag:
                if (e.button == 0 && _dragging != null)
                {
                    Vector2 delta = (localPos - _dragMouseOrigin) / _zoom;
                    _dragging.Pos = _dragNodeOrigin + delta;
                    _dragging.Pinned = true;
                    _dragging.Velocity = Vector2.zero;
                    e.Use(); Repaint();
                }
                else if ((e.button == 1 || e.button == 2) && _isPanning)
                {
                    _pan = _panOrigin + (localPos - _panStart);
                    e.Use(); Repaint();
                }
                break;

            case EventType.MouseUp:
                _dragging = null;
                _isPanning = false;
                break;

            case EventType.ContextClick:
                if (!inCanvas) break;
                {
                    GutNode hit = HitTest(localPos, canvas);
                    if (hit != null) ShowContextMenu(hit);
                    e.Use();
                }
                break;
        }
    }

    GutNode HitTest(Vector2 localPos, Rect canvas)
    {
        Vector2 world = S2W(localPos, canvas);
        // 从后往前（后绘制的在上层）
        var list = _nodes.Values.ToList();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var node = list[i];
            if (!MatchesFilter(node)) continue;
            if (node.WorldRect.Contains(world)) return node;
        }
        return null;
    }

    void ShowContextMenu(GutNode node)
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent(node.Pinned ? "取消固定" : "固定节点"), false,
            () => { node.Pinned = !node.Pinned; Repaint(); });
        menu.AddItem(new GUIContent("居中到此节点"), false,
            () => { _pan = -node.Pos * _zoom; Repaint(); });
        menu.AddSeparator("");
        menu.AddItem(new GUIContent($"仅显示 {node.FileType}-{node.FileIndex} 的节点"), false,
            () => { _filterText = $"{node.FileType}-{node.FileIndex}"; Repaint(); });
        menu.AddItem(new GUIContent("清除过滤"), false,
            () => { _filterText = ""; Repaint(); });
        menu.ShowAsContext();
    }

    bool MatchesFilter(GutNode node)
    {
        if (string.IsNullOrEmpty(_filterText)) return true;
        string f = _filterText.ToLowerInvariant();
        return node.Id.ToLowerInvariant().Contains(f)
            || node.SceneName.ToLowerInvariant().Contains(f)
            || node.Instructions.Any(s => s.ToLowerInvariant().Contains(f));
    }

    // ══════════════════════════════════════════════════════════════
    //  适应窗口
    // ══════════════════════════════════════════════════════════════

    void FitView()
    {
        if (_nodes.Count == 0) return;
        float tbH = EditorStyles.toolbar.fixedHeight;
        float panelW = (_selected != null) ? 256f : 0f;
        Rect canvas = new Rect(0, tbH, position.width - panelW, position.height - tbH - 18f);

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var n in _nodes.Values)
        {
            minX = Mathf.Min(minX, n.Pos.x - n.W * 0.5f);
            minY = Mathf.Min(minY, n.Pos.y - n.H * 0.5f);
            maxX = Mathf.Max(maxX, n.Pos.x + n.W * 0.5f);
            maxY = Mathf.Max(maxY, n.Pos.y + n.H * 0.5f);
        }
        float bw = maxX - minX + 80f;
        float bh = maxY - minY + 80f;
        _zoom = Mathf.Clamp(Mathf.Min(canvas.width / bw, canvas.height / bh) * 0.9f, 0.15f, 3f);
        _pan = -new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f) * _zoom;
        Repaint();
    }

    // ══════════════════════════════════════════════════════════════
    //  加载与解析
    // ══════════════════════════════════════════════════════════════

    void LoadDirectory()
    {
        _enc = _enc ?? new CP936();
        string def = Application.dataPath + "/../ExRes/gut";
        if (!Directory.Exists(def)) def = Application.dataPath;
        string dir = EditorUtility.OpenFolderPanel("选择 .gut 文件目录", def, "");
        if (string.IsNullOrEmpty(dir)) return;

        _nodes.Clear(); _edges.Clear(); _selected = null; _loaded = false;

        string[] files = Directory.GetFiles(dir, "*.gut");
        if (files.Length == 0) { _statusMsg = $"目录中未找到 .gut 文件：{dir}"; return; }

        int ok = 0, fail = 0;
        foreach (string path in files)
        {
            try { ParseGutFile(path); ok++; }
            catch (Exception ex)
            { Debug.LogWarning($"[GutFlowGraph] {Path.GetFileName(path)}: {ex.Message}"); fail++; }
        }

        BuildAllEdges();
        DoAutoLayout();

        _loaded = true;
        _statusMsg = $"已加载 {ok} 个文件 → {_nodes.Count} 节点 / {_edges.Count} 条边"
                   + (fail > 0 ? $"（{fail} 解析失败）" : "");
        Repaint();
    }

    void ParseGutFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 0x1B) return;

        int fileType = data[0];
        int fileIndex = data[1];
        int length = data[0x18] | (data[0x19] << 8);
        int numEvt = data[0x1A];
        int scriptStart = 0x1B + numEvt * 2;
        int scriptDataOffset = 2 + 1 + numEvt * 2;
        int scriptBytesLen = Mathf.Max(0, length - 2 - 1 - numEvt * 2);
        int scriptEnd = Mathf.Min(scriptStart + scriptBytesLen, data.Length);

        // 读跳转表
        int[] jumpTable = new int[numEvt];
        for (int i = 0; i < numEvt; i++)
        {
            int off = 0x1B + i * 2;
            jumpTable[i] = data[off] | (data[off + 1] << 8);
        }

        // 入口节点
        string entryId = NID(fileType, fileIndex, 0);
        var entryNode = GetOrCreate(entryId, fileType, fileIndex, 0, path);

        // GutEvent 槽节点
        var slotNodes = new Dictionary<int, GutNode>();
        for (int i = 0; i < numEvt; i++)
        {
            if (jumpTable[i] == 0) continue;
            int scriptOff = jumpTable[i] - scriptDataOffset;
            if (scriptOff < 0 || scriptOff >= scriptEnd - scriptStart) continue;
            int slotId = i + 1;
            string nid = NID(fileType, fileIndex, slotId);
            var sn = GetOrCreate(nid, fileType, fileIndex, slotId, path);
            slotNodes[scriptOff] = sn;
        }

        ParseScript(data, scriptStart, scriptEnd, scriptDataOffset,
                    fileType, fileIndex, slotNodes, entryNode, path);
    }

    GutNode GetOrCreate(string id, int ft, int fi, int slot, string path)
    {
        if (!_nodes.TryGetValue(id, out GutNode n))
        {
            n = new GutNode { Id = id, FileType = ft, FileIndex = fi, SlotId = slot, FilePath = path };
            _nodes[id] = n;
        }
        else if (string.IsNullOrEmpty(n.FilePath))
        {
            n.FilePath = path; n.FileType = ft; n.FileIndex = fi; n.SlotId = slot;
        }
        return n;
    }

    void ParseScript(byte[] data, int scriptStart, int scriptEnd, int sdo,
                     int ft, int fi, Dictionary<int, GutNode> slotNodes, GutNode entryNode, string path)
    {
        var sortedOffsets = slotNodes.Keys.OrderBy(k => k).ToList();

        GutNode OwnerOf(int off)
        {
            GutNode owner = entryNode;
            foreach (int so in sortedOffsets) { if (so <= off) owner = slotNodes[so]; else break; }
            return owner;
        }

        string TargetNode(int addr)
        {
            int off = addr - sdo;
            if (slotNodes.TryGetValue(off, out GutNode sn)) return sn.Id;
            return entryNode.Id;
        }

        void AddEdge(GutNode owner, string targetId, EdgeKind kind, string label)
        {
            if (owner.Id == targetId) return;
            if (owner.OutEdges.Any(e => e.TargetId == targetId && e.Kind == kind)) return;
            owner.OutEdges.Add(new GutEdge
            { SourceId = owner.Id, TargetId = targetId, Kind = kind, Label = label });
        }

        int pos = scriptStart;
        while (pos < scriptEnd && pos < data.Length)
        {
            int scriptOff = pos - scriptStart;
            GutNode owner = OwnerOf(scriptOff);
            byte op = data[pos++];

            switch (op)
            {
                // ── 场景名 ──────────────────────────────────────
                case 0x36:
                    {
                        string s = ReadStr(data, ref pos, scriptEnd);
                        if (string.IsNullOrEmpty(owner.SceneName)) owner.SceneName = s;
                        break;
                    }
                // ── 对话 ────────────────────────────────────────
                case 0x0D:
                    {
                        int actor = RU16(data, ref pos);
                        string txt = ReadStr(data, ref pos, scriptEnd);
                        string b = txt.Length > 16 ? txt.Substring(0, 16) + "…" : txt;
                        owner.Instructions.Add($"说[{actor}]: {b}");
                        break;
                    }
                case 0x2F:
                    {
                        string txt = ReadStr(data, ref pos, scriptEnd);
                        string b = txt.Length > 18 ? txt.Substring(0, 18) + "…" : txt;
                        owner.Instructions.Add($"消息: {b}");
                        break;
                    }
                // ── 跳场景 ──────────────────────────────────────
                case 0x0E:
                    {
                        int t = RU16(data, ref pos), idx = RU16(data, ref pos);
                        owner.Instructions.Add($"StartChapter {t}-{idx}");
                        AddEdge(owner, NID(t, idx, 0), EdgeKind.Chapter, $"{t}-{idx}");
                        break;
                    }
                // ── 无条件跳转 ──────────────────────────────────
                case 0x0A:
                    {
                        int addr = RU16(data, ref pos);
                        AddEdge(owner, TargetNode(addr), EdgeKind.Goto, "Goto");
                        break;
                    }
                // ── If NA ───────────────────────────────────────
                case 0x0B:
                    {
                        int varIdx = RU16(data, ref pos), addr = RU16(data, ref pos);
                        AddEdge(owner, TargetNode(addr), EdgeKind.Branch, $"If({varIdx})");
                        owner.Instructions.Add($"If {varIdx}");
                        break;
                    }
                // ── IfCmp NNA ───────────────────────────────────
                case 0x15:
                    {
                        int v1 = RU16(data, ref pos), v2 = RU16(data, ref pos), addr = RU16(data, ref pos);
                        AddEdge(owner, TargetNode(addr), EdgeKind.Branch, "IfCmp");
                        break;
                    }
                // ── EnterFight ──────────────────────────────────
                case 0x27:
                    {
                        for (int i = 0; i < 13; i++) RU16(data, ref pos);
                        int a1 = RU16(data, ref pos), a2 = RU16(data, ref pos);
                        AddEdge(owner, TargetNode(a1), EdgeKind.EnterFight, "战败");
                        AddEdge(owner, TargetNode(a2), EdgeKind.EnterFight, "战胜");
                        owner.Instructions.Add("EnterFight");
                        break;
                    }
                // ── AttribTest NNNAA ────────────────────────────
                case 0x3A:
                    {
                        RU16(data, ref pos); RU16(data, ref pos); RU16(data, ref pos);
                        AddEdge(owner, TargetNode(RU16(data, ref pos)), EdgeKind.Branch, "属性<");
                        AddEdge(owner, TargetNode(RU16(data, ref pos)), EdgeKind.Branch, "属性>");
                        owner.Instructions.Add("AttribTest");
                        break;
                    }
                // ── DisCmp NNAA ─────────────────────────────────
                case 0x43:
                    {
                        RU16(data, ref pos); RU16(data, ref pos);
                        AddEdge(owner, TargetNode(RU16(data, ref pos)), EdgeKind.Branch, "DisCmp-");
                        AddEdge(owner, TargetNode(RU16(data, ref pos)), EdgeKind.Branch, "DisCmp+");
                        break;
                    }
                // ── Choice CCA ──────────────────────────────────
                case 0x1F:
                    {
                        string s1 = ReadStr(data, ref pos, scriptEnd);
                        string s2 = ReadStr(data, ref pos, scriptEnd);
                        int addr = RU16(data, ref pos);
                        string b1 = s1.Length > 6 ? s1.Substring(0, 6) : s1;
                        AddEdge(owner, TargetNode(addr), EdgeKind.Branch, $"选:{b1}");
                        owner.Instructions.Add($"选择:{b1}/{(s2.Length > 6 ? s2.Substring(0, 6) : s2)}");
                        break;
                    }
                // ── TestGoodsNum NNNAA ──────────────────────────
                case 0x4D:
                    {
                        RU16(data, ref pos); RU16(data, ref pos); RU16(data, ref pos);
                        AddEdge(owner, TargetNode(RU16(data, ref pos)), EdgeKind.Branch, "物品<");
                        AddEdge(owner, TargetNode(RU16(data, ref pos)), EdgeKind.Branch, "物品≥");
                        break;
                    }
                // ── DeleteGoods/UseGoods NNA ─────────────────────
                case 0x30:
                case 0x39:
                    {
                        RU16(data, ref pos); RU16(data, ref pos);
                        AddEdge(owner, TargetNode(RU16(data, ref pos)), EdgeKind.Branch,
                                op == 0x30 ? "删物品" : "用物品");
                        break;
                    }
                // ── UseGoodsNum NNNA ────────────────────────────
                case 0x3E:
                    {
                        RU16(data, ref pos); RU16(data, ref pos); RU16(data, ref pos);
                        AddEdge(owner, TargetNode(RU16(data, ref pos)), EdgeKind.Branch, "用物品N");
                        break;
                    }
                // ── TestMoney LA ────────────────────────────────
                case 0x41:
                    {
                        SU32(data, ref pos);
                        AddEdge(owner, TargetNode(RU16(data, ref pos)), EdgeKind.Branch, "TestMoney");
                        break;
                    }
                // ── Randrade NA ─────────────────────────────────
                case 0x3F:
                    {
                        RU16(data, ref pos);
                        AddEdge(owner, TargetNode(RU16(data, ref pos)), EdgeKind.Branch, "随机");
                        break;
                    }
                // ── ActorEvent / Event NA ───────────────────────
                case 0x05:
                case 0x12:
                    { RU16(data, ref pos); RU16(data, ref pos); break; }
                // ── ShowGut NNC ─────────────────────────────────
                case 0x3D:
                    {
                        RU16(data, ref pos); RU16(data, ref pos); ReadStr(data, ref pos, scriptEnd);
                        owner.Instructions.Add("ShowGut"); break;
                    }
                // ── Menu/TimeMsg NC ─────────────────────────────
                case 0x40:
                    {
                        RU16(data, ref pos); string s = ReadStr(data, ref pos, scriptEnd);
                        owner.Instructions.Add($"菜单:{(s.Length > 10 ? s.Substring(0, 10) : s)}"); break;
                    }
                case 0x45:
                    { RU16(data, ref pos); ReadStr(data, ref pos, scriptEnd); break; }

                // ── 无参数 ──────────────────────────────────────
                case 0x09:
                case 0x14:
                case 0x24:
                case 0x25:
                case 0x2D:
                case 0x34:
                case 0x37:
                case 0x38:
                case 0x44:
                case 0x46:
                case 0x47:
                case 0x48:
                case 0x4A:
                case 0x4B: break;

                // ── N×1 ─────────────────────────────────────────
                case 0x03:
                case 0x0F:
                case 0x11:
                case 0x13:
                case 0x18:
                case 0x1A:
                case 0x1B:
                case 0x21:
                case 0x28:
                case 0x33:
                    SU16(data, ref pos); break;
                // ── N×2 ─────────────────────────────────────────
                case 0x00:
                case 0x08:
                case 0x0C:
                case 0x10:
                case 0x16:
                case 0x17:
                case 0x1D:
                case 0x22:
                case 0x2E:
                case 0x31:
                case 0x32:
                case 0x42:
                case 0x49:
                case 0x4C:
                    SU16(data, ref pos); SU16(data, ref pos); break;
                // ── N×3 ─────────────────────────────────────────
                case 0x02:
                case 0x06:
                case 0x2C:
                case 0x35:
                case 0x3B:
                case 0x3C:
                    SU16(data, ref pos); SU16(data, ref pos); SU16(data, ref pos); break;
                // ── N×4 ─────────────────────────────────────────
                case 0x01:
                case 0x20:
                case 0x26:
                    for (int i = 0; i < 4; i++) SU16(data, ref pos); break;
                // ── N×5 ─────────────────────────────────────────
                case 0x1E:
                    for (int i = 0; i < 5; i++) SU16(data, ref pos); break;
                // ── N×6 ─────────────────────────────────────────
                case 0x04:
                case 0x07:
                    for (int i = 0; i < 6; i++) SU16(data, ref pos); break;
                // ── N×11 ────────────────────────────────────────
                case 0x23:
                    for (int i = 0; i < 11; i++) SU16(data, ref pos); break;
                // ── L ───────────────────────────────────────────
                case 0x29:
                case 0x2A:
                case 0x2B:
                    SU32(data, ref pos); break;
                // ── Buy U ───────────────────────────────────────
                case 0x1C:
                    {
                        var items = new List<int>();
                        while (pos < scriptEnd && pos < data.Length)
                        {
                            if (data[pos] == 0x00)
                            {
                                pos += (pos + 1 < scriptEnd && data[pos + 1] == 0x00) ? 2 : 1;
                                break;
                            }
                            items.Add(RU16(data, ref pos));
                        }
                        owner.Instructions.Add($"Buy({items.Count}件)");
                        break;
                    }
                // ── Sale/NpcMoveMod ──────────────────────────────
                default:
                    pos = scriptEnd; // 未知opcode，停止
                    break;
            }
        }

        // 按指令数调整节点高度
        foreach (var n in _nodes.Values)
        {
            if (n.FilePath != path && n.FilePath != "") continue;
            int lines = _showInstructions ? Mathf.Min(n.Instructions.Count, 6) : 0;
            n.H = NODE_H + lines * 11f;
        }
    }

    void BuildAllEdges()
    {
        _edges.Clear();
        foreach (var node in _nodes.Values)
        {
            foreach (var edge in node.OutEdges)
            {
                // 目标节点占位
                if (!_nodes.ContainsKey(edge.TargetId))
                {
                    var p = edge.TargetId.Split('#');
                    var q = p[0].Split('-');
                    if (q.Length == 2 && int.TryParse(q[0], out int t) && int.TryParse(q[1], out int i))
                        _nodes[edge.TargetId] = new GutNode
                        { Id = edge.TargetId, FileType = t, FileIndex = i, SceneName = "?" };
                }
                _edges.Add(edge);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  布局
    // ══════════════════════════════════════════════════════════════

    void DoAutoLayout()
    {
        if (_nodes.Count == 0) return;

        // 分层：按 FileType 分列，FileIndex 分行
        var groups = _nodes.Values
            .GroupBy(n => n.FileType).OrderBy(g => g.Key).ToList();

        float colX = 0f;
        foreach (var grp in groups)
        {
            var byFile = grp.GroupBy(n => n.FileIndex).OrderBy(g => g.Key).ToList();
            float maxRowW = 0f;
            float rowY = 0f;
            foreach (var fg in byFile)
            {
                var ns = fg.OrderBy(n => n.SlotId).ToList();
                float rowH = ns.Max(n => n.H);
                float rowW = ns.Count * (NODE_W + 20f) - 20f;
                maxRowW = Mathf.Max(maxRowW, rowW);
                float nx = colX;
                foreach (var n in ns)
                {
                    if (!n.Pinned)
                        n.Pos = new Vector2(nx + n.W * 0.5f, rowY + n.H * 0.5f);
                    n.Velocity = Vector2.zero;
                    nx += NODE_W + 20f;
                }
                rowY += rowH + 32f;
            }
            colX += maxRowW + 60f;
        }

        // 居中整体
        float cx = _nodes.Values.Average(n => n.Pos.x);
        float cy = _nodes.Values.Average(n => n.Pos.y);
        foreach (var n in _nodes.Values)
            if (!n.Pinned) n.Pos -= new Vector2(cx, cy);

        // 重置力导向
        _simIter = 0;
        _simRunning = true;

        FitView();
        Repaint();
    }

    // ── 力导向（Fruchterman-Reingold 简化版，O(n²) 斥力 + 弹簧引力）
    void TickForce()
    {
        var nodeList = _nodes.Values.ToList();
        int n = nodeList.Count;
        if (n == 0) return;

        // 随迭代衰减温度
        float T = Mathf.Lerp(80f, 2f, (float)_simIter / SIM_MAX_ITER);

        // 斥力：F = k²/d
        float area = Mathf.Max(1f, n) * (NODE_W + 60f) * (NODE_H + 40f);
        float k = Mathf.Sqrt(area / n) * 1.2f;
        float k2 = k * k;

        var disp = new Vector2[n];

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                Vector2 delta = nodeList[i].Pos - nodeList[j].Pos;
                float dist2 = Mathf.Max(delta.sqrMagnitude, 0.01f);
                float dist = Mathf.Sqrt(dist2);
                Vector2 f = delta / dist * (k2 / dist);  // 斥力：k²/d
                if (!nodeList[i].Pinned) disp[i] += f;
                if (!nodeList[j].Pinned) disp[j] -= f;
            }
        }

        // 引力：F = d²/k（弹簧）
        foreach (var edge in _edges)
        {
            int si = nodeList.FindIndex(x => x.Id == edge.SourceId);
            int di = nodeList.FindIndex(x => x.Id == edge.TargetId);
            if (si < 0 || di < 0) continue;
            Vector2 delta = nodeList[di].Pos - nodeList[si].Pos;
            float dist = Mathf.Max(delta.magnitude, 0.01f);
            Vector2 f = delta / dist * (dist * dist / k);
            if (!nodeList[si].Pinned) disp[si] += f;
            if (!nodeList[di].Pinned) disp[di] -= f;
        }

        // 应用位移，限制在温度 T 以内
        for (int i = 0; i < n; i++)
        {
            if (nodeList[i].Pinned) { nodeList[i].Velocity = Vector2.zero; continue; }
            float mag = Mathf.Max(disp[i].magnitude, 0.001f);
            nodeList[i].Pos += disp[i] / mag * Mathf.Min(mag, T);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  工具函数
    // ══════════════════════════════════════════════════════════════

    static string NID(int ft, int fi, int slot) => $"{ft}-{fi}#{slot}";

    static int RU16(byte[] d, ref int p)
    {
        if (p + 2 > d.Length) return 0;
        int v = d[p] | (d[p + 1] << 8); p += 2; return v;
    }
    static void SU16(byte[] d, ref int p) { if (p + 2 <= d.Length) p += 2; }
    static void SU32(byte[] d, ref int p) { if (p + 4 <= d.Length) p += 4; }

    static string ReadStr(byte[] d, ref int p, int limit)
    {
        int s = p;
        while (p < limit && p < d.Length && d[p] != 0) p++;
        string r = "";
        try { r = _enc.GetString(d, s, p - s); } catch { r = "?"; }
        if (p < d.Length) p++;
        return r;
    }
}
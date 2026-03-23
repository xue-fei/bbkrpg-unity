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
/// 加载所有 .gut 文件，解析 StartChapter / GutEvent / Callback / LoadMap 等
/// 关键指令，自动构建并可视化跨章节的事件跳转关系图。
///
/// 节点 = 一个 .gut 文件中的一个 GutEvent 槽（事件入口块）
/// 边   = StartChapter(type,index) 跨文件跳转
///        GutEvent 槽内部的 Goto/If/EnterFight 分支
///
/// 操作：
///   鼠标拖拽       — 移动节点
///   鼠标中键拖拽   — 平移画布
///   滚轮           — 缩放画布
///   左键单击节点   — 选中，右侧面板显示详情
///   右键节点       — 菜单（固定/隔离/跳转到目标文件）
/// </summary>
public class GutFlowGraph : EditorWindow
{
    // ══════════════════════════════════════════════════════════════
    //  数据模型
    // ══════════════════════════════════════════════════════════════

    /// <summary>图中一个节点 = gut文件内的一个事件块</summary>
    private class GutNode
    {
        public string Id;           // "{type}-{index}#{slot}"，初始块用 "{type}-{index}#0"
        public int FileType;
        public int FileIndex;
        public int SlotId;          // 0 = 文件入口块（无槽），>0 = GutEvent 槽号
        public string SceneName;    // SetSceneName 读到的场景名
        public string FilePath;
        public List<string> Instructions = new List<string>(); // 块内关键指令摘要

        // 图布局
        public Rect Rect;
        public bool Pinned;
        public Vector2 Velocity;

        // 连接
        public List<GutEdge> OutEdges = new List<GutEdge>();
    }

    private class GutEdge
    {
        public string SourceId;
        public string TargetId;
        public EdgeKind Kind;
        public string Label;
    }

    private enum EdgeKind { Chapter, Goto, Branch, EnterFight }

    // ══════════════════════════════════════════════════════════════
    //  解析产物
    // ══════════════════════════════════════════════════════════════

    private Dictionary<string, GutNode> _nodes = new Dictionary<string, GutNode>();
    private List<GutEdge> _edges = new List<GutEdge>();
    private GutNode _selected;
    private string _statusMsg = "点击加载目录开始";
    private bool _loaded;

    // ══════════════════════════════════════════════════════════════
    //  画布状态
    // ══════════════════════════════════════════════════════════════

    private Vector2 _panOffset = Vector2.zero;
    private float _zoom = 1f;
    private bool _isPanning;
    private Vector2 _lastMousePos;
    private GutNode _dragging;
    private bool _layoutDirty;
    private double _lastLayoutTime;
    private string _filterText = "";
    private bool _showEdgeLabels = true;
    private bool _showInstructions = true;

    // 布局
    private const float NODE_W = 160f;
    private const float NODE_H = 72f;
    private const float LAYOUT_PADDING = 30f;

    // 颜色方案（暗色赛博朋克风）
    private static readonly Color BG_COLOR = new Color(0.08f, 0.09f, 0.12f);
    private static readonly Color GRID_COLOR = new Color(0.15f, 0.17f, 0.22f);
    private static readonly Color NODE_NORMAL = new Color(0.16f, 0.20f, 0.28f);
    private static readonly Color NODE_ENTRY = new Color(0.10f, 0.25f, 0.20f);
    private static readonly Color NODE_SELECTED = new Color(0.20f, 0.45f, 0.70f);
    private static readonly Color NODE_BORDER = new Color(0.30f, 0.55f, 0.90f);
    private static readonly Color NODE_BORDER_SEL = new Color(0.50f, 0.85f, 1.00f);
    private static readonly Color EDGE_CHAPTER = new Color(0.30f, 0.80f, 0.50f);
    private static readonly Color EDGE_GOTO = new Color(0.60f, 0.60f, 0.80f);
    private static readonly Color EDGE_BRANCH = new Color(0.90f, 0.65f, 0.20f);
    private static readonly Color EDGE_FIGHT = new Color(0.90f, 0.25f, 0.25f);
    private static readonly Color LABEL_COLOR = new Color(0.85f, 0.90f, 1.00f);
    private static readonly Color SUBLABEL_COLOR = new Color(0.55f, 0.65f, 0.80f);
    private static readonly Color PANEL_BG = new Color(0.10f, 0.12f, 0.16f);

    private static Encoding _gb2312;

    // ══════════════════════════════════════════════════════════════
    //  菜单入口
    // ══════════════════════════════════════════════════════════════

    [MenuItem("工具/GUT 流程关系图")]
    static void Open()
    {
        _gb2312 = _gb2312 ?? new CP936();
        var win = GetWindow<GutFlowGraph>("GUT 流程关系图");
        win.minSize = new Vector2(900, 600);
    }

    // ══════════════════════════════════════════════════════════════
    //  OnGUI 主入口
    // ══════════════════════════════════════════════════════════════

    private void OnGUI()
    {
        _gb2312 = _gb2312 ?? new CP936();
        DrawToolbar();

        float panelW = _selected != null ? 260f : 0f;
        Rect canvasRect = new Rect(0, EditorStyles.toolbar.fixedHeight + 2,
                                   position.width - panelW,
                                   position.height - EditorStyles.toolbar.fixedHeight - 2);

        HandleCanvasInput(canvasRect);
        DrawCanvas(canvasRect);

        if (_selected != null)
        {
            Rect panelRect = new Rect(position.width - panelW, EditorStyles.toolbar.fixedHeight + 2,
                                      panelW, position.height - EditorStyles.toolbar.fixedHeight - 2);
            DrawDetailPanel(panelRect);
        }

        // 状态栏
        Rect statusRect = new Rect(0, position.height - 18, position.width - panelW, 18);
        EditorGUI.DrawRect(statusRect, new Color(0.07f, 0.08f, 0.10f));
        GUI.color = SUBLABEL_COLOR;
        GUI.Label(new Rect(6, position.height - 17, position.width - panelW - 12, 16), _statusMsg, EditorStyles.miniLabel);
        GUI.color = Color.white;

        // 持续力导向布局迭代
        if (_layoutDirty && EditorApplication.timeSinceStartup - _lastLayoutTime > 0.016)
        {
            TickForceLayout();
            _lastLayoutTime = EditorApplication.timeSinceStartup;
            Repaint();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  工具栏
    // ══════════════════════════════════════════════════════════════

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        if (GUILayout.Button("📂 加载目录", EditorStyles.toolbarButton, GUILayout.Width(80)))
            LoadDirectory();

        GUILayout.Space(4);

        if (GUILayout.Button("⟳ 重置视图", EditorStyles.toolbarButton, GUILayout.Width(70)))
        {
            _panOffset = Vector2.zero;
            _zoom = 1f;
            Repaint();
        }

        if (GUILayout.Button("▦ 自动布局", EditorStyles.toolbarButton, GUILayout.Width(70)))
            AutoLayout();

        GUILayout.Space(4);

        _showEdgeLabels = GUILayout.Toggle(_showEdgeLabels, "连线标签", EditorStyles.toolbarButton, GUILayout.Width(60));
        _showInstructions = GUILayout.Toggle(_showInstructions, "指令摘要", EditorStyles.toolbarButton, GUILayout.Width(60));

        GUILayout.Space(8);
        GUILayout.Label("过滤:", GUILayout.Width(30));
        string newFilter = EditorGUILayout.TextField(_filterText, EditorStyles.toolbarTextField, GUILayout.Width(120));
        if (newFilter != _filterText) { _filterText = newFilter; Repaint(); }

        GUILayout.FlexibleSpace();

        if (_loaded)
        {
            GUI.color = SUBLABEL_COLOR;
            GUILayout.Label($"节点 {_nodes.Count}  边 {_edges.Count}  缩放 {_zoom:F2}×", EditorStyles.miniLabel, GUILayout.Width(180));
            GUI.color = Color.white;
        }

        EditorGUILayout.EndHorizontal();
    }

    // ══════════════════════════════════════════════════════════════
    //  画布输入处理
    // ══════════════════════════════════════════════════════════════

    private void HandleCanvasInput(Rect canvasRect)
    {
        Event e = Event.current;
        if (!canvasRect.Contains(e.mousePosition)) return;

        // 滚轮缩放
        if (e.type == EventType.ScrollWheel)
        {
            float delta = -e.delta.y * 0.05f;
            float newZoom = Mathf.Clamp(_zoom + delta, 0.2f, 3f);
            Vector2 mouseWorld = ScreenToWorld(e.mousePosition, canvasRect);
            _zoom = newZoom;
            // 保持鼠标指向的世界坐标不变
            Vector2 afterWorld = ScreenToWorld(e.mousePosition, canvasRect);
            _panOffset += (afterWorld - mouseWorld) * _zoom;
            e.Use();
            Repaint();
        }

        // 中键平移
        if (e.type == EventType.MouseDown && e.button == 2)
        {
            _isPanning = true;
            _lastMousePos = e.mousePosition;
            e.Use();
        }
        if (e.type == EventType.MouseDrag && _isPanning)
        {
            _panOffset += e.mousePosition - _lastMousePos;
            _lastMousePos = e.mousePosition;
            e.Use();
            Repaint();
        }
        if (e.type == EventType.MouseUp && e.button == 2)
        {
            _isPanning = false;
            e.Use();
        }

        // 节点拖拽 & 选择
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            _dragging = null;
            GutNode hit = HitTest(e.mousePosition, canvasRect);
            if (hit != null)
            {
                _dragging = hit;
                _selected = hit;
                e.Use();
                Repaint();
            }
            else
            {
                // 点击空白取消选择
                _selected = null;
                e.Use();
                Repaint();
            }
        }
        if (e.type == EventType.MouseDrag && _dragging != null && e.button == 0)
        {
            _dragging.Rect.position += e.delta / _zoom;
            _dragging.Pinned = true;
            _layoutDirty = false; // 手动移动后停止力布局
            e.Use();
            Repaint();
        }
        if (e.type == EventType.MouseUp && e.button == 0)
            _dragging = null;

        // 右键菜单
        if (e.type == EventType.ContextClick)
        {
            GutNode hit = HitTest(e.mousePosition, canvasRect);
            if (hit != null)
            {
                ShowNodeContextMenu(hit);
                e.Use();
            }
        }
    }

    private GutNode HitTest(Vector2 screenPos, Rect canvasRect)
    {
        Vector2 world = ScreenToWorld(screenPos, canvasRect);
        foreach (var node in _nodes.Values)
        {
            if (!IsNodeVisible(node)) continue;
            if (node.Rect.Contains(world)) return node;
        }
        return null;
    }

    private Vector2 ScreenToWorld(Vector2 screen, Rect canvasRect)
    {
        Vector2 center = canvasRect.center;
        return (screen - center - _panOffset) / _zoom;
    }

    private Vector2 WorldToScreen(Vector2 world, Rect canvasRect)
    {
        Vector2 center = canvasRect.center;
        return world * _zoom + _panOffset + center;
    }

    private bool IsNodeVisible(GutNode node)
    {
        if (string.IsNullOrEmpty(_filterText)) return true;
        string lower = _filterText.ToLower();
        return node.Id.ToLower().Contains(lower) ||
               node.SceneName.ToLower().Contains(lower) ||
               node.Instructions.Any(s => s.ToLower().Contains(lower));
    }

    // ══════════════════════════════════════════════════════════════
    //  画布绘制
    // ══════════════════════════════════════════════════════════════

    private void DrawCanvas(Rect canvasRect)
    {
        // 背景
        EditorGUI.DrawRect(canvasRect, BG_COLOR);

        // 网格
        DrawGrid(canvasRect);

        GUI.BeginClip(canvasRect);

        Matrix4x4 oldMatrix = GUI.matrix;
        Vector2 center = canvasRect.size * 0.5f;
        GUI.matrix = Matrix4x4.TRS(center + _panOffset, Quaternion.identity, Vector3.one * _zoom);

        // 先画边
        if (Event.current.type == EventType.Repaint)
        {
            foreach (var edge in _edges)
                DrawEdge(edge, canvasRect);
        }

        // 再画节点
        foreach (var node in _nodes.Values)
        {
            if (!IsNodeVisible(node)) continue;
            DrawNode(node);
        }

        GUI.matrix = oldMatrix;
        GUI.EndClip();

        // 无节点提示
        if (!_loaded)
        {
            string hint = "点击工具栏📂 加载目录，选择包含 .gut 文件的文件夹";
            var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                fontSize = 13,
                normal = { textColor = SUBLABEL_COLOR }
            };
            GUI.Label(canvasRect, hint, style);
        }
    }

    private void DrawGrid(Rect canvasRect)
    {
        if (Event.current.type != EventType.Repaint) return;

        float gridStep = 40f * _zoom;
        float ox = (_panOffset.x + canvasRect.x) % gridStep;
        float oy = (_panOffset.y + canvasRect.y) % gridStep;

        Handles.color = GRID_COLOR;
        for (float x = canvasRect.x + ox; x < canvasRect.xMax; x += gridStep)
            Handles.DrawLine(new Vector3(x, canvasRect.y), new Vector3(x, canvasRect.yMax));
        for (float y = canvasRect.y + oy; y < canvasRect.yMax; y += gridStep)
            Handles.DrawLine(new Vector3(canvasRect.x, y), new Vector3(canvasRect.xMax, y));
    }

    private void DrawNode(GutNode node)
    {
        bool isSel = node == _selected;
        bool isEntry = node.SlotId == 0;

        // 阴影
        var shadowRect = new Rect(node.Rect.x + 3, node.Rect.y + 3, node.Rect.width, node.Rect.height);
        EditorGUI.DrawRect(shadowRect, new Color(0, 0, 0, 0.45f));

        // 主体
        Color bg = isSel ? NODE_SELECTED : (isEntry ? NODE_ENTRY : NODE_NORMAL);
        EditorGUI.DrawRect(node.Rect, bg);

        // 边框
        Color border = isSel ? NODE_BORDER_SEL : NODE_BORDER;
        DrawBorder(node.Rect, border, isSel ? 2f : 1f);

        // 顶部色条
        float barH = 4f;
        Color barColor = isEntry ? new Color(0.20f, 0.80f, 0.55f) : new Color(0.30f, 0.55f, 0.90f);
        EditorGUI.DrawRect(new Rect(node.Rect.x, node.Rect.y, node.Rect.width, barH), barColor);

        // 文字
        float tx = node.Rect.x + 6;
        float ty = node.Rect.y + barH + 2;
        float tw = node.Rect.width - 12;

        // 节点ID
        var idStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = LABEL_COLOR },
            fontStyle = FontStyle.Bold,
            fontSize = 10,
            clipping = TextClipping.Clip
        };
        string idText = isEntry ? $"[{node.FileType}-{node.FileIndex}] 入口" : $"[{node.FileType}-{node.FileIndex}] 槽{node.SlotId}";
        GUI.Label(new Rect(tx, ty, tw, 14), idText, idStyle);
        ty += 14;

        // 场景名
        if (!string.IsNullOrEmpty(node.SceneName))
        {
            var nameStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.95f, 0.85f, 0.40f) },
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                clipping = TextClipping.Clip
            };
            string displayName = node.SceneName.Length > 12 ? node.SceneName.Substring(0, 12) + "…" : node.SceneName;
            GUI.Label(new Rect(tx, ty, tw, 16), displayName, nameStyle);
            ty += 16;
        }

        // 指令摘要
        if (_showInstructions && node.Instructions.Count > 0)
        {
            var instrStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = SUBLABEL_COLOR },
                fontSize = 9,
                clipping = TextClipping.Clip
            };
            int maxLines = (int)((node.Rect.height - (ty - node.Rect.y) - 4) / 11);
            for (int i = 0; i < Math.Min(maxLines, node.Instructions.Count); i++)
            {
                GUI.Label(new Rect(tx, ty, tw, 11), node.Instructions[i], instrStyle);
                ty += 11;
            }
        }

        // 固定标记
        if (node.Pinned)
        {
            GUI.color = new Color(1f, 0.7f, 0.2f, 0.8f);
            GUI.Label(new Rect(node.Rect.xMax - 14, node.Rect.y + barH + 2, 12, 12), "📌", EditorStyles.miniLabel);
            GUI.color = Color.white;
        }
    }

    private void DrawEdge(GutEdge edge, Rect canvasRect)
    {
        if (!_nodes.TryGetValue(edge.SourceId, out GutNode src)) return;
        if (!_nodes.TryGetValue(edge.TargetId, out GutNode dst)) return;
        if (!IsNodeVisible(src) || !IsNodeVisible(dst)) return;

        // 自环跳过
        if (edge.SourceId == edge.TargetId) return;

        Color c = edge.Kind switch
        {
            EdgeKind.Chapter => EDGE_CHAPTER,
            EdgeKind.Goto => EDGE_GOTO,
            EdgeKind.Branch => EDGE_BRANCH,
            EdgeKind.EnterFight => EDGE_FIGHT,
            _ => EDGE_GOTO
        };

        Vector2 from = new Vector2(src.Rect.xMax, src.Rect.center.y);
        Vector2 to = new Vector2(dst.Rect.xMin, dst.Rect.center.y);

        // 如果目标在左侧，使用弧线
        if (dst.Rect.xMin < src.Rect.xMax)
        {
            from = new Vector2(src.Rect.center.x, src.Rect.yMax);
            to = new Vector2(dst.Rect.center.x, dst.Rect.yMin);
        }

        float dist = Vector2.Distance(from, to);
        float tangentLen = Mathf.Clamp(dist * 0.5f, 50f, 200f);
        Vector3 t1 = new Vector3(from.x + tangentLen, from.y, 0);
        Vector3 t2 = new Vector3(to.x - tangentLen, to.y, 0);

        Handles.DrawBezier(
            new Vector3(from.x, from.y, 0),
            new Vector3(to.x, to.y, 0),
            t1, t2, c, null, edge == GetHoveredEdge() ? 2.5f : 1.5f
        );

        // 箭头
        DrawArrow(to, (to - new Vector2(t2.x, t2.y)).normalized, c, 6f);

        // 边标签
        if (_showEdgeLabels && !string.IsNullOrEmpty(edge.Label))
        {
            Vector2 mid = BezierPoint(
                new Vector2(from.x, from.y), t1, t2, new Vector2(to.x, to.y), 0.5f);
            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = c },
                fontSize = 8,
                alignment = TextAnchor.MiddleCenter
            };
            GUI.Label(new Rect(mid.x - 30, mid.y - 8, 60, 16), edge.Label, labelStyle);
        }
    }

    private void DrawArrow(Vector2 tip, Vector2 dir, Color color, float size)
    {
        if (dir == Vector2.zero) return;
        dir = dir.normalized;
        Vector2 right = new Vector2(-dir.y, dir.x);
        Vector3[] pts = {
            tip,
            tip - dir * size + right * size * 0.5f,
            tip - dir * size - right * size * 0.5f
        };
        Handles.color = color;
        Handles.DrawAAConvexPolygon(pts);
    }

    private void DrawBorder(Rect r, Color color, float thickness)
    {
        Handles.color = color;
        Handles.DrawSolidRectangleWithOutline(r, Color.clear, color);
    }

    private Vector2 BezierPoint(Vector2 p0, Vector3 p1, Vector3 p2, Vector2 p3, float t)
    {
        float u = 1 - t;
        return u * u * u * p0
            + 3 * u * u * t * new Vector2(p1.x, p1.y)
            + 3 * u * t * t * new Vector2(p2.x, p2.y)
            + t * t * t * p3;
    }

    private GutEdge GetHoveredEdge() => null; // 简化版，不实现悬停高亮

    // ══════════════════════════════════════════════════════════════
    //  详情面板
    // ══════════════════════════════════════════════════════════════

    private Vector2 _panelScroll;

    private void DrawDetailPanel(Rect panelRect)
    {
        EditorGUI.DrawRect(panelRect, PANEL_BG);
        // 左侧分割线
        EditorGUI.DrawRect(new Rect(panelRect.x, panelRect.y, 1, panelRect.height),
                           new Color(0.25f, 0.40f, 0.65f));

        if (_selected == null) return;

        GUILayout.BeginArea(new Rect(panelRect.x + 8, panelRect.y + 8,
                                     panelRect.width - 16, panelRect.height - 16));

        var titleStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            normal = { textColor = LABEL_COLOR },
            fontSize = 12
        };
        var subStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = SUBLABEL_COLOR }
        };
        var valueStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = new Color(0.85f, 0.95f, 1f) },
            wordWrap = true
        };

        GUILayout.Label("节点详情", titleStyle);
        GUILayout.Space(4);

        void Row(string k, string v)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(k, subStyle, GUILayout.Width(70));
            GUILayout.Label(v, valueStyle);
            GUILayout.EndHorizontal();
        }

        Row("ID", _selected.Id);
        Row("文件", $"{_selected.FileType}-{_selected.FileIndex}");
        Row("槽号", _selected.SlotId == 0 ? "入口块" : _selected.SlotId.ToString());
        if (!string.IsNullOrEmpty(_selected.SceneName))
            Row("场景名", _selected.SceneName);
        Row("文件路径", Path.GetFileName(_selected.FilePath));

        GUILayout.Space(6);
        GUILayout.Label("─── 指令摘要 ───", subStyle);
        _panelScroll = GUILayout.BeginScrollView(_panelScroll, GUILayout.Height(140));
        foreach (var inst in _selected.Instructions)
            GUILayout.Label(inst, valueStyle);
        GUILayout.EndScrollView();

        GUILayout.Space(6);
        GUILayout.Label("─── 出边 ───", subStyle);
        foreach (var edge in _selected.OutEdges)
        {
            Color eColor = edge.Kind switch
            {
                EdgeKind.Chapter => EDGE_CHAPTER,
                EdgeKind.Goto => EDGE_GOTO,
                EdgeKind.Branch => EDGE_BRANCH,
                EdgeKind.EnterFight => EDGE_FIGHT,
                _ => EDGE_GOTO
            };
            GUI.color = eColor;
            string kindLabel = edge.Kind switch
            {
                EdgeKind.Chapter => "→场景",
                EdgeKind.Goto => "→跳转",
                EdgeKind.Branch => "→分支",
                EdgeKind.EnterFight => "→战斗",
                _ => "→"
            };
            if (GUILayout.Button($"{kindLabel} {edge.TargetId}", EditorStyles.miniButton))
            {
                if (_nodes.TryGetValue(edge.TargetId, out GutNode target))
                {
                    _selected = target;
                    // 将目标节点居中显示
                    _panOffset = -target.Rect.center * _zoom;
                    Repaint();
                }
            }
            GUI.color = Color.white;
        }

        GUILayout.Space(4);
        if (GUILayout.Button("📂 在脚本查看器中打开", EditorStyles.miniButton))
        {
            // 复用 GutEditor 打开同一文件
            if (File.Exists(_selected.FilePath))
            {
                // 触发GutEditor加载（如果已存在）
                var editor = GetWindow<GutEditor>("GUT 脚本查看器");
                editor.Show();
            }
        }

        GUILayout.EndArea();
    }

    // ══════════════════════════════════════════════════════════════
    //  右键上下文菜单
    // ══════════════════════════════════════════════════════════════

    private void ShowNodeContextMenu(GutNode node)
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent(node.Pinned ? "取消固定" : "固定位置"), false, () =>
        {
            node.Pinned = !node.Pinned;
            Repaint();
        });
        menu.AddItem(new GUIContent("居中视图到此节点"), false, () =>
        {
            _panOffset = -node.Rect.center * _zoom;
            Repaint();
        });
        menu.AddSeparator("");
        menu.AddItem(new GUIContent("隔离显示（仅显示关联节点）"), false, () =>
        {
            _filterText = node.Id.Split('#')[0]; // 过滤同文件
            Repaint();
        });
        menu.AddItem(new GUIContent("清除过滤"), false, () =>
        {
            _filterText = "";
            Repaint();
        });
        menu.ShowAsContext();
    }

    // ══════════════════════════════════════════════════════════════
    //  加载 & 解析
    // ══════════════════════════════════════════════════════════════

    private void LoadDirectory()
    {
        string defaultDir = Application.dataPath + "/../ExRes/gut";
        if (!Directory.Exists(defaultDir)) defaultDir = Application.dataPath;

        string dir = EditorUtility.OpenFolderPanel("选择 .gut 文件目录", defaultDir, "");
        if (string.IsNullOrEmpty(dir)) return;

        _nodes.Clear();
        _edges.Clear();
        _selected = null;
        _loaded = false;

        string[] gutFiles = Directory.GetFiles(dir, "*.gut");
        if (gutFiles.Length == 0)
        {
            _statusMsg = $"目录 {dir} 中没有找到 .gut 文件";
            return;
        }

        int ok = 0, fail = 0;
        foreach (string path in gutFiles)
        {
            try
            {
                ParseGutFile(path);
                ok++;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GutFlowGraph] 解析失败 {Path.GetFileName(path)}: {ex.Message}");
                fail++;
            }
        }

        // 构建跨文件边（StartChapter 指令）
        BuildCrossFileEdges();

        // 初始布局
        AutoLayout();

        _loaded = true;
        _layoutDirty = true;
        _lastLayoutTime = EditorApplication.timeSinceStartup;
        _statusMsg = $"已加载 {ok} 个文件，{_nodes.Count} 个节点，{_edges.Count} 条边" +
                     (fail > 0 ? $"（{fail} 个解析失败）" : "");
        Repaint();
    }

    /// <summary>解析单个 .gut 文件，生成若干 GutNode</summary>
    private void ParseGutFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 0x1B) return;

        int fileType = data[0];
        int fileIndex = data[1];
        int length = data[0x18] | (data[0x19] << 8);
        int numEvt = data[0x1A];

        int scriptStart = 0x1B + numEvt * 2;
        int scriptDataOffset = 2 + 1 + numEvt * 2;
        int scriptBytesLen = length - 2 - 1 - numEvt * 2;
        if (scriptBytesLen < 0) scriptBytesLen = 0;
        int scriptEnd = Math.Min(scriptStart + scriptBytesLen, data.Length);

        // 读跳转表
        int[] jumpTable = new int[numEvt];
        for (int i = 0; i < numEvt; i++)
        {
            int off = 0x1B + i * 2;
            jumpTable[i] = data[off] | (data[off + 1] << 8);
        }

        // 入口节点（slot=0）
        string entryId = NodeId(fileType, fileIndex, 0);
        var entryNode = new GutNode
        {
            Id = entryId,
            FileType = fileType,
            FileIndex = fileIndex,
            SlotId = 0,
            FilePath = path
        };
        _nodes[entryId] = entryNode;

        // 为每个有效 GutEvent 槽创建节点
        var slotNodes = new Dictionary<int, GutNode>(); // scriptOffset → node
        for (int i = 0; i < numEvt; i++)
        {
            if (jumpTable[i] == 0) continue;
            int scriptOff = jumpTable[i] - scriptDataOffset;
            if (scriptOff < 0 || scriptOff >= scriptEnd - scriptStart) continue;

            int slotId = i + 1;
            string nid = NodeId(fileType, fileIndex, slotId);
            var node = new GutNode
            {
                Id = nid,
                FileType = fileType,
                FileIndex = fileIndex,
                SlotId = slotId,
                FilePath = path
            };
            _nodes[nid] = node;
            slotNodes[scriptOff] = node;
        }

        // 解析脚本流，填充每个节点的指令摘要和出边
        // 按脚本偏移分配到对应槽节点
        ParseScriptBlocks(data, scriptStart, scriptEnd, scriptDataOffset,
                          fileType, fileIndex, slotNodes, entryNode, path);
    }

    /// <summary>
    /// 扫描脚本字节流，将指令分配到对应槽节点，提取关键信息
    /// </summary>
    private void ParseScriptBlocks(byte[] data, int scriptStart, int scriptEnd,
        int scriptDataOffset, int fileType, int fileIndex,
        Dictionary<int, GutNode> slotNodes, GutNode entryNode, string filePath)
    {
        // 构建 scriptOffset → 所属节点 的映射（用于确定指令属于哪个块）
        // 策略：指令落在哪个槽入口之后（且在下一个槽入口之前），就属于该槽
        var sortedSlots = slotNodes.Keys.OrderBy(k => k).ToList();

        GutNode NodeForOffset(int scriptOff)
        {
            // 找最后一个 <= scriptOff 的槽
            GutNode owner = entryNode;
            foreach (int slotOff in sortedSlots)
            {
                if (slotOff <= scriptOff) owner = slotNodes[slotOff];
                else break;
            }
            return owner;
        }

        int pos = scriptStart;
        string currentSceneName = "";

        while (pos < scriptEnd && pos < data.Length)
        {
            int scriptOff = pos - scriptStart;
            GutNode owner = NodeForOffset(scriptOff);

            byte op = data[pos]; pos++;

            switch (op)
            {
                case 0x36: // SetSceneName C
                    {
                        string s = ReadGB2312String(data, ref pos, scriptEnd);
                        owner.SceneName = s;
                        owner.Instructions.Add($"场景: {s}");
                        break;
                    }
                case 0x0D: // Say NC
                    {
                        int actor = ReadU16(data, ref pos);
                        string txt = ReadGB2312String(data, ref pos, scriptEnd);
                        string brief = txt.Length > 14 ? txt.Substring(0, 14) + "…" : txt;
                        owner.Instructions.Add($"说话[{actor}]: {brief}");
                        break;
                    }
                case 0x2F: // Message C
                    {
                        string txt = ReadGB2312String(data, ref pos, scriptEnd);
                        string brief = txt.Length > 16 ? txt.Substring(0, 16) + "…" : txt;
                        owner.Instructions.Add($"消息: {brief}");
                        break;
                    }
                case 0x0E: // StartChapter NN
                    {
                        int t = ReadU16(data, ref pos);
                        int idx = ReadU16(data, ref pos);
                        owner.Instructions.Add($"StartChapter {t}-{idx}");
                        // 跨文件边稍后在 BuildCrossFileEdges 中统一处理
                        // 此处记录到出边列表中供后续使用
                        string targetId = NodeId(t, idx, 0);
                        AddEdgeIfNotDuplicate(owner.Id, targetId, EdgeKind.Chapter, $"{t}-{idx}", owner);
                        break;
                    }
                case 0x0A: // Goto A
                    {
                        int addr = ReadU16(data, ref pos);
                        int tOff = addr - scriptDataOffset;
                        string targetId = FindSlotNodeId(fileType, fileIndex, tOff, slotNodes, entryNode);
                        AddEdgeIfNotDuplicate(owner.Id, targetId, EdgeKind.Goto, "Goto", owner);
                        owner.Instructions.Add($"Goto {tOff}");
                        break;
                    }
                case 0x0B: // If NA
                    {
                        int varIdx = ReadU16(data, ref pos);
                        int addr = ReadU16(data, ref pos);
                        int tOff = addr - scriptDataOffset;
                        string targetId = FindSlotNodeId(fileType, fileIndex, tOff, slotNodes, entryNode);
                        AddEdgeIfNotDuplicate(owner.Id, targetId, EdgeKind.Branch, $"If({varIdx})", owner);
                        owner.Instructions.Add($"If {varIdx} → {tOff}");
                        break;
                    }
                case 0x15: // IfCmp NNA
                    {
                        int v1 = ReadU16(data, ref pos);
                        int v2 = ReadU16(data, ref pos);
                        int addr = ReadU16(data, ref pos);
                        int tOff = addr - scriptDataOffset;
                        string targetId = FindSlotNodeId(fileType, fileIndex, tOff, slotNodes, entryNode);
                        AddEdgeIfNotDuplicate(owner.Id, targetId, EdgeKind.Branch, $"IfCmp", owner);
                        owner.Instructions.Add($"IfCmp {v1} {v2}");
                        break;
                    }
                case 0x27: // EnterFight NNNNNNNNNNNNNAA
                    {
                        for (int i = 0; i < 13; i++) ReadU16(data, ref pos);
                        int a1 = ReadU16(data, ref pos); // 失败跳转
                        int a2 = ReadU16(data, ref pos); // 胜利跳转
                        int t1Off = a1 - scriptDataOffset;
                        int t2Off = a2 - scriptDataOffset;
                        string tid1 = FindSlotNodeId(fileType, fileIndex, t1Off, slotNodes, entryNode);
                        string tid2 = FindSlotNodeId(fileType, fileIndex, t2Off, slotNodes, entryNode);
                        AddEdgeIfNotDuplicate(owner.Id, tid1, EdgeKind.EnterFight, "战败", owner);
                        AddEdgeIfNotDuplicate(owner.Id, tid2, EdgeKind.EnterFight, "战胜", owner);
                        owner.Instructions.Add("EnterFight");
                        break;
                    }
                case 0x3A: // AttribTest NNNAA
                    {
                        ReadU16(data, ref pos); ReadU16(data, ref pos); ReadU16(data, ref pos);
                        int a1 = ReadU16(data, ref pos);
                        int a2 = ReadU16(data, ref pos);
                        string tid1 = FindSlotNodeId(fileType, fileIndex, a1 - scriptDataOffset, slotNodes, entryNode);
                        string tid2 = FindSlotNodeId(fileType, fileIndex, a2 - scriptDataOffset, slotNodes, entryNode);
                        AddEdgeIfNotDuplicate(owner.Id, tid1, EdgeKind.Branch, "属性<", owner);
                        AddEdgeIfNotDuplicate(owner.Id, tid2, EdgeKind.Branch, "属性>", owner);
                        owner.Instructions.Add("AttribTest");
                        break;
                    }
                case 0x43: // DisCmp NNAA
                    {
                        ReadU16(data, ref pos); ReadU16(data, ref pos);
                        int a1 = ReadU16(data, ref pos);
                        int a2 = ReadU16(data, ref pos);
                        string tid1 = FindSlotNodeId(fileType, fileIndex, a1 - scriptDataOffset, slotNodes, entryNode);
                        string tid2 = FindSlotNodeId(fileType, fileIndex, a2 - scriptDataOffset, slotNodes, entryNode);
                        AddEdgeIfNotDuplicate(owner.Id, tid1, EdgeKind.Branch, "DisCmp<", owner);
                        AddEdgeIfNotDuplicate(owner.Id, tid2, EdgeKind.Branch, "DisCmp>", owner);
                        break;
                    }
                case 0x1F: // Choice CCA
                    {
                        string s1 = ReadGB2312String(data, ref pos, scriptEnd);
                        string s2 = ReadGB2312String(data, ref pos, scriptEnd);
                        int addr = ReadU16(data, ref pos);
                        int tOff = addr - scriptDataOffset;
                        string targetId = FindSlotNodeId(fileType, fileIndex, tOff, slotNodes, entryNode);
                        AddEdgeIfNotDuplicate(owner.Id, targetId, EdgeKind.Branch, $"选{s1}", owner);
                        string brief1 = s1.Length > 6 ? s1.Substring(0, 6) : s1;
                        string brief2 = s2.Length > 6 ? s2.Substring(0, 6) : s2;
                        owner.Instructions.Add($"选择: {brief1}/{brief2}");
                        break;
                    }
                case 0x3D: // ShowGut NNC
                    {
                        ReadU16(data, ref pos); ReadU16(data, ref pos);
                        ReadGB2312String(data, ref pos, scriptEnd);
                        owner.Instructions.Add("ShowGut");
                        break;
                    }
                case 0x40: // Menu NC
                    {
                        ReadU16(data, ref pos);
                        string s = ReadGB2312String(data, ref pos, scriptEnd);
                        owner.Instructions.Add($"菜单: {(s.Length > 10 ? s.Substring(0, 10) : s)}");
                        break;
                    }
                case 0x45: // TimeMsg NC
                    {
                        ReadU16(data, ref pos);
                        ReadGB2312String(data, ref pos, scriptEnd);
                        break;
                    }
                case 0x30: // DeleteGoods NNA
                case 0x39: // UseGoods NNA
                    {
                        ReadU16(data, ref pos); ReadU16(data, ref pos);
                        int addr = ReadU16(data, ref pos);
                        int tOff = addr - scriptDataOffset;
                        string targetId = FindSlotNodeId(fileType, fileIndex, tOff, slotNodes, entryNode);
                        AddEdgeIfNotDuplicate(owner.Id, targetId, EdgeKind.Branch,
                            op == 0x30 ? "删物品" : "用物品", owner);
                        break;
                    }
                case 0x3E: // UseGoodsNum NNNA
                    {
                        ReadU16(data, ref pos); ReadU16(data, ref pos); ReadU16(data, ref pos);
                        int addr = ReadU16(data, ref pos);
                        int tOff = addr - scriptDataOffset;
                        string targetId = FindSlotNodeId(fileType, fileIndex, tOff, slotNodes, entryNode);
                        AddEdgeIfNotDuplicate(owner.Id, targetId, EdgeKind.Branch, "用物品数", owner);
                        break;
                    }
                case 0x41: // TestMoney LA
                    {
                        SkipU32(data, ref pos);
                        int addr = ReadU16(data, ref pos);
                        int tOff = addr - scriptDataOffset;
                        string targetId = FindSlotNodeId(fileType, fileIndex, tOff, slotNodes, entryNode);
                        AddEdgeIfNotDuplicate(owner.Id, targetId, EdgeKind.Branch, "TestMoney", owner);
                        break;
                    }
                case 0x4D: // TestGoodsNum NNNAA
                    {
                        ReadU16(data, ref pos); ReadU16(data, ref pos); ReadU16(data, ref pos);
                        int a1 = ReadU16(data, ref pos);
                        int a2 = ReadU16(data, ref pos);
                        string tid1 = FindSlotNodeId(fileType, fileIndex, a1 - scriptDataOffset, slotNodes, entryNode);
                        string tid2 = FindSlotNodeId(fileType, fileIndex, a2 - scriptDataOffset, slotNodes, entryNode);
                        AddEdgeIfNotDuplicate(owner.Id, tid1, EdgeKind.Branch, "TestGoods<", owner);
                        AddEdgeIfNotDuplicate(owner.Id, tid2, EdgeKind.Branch, "TestGoods>", owner);
                        break;
                    }
                // ── 跳过固定长度指令 ──────────────────────────
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
                case 0x4B:
                    break; // 无参数

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
                    SkipU16(data, ref pos); break;

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
                    SkipU16(data, ref pos); SkipU16(data, ref pos); break;

                case 0x02:
                case 0x06:
                case 0x2C:
                case 0x35:
                case 0x3B:
                case 0x3C:
                    SkipU16(data, ref pos); SkipU16(data, ref pos); SkipU16(data, ref pos); break;

                case 0x01:
                case 0x20:
                case 0x26:
                    SkipU16(data, ref pos); SkipU16(data, ref pos);
                    SkipU16(data, ref pos); SkipU16(data, ref pos); break;

                case 0x1E:
                    for (int i = 0; i < 5; i++) SkipU16(data, ref pos); break;

                case 0x04:
                case 0x07:
                    for (int i = 0; i < 6; i++) SkipU16(data, ref pos); break;

                case 0x23:
                    for (int i = 0; i < 11; i++) SkipU16(data, ref pos); break;

                case 0x05:
                case 0x12:
                case 0x3F:
                    SkipU16(data, ref pos); SkipU16(data, ref pos); break;

                case 0x29:
                case 0x2A:
                case 0x2B:
                    SkipU32(data, ref pos); break;

                case 0x1C: // Buy U
                    while (pos < scriptEnd && pos < data.Length)
                    {
                        if (data[pos] == 0x00)
                        {
                            if (pos + 1 < scriptEnd && data[pos + 1] == 0x00)
                                pos += 2;
                            else
                                pos += 1;
                            break;
                        }
                        pos += 2;
                    }
                    break;

                default:
                    // 未知指令，停止解析该文件剩余内容以防错位
                    pos = scriptEnd;
                    break;
            }
        }
    }

    /// <summary>构建 StartChapter 产生的跨文件边（目标节点此时已全部创建）</summary>
    private void BuildCrossFileEdges()
    {
        // StartChapter 边已在 ParseScriptBlocks 中通过 AddEdgeIfNotDuplicate 添加到 owner.OutEdges
        // 这里只需把所有出边同步到全局 _edges 列表
        _edges.Clear();
        foreach (var node in _nodes.Values)
        {
            foreach (var edge in node.OutEdges)
            {
                // 目标节点若不存在（跨文件且文件未加载），创建占位节点
                if (!_nodes.ContainsKey(edge.TargetId))
                {
                    var parts = edge.TargetId.Split('#');
                    var sub = parts[0].Split('-');
                    if (sub.Length == 2 && int.TryParse(sub[0], out int t) && int.TryParse(sub[1], out int idx))
                    {
                        var placeholder = new GutNode
                        {
                            Id = edge.TargetId,
                            FileType = t,
                            FileIndex = idx,
                            SlotId = 0,
                            SceneName = "?",
                            FilePath = ""
                        };
                        _nodes[edge.TargetId] = placeholder;
                    }
                }
                _edges.Add(edge);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  布局算法
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 按 FileType 分层，同 FileType 内按 FileIndex 排列，
    /// 入口块在左，槽节点在右
    /// </summary>
    private void AutoLayout()
    {
        // 按 FileType 分组
        var groups = _nodes.Values
            .GroupBy(n => n.FileType)
            .OrderBy(g => g.Key)
            .ToList();

        float groupX = -(groups.Count * (NODE_W + LAYOUT_PADDING * 4)) * 0.5f;
        foreach (var grp in groups)
        {
            var filesInGroup = grp.GroupBy(n => n.FileIndex).OrderBy(g => g.Key).ToList();
            float fileY = -(filesInGroup.Count * (NODE_H + LAYOUT_PADDING) * 1.5f) * 0.5f;

            foreach (var fileGrp in filesInGroup)
            {
                var nodesInFile = fileGrp.OrderBy(n => n.SlotId).ToList();
                float slotX = groupX;
                for (int i = 0; i < nodesInFile.Count; i++)
                {
                    var node = nodesInFile[i];
                    if (!node.Pinned)
                    {
                        // 计算节点高度（含指令摘要）
                        float h = NODE_H + (node.Instructions.Count > 0 ? node.Instructions.Count * 11f : 0);
                        h = Mathf.Min(h, 140f);
                        node.Rect = new Rect(slotX + i * (NODE_W + LAYOUT_PADDING),
                                             fileY,
                                             NODE_W,
                                             h);
                    }
                }
                fileY += NODE_H + LAYOUT_PADDING * 2.5f + (nodesInFile.Max(n => n.Instructions.Count) * 11f);
            }
            groupX += (NODE_W + LAYOUT_PADDING) * 5;
        }

        _layoutDirty = true;
        _lastLayoutTime = EditorApplication.timeSinceStartup;
        Repaint();
    }

    /// <summary>力导向布局一帧迭代（斥力+引力）</summary>
    private void TickForceLayout()
    {
        var nodeList = _nodes.Values.Where(IsNodeVisible).ToList();
        float k = 120f; // 理想边长
        float repulsion = k * k;
        float damping = 0.85f;
        float dt = 0.08f;
        float maxDisp = 20f;

        // 初始化速度
        foreach (var n in nodeList)
            if (!n.Pinned) { /* velocity already set */ }

        // 斥力
        for (int i = 0; i < nodeList.Count; i++)
        {
            for (int j = i + 1; j < nodeList.Count; j++)
            {
                var ni = nodeList[i];
                var nj = nodeList[j];
                Vector2 d = ni.Rect.center - nj.Rect.center;
                float dist = Mathf.Max(d.magnitude, 0.1f);
                Vector2 force = d.normalized * (repulsion / dist);

                if (!ni.Pinned) ni.Velocity += force * dt;
                if (!nj.Pinned) nj.Velocity -= force * dt;
            }
        }

        // 引力（有边的节点相互吸引）
        float edgeSpring = 0.01f;
        foreach (var edge in _edges)
        {
            if (!_nodes.TryGetValue(edge.SourceId, out GutNode src)) continue;
            if (!_nodes.TryGetValue(edge.TargetId, out GutNode dst)) continue;
            Vector2 d = dst.Rect.center - src.Rect.center;
            float dist = d.magnitude;
            Vector2 force = d.normalized * (dist - k) * edgeSpring;
            if (!src.Pinned) src.Velocity += force * dt;
            if (!dst.Pinned) dst.Velocity -= force * dt;
        }

        // 更新位置
        float totalKE = 0f;
        foreach (var n in nodeList)
        {
            if (n.Pinned) continue;
            n.Velocity *= damping;
            Vector2 disp = n.Velocity * dt;
            float mag = disp.magnitude;
            if (mag > maxDisp) disp = disp.normalized * maxDisp;
            n.Rect.position += disp;
            totalKE += n.Velocity.sqrMagnitude;
        }

        // 动能很小时停止迭代
        if (totalKE < 0.5f * nodeList.Count)
            _layoutDirty = false;
    }

    // ══════════════════════════════════════════════════════════════
    //  工具函数
    // ══════════════════════════════════════════════════════════════

    private static string NodeId(int fileType, int fileIndex, int slot)
        => $"{fileType}-{fileIndex}#{slot}";

    private string FindSlotNodeId(int fileType, int fileIndex, int scriptOff,
        Dictionary<int, GutNode> slotNodes, GutNode entryNode)
    {
        if (slotNodes.TryGetValue(scriptOff, out GutNode sn)) return sn.Id;
        return entryNode.Id; // 回退到入口节点
    }

    private void AddEdgeIfNotDuplicate(string srcId, string dstId, EdgeKind kind, string label, GutNode owner)
    {
        if (srcId == dstId) return; // 自环不加
        // 同类型同目标的边去重
        if (owner.OutEdges.Any(e => e.TargetId == dstId && e.Kind == kind)) return;
        var edge = new GutEdge { SourceId = srcId, TargetId = dstId, Kind = kind, Label = label };
        owner.OutEdges.Add(edge);
    }

    private static int ReadU16(byte[] data, ref int pos)
    {
        if (pos + 2 > data.Length) return 0;
        int v = data[pos] | (data[pos + 1] << 8);
        pos += 2;
        return v;
    }

    private static void SkipU16(byte[] data, ref int pos)
    {
        if (pos + 2 <= data.Length) pos += 2;
    }

    private static void SkipU32(byte[] data, ref int pos)
    {
        if (pos + 4 <= data.Length) pos += 4;
    }

    private static string ReadGB2312String(byte[] data, ref int pos, int limit)
    {
        int start = pos;
        while (pos < limit && pos < data.Length && data[pos] != 0x00) pos++;
        string s;
        try { s = _gb2312.GetString(data, start, pos - start); }
        catch { s = "???"; }
        if (pos < data.Length) pos++; // skip \0
        return s;
    }
}
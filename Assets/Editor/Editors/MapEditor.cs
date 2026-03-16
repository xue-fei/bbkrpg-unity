using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 地图资源查看器  MapEditor.cs
///
/// ── .map 文件格式（ResMap.SetData）──────────────────────────────
///   +0x00        Type       1字节
///   +0x01        Index      1字节
///   +0x02        TilIndex   1字节  对应 til 文件的 index（1-N.til）
///   +0x03~+0x0F  MapName    13字节，GB2312，\0结尾，0xCC填充
///   +0x10        MapWidth   1字节（地图格子列数）
///   +0x11        MapHeight  1字节（地图格子行数）
///   +0x12        _data      MapWidth × MapHeight × 2 字节
///                低字节：bit7=可行走，bit6~0=tile索引(0~126)
///                高字节：事件号(0=无事件)
///
/// ── .til 文件格式（ResImage，TIL）───────────────────────────────
///   +0x00  Type   1字节
///   +0x01  Index  1字节
///   +0x02  Width  1字节（固定=16）
///   +0x03  Height 1字节（固定=16）
///   +0x04  Number 1字节（固定=127，切片数即 tile 种数）
///   +0x05  Mode   1字节（固定=1，不透明1bpp）
///   +0x06  PixelData   127 × 2 × 16 = 4064 字节
///
/// ── Tile 像素解码（Mode=1，ResImage.CreateBitmaps 不透明分支）──
///   每像素1bit，高位先行，0=白 1=黑，行末不足1字节丢弃
///   每个 tile = 16×16 = 32字节
///
/// ── 地图渲染（对应 ResMap.DrawWholeMap）─────────────────────────
///   对每个格子 (tx, ty)：
///     tileIdx = data[(ty*W+tx)*2] & 0x7F
///     在 (tx*16, ty*16) 处绘制对应 tile 图片
///     若事件号非0，叠加显示事件号文字
/// </summary>
public class MapEditor : EditorWindow
{
    // ══════════════════════════════════════════════════════
    //  解析结果
    // ══════════════════════════════════════════════════════
    private string _mapPath;
    private string _statusMsg;

    // 地图元数据
    private int _mapResType, _mapResIndex;
    private int _tilIndex;
    private string _mapName;
    private int _mapWidth, _mapHeight;
    // 地图格子原始数据（每格2字节：低=tile信息，高=事件号）
    private byte[] _mapData;

    // 已解码的 127 个 tile（每个 Color[16×16]，图像坐标左上原点）
    private Color[][] _tiles;       // _tiles[tileIdx][像素]
    private Texture2D _tileTex;     // 预览用：所有tile拼成一行

    // ══════════════════════════════════════════════════════
    //  显示选项
    // ══════════════════════════════════════════════════════
    private bool _showWalkable = true;   // 叠加可行走高亮
    private bool _showEvents = true;   // 叠加事件号
    private bool _showGrid = false;  // 叠加网格线
    private float _zoom = 2f;
    private Vector2 _scroll;

    // 地图 Texture2D（完整渲染结果）
    private Texture2D _mapTex;

    // ══════════════════════════════════════════════════════
    //  常量
    // ══════════════════════════════════════════════════════
    private const int TILE_W = 16;
    private const int TILE_H = 16;
    private const int TILE_COUNT = 127;

    // 叠加色
    private static readonly Color WalkColor = new Color(0f, 1f, 0f, 0.25f); // 绿色半透明
    private static readonly Color BlockColor = new Color(1f, 0f, 0f, 0.18f); // 红色半透明
    private static readonly Color GridColor = new Color(0.5f, 0.5f, 1f, 0.35f); // 蓝色半透明
    private static readonly Color EventColor = new Color(1f, 1f, 0f, 1f);    // 黄色
    private static readonly Color BlackColor = Color.black;
    private static readonly Color WhiteColor = Color.white;

    // ══════════════════════════════════════════════════════
    //  菜单
    // ══════════════════════════════════════════════════════
    [MenuItem("工具/地图查看器")]
    static void Init() => GetWindow<MapEditor>("地图查看器");

    // ══════════════════════════════════════════════════════
    //  OnGUI
    // ══════════════════════════════════════════════════════
    private void OnGUI()
    {
        DrawToolbar();
        DrawInfoBar();

        if (!string.IsNullOrEmpty(_statusMsg))
        {
            EditorGUILayout.HelpBox(_statusMsg, MessageType.Warning);
        }

        if (_mapTex != null)
            DrawMapCanvas();
        else if (_mapData == null)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("← 点击打开.map加载地图文件", EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
        }
    }

    // ── 工具栏 ────────────────────────────────────────────
    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        if (GUILayout.Button("打开 .map", EditorStyles.toolbarButton, GUILayout.Width(80)))
            OpenMapFile();

        GUILayout.Space(6);

        EditorGUI.BeginDisabledGroup(_mapData == null);

        // 叠加选项
        bool w2 = GUILayout.Toggle(_showWalkable, "行走", EditorStyles.toolbarButton, GUILayout.Width(40));
        bool e2 = GUILayout.Toggle(_showEvents, "事件", EditorStyles.toolbarButton, GUILayout.Width(40));
        bool g2 = GUILayout.Toggle(_showGrid, "网格", EditorStyles.toolbarButton, GUILayout.Width(40));
        if (w2 != _showWalkable || e2 != _showEvents || g2 != _showGrid)
        {
            _showWalkable = w2; _showEvents = e2; _showGrid = g2;
            RebuildMapTex();
        }

        EditorGUI.EndDisabledGroup();

        GUILayout.FlexibleSpace();

        GUILayout.Label("缩放", GUILayout.Width(30));
        _zoom = EditorGUILayout.Slider(_zoom, 0.5f, 6f, GUILayout.Width(120));

        EditorGUILayout.EndHorizontal();
    }

    // ── 信息栏 ────────────────────────────────────────────
    private void DrawInfoBar()
    {
        if (_mapData == null) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("文件", Path.GetFileName(_mapPath));
        EditorGUILayout.LabelField(
            $"Type={_mapResType}  Index={_mapResIndex}  " +
            $"名称={_mapName}  TilIndex={_tilIndex}  " +
            $"地图={_mapWidth}×{_mapHeight} 格  " +
            $"像素={_mapWidth * TILE_W}×{_mapHeight * TILE_H}");
        EditorGUILayout.EndVertical();
    }

    // ── 地图画布 ──────────────────────────────────────────
    private void DrawMapCanvas()
    {
        float dispW = _mapTex.width * _zoom;
        float dispH = _mapTex.height * _zoom;

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        Rect r = GUILayoutUtility.GetRect(dispW, dispH);
        EditorGUI.DrawPreviewTexture(r, _mapTex, null, ScaleMode.ScaleToFit);

        // 鼠标悬停时显示格子信息
        if (Event.current.type == EventType.MouseMove && r.Contains(Event.current.mousePosition))
        {
            Vector2 local = Event.current.mousePosition - r.position;
            int tx = Mathf.FloorToInt(local.x / (_zoom * TILE_W));
            int ty = Mathf.FloorToInt(local.y / (_zoom * TILE_H));
            if (tx >= 0 && tx < _mapWidth && ty >= 0 && ty < _mapHeight)
            {
                int di = (ty * _mapWidth + tx) * 2;
                int tileIdx = _mapData[di] & 0x7F;
                bool canWalk = (_mapData[di] & 0x80) != 0;
                int eventN = _mapData[di + 1] & 0xFF;
                string tip = $"格子({tx},{ty})  tile={tileIdx}  {(canWalk ? "可走" : "阻挡")}" +
                               (eventN != 0 ? $"  事件={eventN}" : "");
                GUI.Label(new Rect(r.x + 4, r.yMax - 18, 400, 18), tip, EditorStyles.miniLabel);
            }
            Repaint();
        }

        EditorGUILayout.EndScrollView();
    }

    // ══════════════════════════════════════════════════════
    //  文件打开
    // ══════════════════════════════════════════════════════
    private void OpenMapFile()
    {
        string path = EditorUtility.OpenFilePanelWithFilters(
            "选择地图文件", "ExRes/map",
            new[] { "地图文件", "map", "所有文件", "*" }
        );
        if (string.IsNullOrEmpty(path)) return;

        ClearAll();
        _mapPath = path;
        _statusMsg = null;

        byte[] buf;
        try { buf = File.ReadAllBytes(path); }
        catch (System.Exception e) { _statusMsg = $"读取失败：{e.Message}"; Repaint(); return; }

        if (!ParseMap(buf)) { Repaint(); return; }

        // 自动从同目录 ../til/1-{tilIndex}.til 加载 tile 图集
        string tilDir = Path.Combine(Path.GetDirectoryName(path), "..", "til");
        string tilPath = Path.Combine(tilDir, $"1-{_tilIndex}.til");
        tilPath = Path.GetFullPath(tilPath);

        if (!File.Exists(tilPath))
        {
            _statusMsg = $"未找到 tile 文件：{tilPath}\n请手动选择";
            // 弹出手动选择
            tilPath = EditorUtility.OpenFilePanelWithFilters(
                $"选择 til 文件（TilIndex={_tilIndex}）", "ExRes/til",
                new[] { "TIL 文件", "til", "所有文件", "*" }
            );
            if (string.IsNullOrEmpty(tilPath)) { Repaint(); return; }
            _statusMsg = null;
        }

        byte[] tilBuf;
        try { tilBuf = File.ReadAllBytes(tilPath); }
        catch (System.Exception e) { _statusMsg = $"tile 读取失败：{e.Message}"; Repaint(); return; }

        if (!ParseTil(tilBuf)) { Repaint(); return; }

        RebuildMapTex();
        Repaint();
    }

    // ══════════════════════════════════════════════════════
    //  解析 .map
    // ══════════════════════════════════════════════════════
    private bool ParseMap(byte[] buf)
    {
        if (buf.Length < 0x12)
        {
            _statusMsg = $"文件过小（{buf.Length} 字节）"; return false;
        }

        _mapResType = buf[0];
        _mapResIndex = buf[1];
        _tilIndex = buf[2];

        // MapName：+0x03 起，GB2312，\0结尾
        int nameEnd = 3;
        while (nameEnd < 0x10 && buf[nameEnd] != 0) nameEnd++;
        try
        {
            var enc = System.Text.Encoding.GetEncoding("gb2312");
            _mapName = enc.GetString(buf, 3, nameEnd - 3);
        }
        catch { _mapName = "?"; }

        _mapWidth = buf[0x10];
        _mapHeight = buf[0x11];

        if (_mapWidth == 0 || _mapHeight == 0)
        {
            _statusMsg = $"地图尺寸无效（W={_mapWidth} H={_mapHeight}）"; return false;
        }

        int dataLen = _mapWidth * _mapHeight * 2;
        if (buf.Length < 0x12 + dataLen)
        {
            _statusMsg = $"地图数据长度不足（需{0x12 + dataLen}，实{buf.Length}）"; return false;
        }

        _mapData = new byte[dataLen];
        System.Array.Copy(buf, 0x12, _mapData, 0, dataLen);
        return true;
    }

    // ══════════════════════════════════════════════════════
    //  解析 .til（ResImage，Mode=1 不透明1bpp）
    // ══════════════════════════════════════════════════════
    private bool ParseTil(byte[] buf)
    {
        if (buf.Length < 6) { _statusMsg = $"til 文件过小"; return false; }

        int iw = buf[2] & 0xFF;  // 16
        int ih = buf[3] & 0xFF;  // 16
        int num = buf[4] & 0xFF;  // 127
        int mode = buf[5] & 0xFF;  // 1

        if (iw != TILE_W || ih != TILE_H)
        {
            _statusMsg = $"tile 尺寸非预期（{iw}×{ih}，期望16×16）"; return false;
        }
        if (mode != 1 && mode != 2)
        {
            _statusMsg = $"未知 mode={mode}"; return false;
        }

        int rowBytes = (iw + 7) / 8;           // =2
        int tileBytes = rowBytes * ih * mode;   // =32（mode=1）
        int dataLen = num * tileBytes;

        if (buf.Length < 6 + dataLen)
        {
            _statusMsg = $"til 数据长度不足（需{6 + dataLen}，实{buf.Length}）"; return false;
        }

        _tiles = new Color[num][];
        int iData = 0;
        byte[] pixData = new byte[dataLen];
        System.Array.Copy(buf, 6, pixData, 0, dataLen);

        if (mode == 1)
        {
            // 不透明 1bpp：逐 tile 解码
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
                _tiles[t] = px;
            }
        }
        else
        {
            // 透明 2bpp
            Color transp = new Color(1f, 0f, 1f, 1f);
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
                _tiles[t] = px;
            }
        }

        return true;
    }

    // ══════════════════════════════════════════════════════
    //  渲染完整地图 → _mapTex
    //  对应 ResMap.DrawWholeMap：逐格绘制 tile，叠加事件号/行走标记
    // ══════════════════════════════════════════════════════
    private void RebuildMapTex()
    {
        if (_mapData == null || _tiles == null) return;

        int pixW = _mapWidth * TILE_W;
        int pixH = _mapHeight * TILE_H;

        // canvas 使用图像坐标（左上原点），最后统一翻转给 Unity
        Color[] canvas = new Color[pixW * pixH];

        for (int ty = 0; ty < _mapHeight; ty++)
        {
            for (int tx = 0; tx < _mapWidth; tx++)
            {
                int di = (ty * _mapWidth + tx) * 2;
                int lo = _mapData[di] & 0xFF;
                int hi = _mapData[di + 1] & 0xFF;
                int tileIdx = lo & 0x7F;
                bool canW = (lo & 0x80) != 0;
                int evtN = hi;

                // 基础 tile（图像坐标逐行写入 canvas）
                Color[] tpx = tileIdx < _tiles.Length ? _tiles[tileIdx] : null;

                int baseX = tx * TILE_W;
                int baseY = ty * TILE_H;

                for (int py = 0; py < TILE_H; py++)
                    for (int px2 = 0; px2 < TILE_W; px2++)
                    {
                        int ci = (baseY + py) * pixW + (baseX + px2);

                        // tile 本体
                        Color c = tpx != null ? tpx[py * TILE_W + px2] : WhiteColor;

                        // 叠加行走标记（半透明混合）
                        if (_showWalkable)
                        {
                            Color overlay = canW ? WalkColor : BlockColor;
                            c = AlphaBlend(c, overlay);
                        }

                        canvas[ci] = c;
                    }

                // 叠加网格线（tile 边框）
                if (_showGrid)
                {
                    DrawGridBorder(canvas, pixW, pixH, baseX, baseY, TILE_W, TILE_H);
                }

                // 叠加事件号（用黑底黄字的小数字绘制）
                if (_showEvents && evtN != 0)
                {
                    DrawEventNumber(canvas, pixW, pixH, baseX, baseY, evtN);
                }
            }
        }

        // 翻转整张 canvas（图像坐标 → Unity 左下原点）
        Color[] flipped = new Color[pixW * pixH];
        for (int y = 0; y < pixH; y++)
            System.Array.Copy(canvas, y * pixW, flipped, (pixH - 1 - y) * pixW, pixW);

        if (_mapTex != null && (_mapTex.width != pixW || _mapTex.height != pixH))
        {
            DestroyImmediate(_mapTex); _mapTex = null;
        }
        if (_mapTex == null)
        {
            _mapTex = new Texture2D(pixW, pixH, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
        }
        _mapTex.SetPixels(flipped);
        _mapTex.Apply();
    }

    // ── 半透明叠加混合（src over dst 的简化版）────────────
    private static Color AlphaBlend(Color base_c, Color overlay)
    {
        float a = overlay.a;
        return new Color(
            base_c.r * (1f - a) + overlay.r * a,
            base_c.g * (1f - a) + overlay.g * a,
            base_c.b * (1f - a) + overlay.b * a,
            1f);
    }

    // ── 网格线（tile 四边）────────────────────────────────
    private static void DrawGridBorder(Color[] canvas, int cw, int ch,
        int ox, int oy, int w, int h)
    {
        for (int px = 0; px < w; px++)
        {
            SetPx(canvas, cw, ch, ox + px, oy, GridColor);
            SetPx(canvas, cw, ch, ox + px, oy + h - 1, GridColor);
        }
        for (int py = 0; py < h; py++)
        {
            SetPx(canvas, cw, ch, ox, oy + py, GridColor);
            SetPx(canvas, cw, ch, ox + w - 1, oy + py, GridColor);
        }
    }

    private static void SetPx(Color[] canvas, int cw, int ch, int x, int y, Color c)
    {
        if (x < 0 || y < 0 || x >= cw || y >= ch) return;
        canvas[y * cw + x] = AlphaBlend(canvas[y * cw + x], c);
    }

    // ── 事件号：用 3×5 像素字体绘制 1~2 位数字 ───────────
    // 每个数字 3宽×5高，字模 0~9
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

    private static void DrawEventNumber(Color[] canvas, int cw, int ch,
        int ox, int oy, int num)
    {
        // 最多显示2位数字，从格子左上角偏移1像素绘制
        string s = num.ToString();
        int drawX = ox + 1;
        int drawY = oy + 1;

        foreach (char ch2 in s)
        {
            int d = ch2 - '0';
            if (d < 0 || d > 9) continue;
            for (int row = 0; row < 5; row++)
                for (int col = 0; col < 3; col++)
                {
                    if ((_font3x5[d, row] & (0b100 >> col)) != 0)
                        SetPx(canvas, cw, ch, drawX + col, drawY + row, EventColor);
                }
            drawX += 4; // 字符间距
        }
    }

    // ══════════════════════════════════════════════════════
    //  内存清理
    // ══════════════════════════════════════════════════════
    private void ClearAll()
    {
        _mapData = null;
        _tiles = null;
        _statusMsg = null;
        if (_mapTex != null) { DestroyImmediate(_mapTex); _mapTex = null; }
        if (_tileTex != null) { DestroyImmediate(_tileTex); _tileTex = null; }
    }

    private void OnDestroy() => ClearAll();
}
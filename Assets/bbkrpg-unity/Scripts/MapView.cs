using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 运行时地图视图
///
/// 挂载到一个带有 RawImage 子对象的 Canvas GameObject 上。
/// 调用 LoadMap(mapPath, tilPath) 或 LoadMap(mapPath) 即可渲染地图。
///
/// 层级结构示例：
///   Canvas
///   └─ MapViewRoot            ← 本脚本挂载处
///      └─ MapRawImage         ← 拖入 mapRawImage 字段（RawImage 组件）
///
/// .map / .til 文件格式与 MapEditor.cs 中注释完全一致。
/// </summary>
public class MapView : MonoBehaviour
{
    // ══════════════════════════════════════════════════════════════
    //  Inspector 字段
    // ══════════════════════════════════════════════════════════════

    [Header("UI 引用")]
    [Tooltip("用于显示地图的 RawImage（Canvas 下子对象）")]
    public RawImage mapRawImage;

    [Header("文件路径（可在 Inspector 中直接指定或运行时调用 LoadMap）")]
    [Tooltip(".map 文件的绝对路径或相对于 StreamingAssets 的路径")]
    public string mapFilePath = "";

    [Tooltip(".til 文件路径；留空则按规则自动推断（../til/1-{tilIndex}.til）")]
    public string tilFilePath = "";

    [Header("叠加显示")]
    public bool showWalkable = false;
    public bool showEvents = false;
    public bool showGrid = false;

    [Header("缩放（RawImage 像素尺寸 = 地图像素 × scale）")]
    [Range(0.5f, 8f)]
    public float pixelScale = 2f;

    // ══════════════════════════════════════════════════════════════
    //  常量
    // ══════════════════════════════════════════════════════════════

    private const int TILE_W = 16;
    private const int TILE_H = 16;
    private const int TILE_COUNT = 127;

    private static readonly Color WalkColor = new Color(0f, 1f, 0f, 0.25f);
    private static readonly Color BlockColor = new Color(1f, 0f, 0f, 0.18f);
    private static readonly Color GridColor = new Color(0.5f, 0.5f, 1f, 0.35f);
    private static readonly Color EventColor = new Color(1f, 1f, 0f, 1f);
    private static readonly Color BlackColor = Color.black;
    private static readonly Color WhiteColor = Color.white;

    // ══════════════════════════════════════════════════════════════
    //  解析结果（私有状态）
    // ══════════════════════════════════════════════════════════════

    // .map 元数据
    private int _mapResType, _mapResIndex;
    private int _tilIndex;
    private string _mapName;
    private int _mapWidth, _mapHeight;
    private byte[] _mapData;          // 每格 2 字节：低=tile信息，高=事件号

    // .til 解码结果
    private Color[][] _tiles;         // _tiles[tileIdx][像素 row-major，左上原点]

    // 渲染结果
    private Texture2D _mapTexture;

    // ══════════════════════════════════════════════════════════════
    //  公开属性（只读元数据）
    // ══════════════════════════════════════════════════════════════

    public string MapName => _mapName;
    public int MapWidth => _mapWidth;
    public int MapHeight => _mapHeight;
    public bool IsLoaded => _mapData != null && _tiles != null;

    // ══════════════════════════════════════════════════════════════
    //  Unity 生命周期
    // ══════════════════════════════════════════════════════════════

    private void Start()
    {
        mapFilePath = Application.dataPath + "/../ExRes/map/1-1-三清宫.map";
        if (!string.IsNullOrEmpty(mapFilePath))
            LoadMap(mapFilePath, tilFilePath);
    }

    private void OnDestroy()
    {
        if (_mapTexture != null)
            Destroy(_mapTexture);
    }

    // ══════════════════════════════════════════════════════════════
    //  公开 API
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 加载地图。tilPath 为空时自动推断 .til 路径。
    /// </summary>
    public bool LoadMap(string mapPath, string tilPath = "")
    {
        ClearState();

        // ── 解析路径 ─────────────────────────────────────────────
        mapPath = ResolvePath(mapPath);
        if (!File.Exists(mapPath))
        {
            Debug.LogError($"[MapView] .map 文件不存在：{mapPath}");
            return false;
        }

        // ── 解析 .map ────────────────────────────────────────────
        byte[] mapBuf;
        try { mapBuf = File.ReadAllBytes(mapPath); }
        catch (Exception e) { Debug.LogError($"[MapView] 读取 .map 失败：{e.Message}"); return false; }

        if (!ParseMap(mapBuf)) return false;

        // ── 推断 .til 路径 ───────────────────────────────────────
        if (string.IsNullOrEmpty(tilPath))
        {
            string tilDir = Path.Combine(Path.GetDirectoryName(mapPath)!, "..", "til");
            tilPath = Path.GetFullPath(Path.Combine(tilDir, $"1-{_tilIndex}.til"));
        }
        else
        {
            tilPath = ResolvePath(tilPath);
        }

        if (!File.Exists(tilPath))
        {
            Debug.LogError($"[MapView] .til 文件不存在：{tilPath}");
            return false;
        }

        // ── 解析 .til ────────────────────────────────────────────
        byte[] tilBuf;
        try { tilBuf = File.ReadAllBytes(tilPath); }
        catch (Exception e) { Debug.LogError($"[MapView] 读取 .til 失败：{e.Message}"); return false; }

        if (!ParseTil(tilBuf)) return false;

        // ── 渲染 & 显示 ──────────────────────────────────────────
        RebuildTexture();
        ApplyToRawImage();

        Debug.Log($"[MapView] 地图加载完成：{_mapName}  {_mapWidth}×{_mapHeight} 格  {_mapWidth * TILE_W}×{_mapHeight * TILE_H} px");
        return true;
    }

    /// <summary>
    /// 切换叠加显示后重建纹理（无需重新解析文件）。
    /// </summary>
    public void RefreshOverlay(bool walkable, bool events, bool grid)
    {
        showWalkable = walkable;
        showEvents = events;
        showGrid = grid;
        if (IsLoaded)
        {
            RebuildTexture();
            ApplyToRawImage();
        }
    }

    /// <summary>
    /// 查询指定格子信息（x, y 为地图格坐标）。
    /// </summary>
    public bool TryGetTileInfo(int x, int y, out int tileIndex, out bool canWalk, out int eventNum)
    {
        tileIndex = 0; canWalk = false; eventNum = 0;
        if (_mapData == null || x < 0 || x >= _mapWidth || y < 0 || y >= _mapHeight)
            return false;

        int di = (y * _mapWidth + x) * 2;
        tileIndex = _mapData[di] & 0x7F;
        canWalk = (_mapData[di] & 0x80) != 0;
        eventNum = _mapData[di + 1] & 0xFF;
        return true;
    }

    // ══════════════════════════════════════════════════════════════
    //  解析 .map
    // ══════════════════════════════════════════════════════════════

    private bool ParseMap(byte[] buf)
    {
        if (buf.Length < 0x12)
        {
            Debug.LogError($"[MapView] .map 文件过小（{buf.Length} 字节，最小需 18）");
            return false;
        }

        _mapResType = buf[0];
        _mapResIndex = buf[1];
        _tilIndex = buf[2];

        // MapName：+0x03 起，GB2312，\0 结尾，最长到 +0x0F
        int nameEnd = 3;
        while (nameEnd < 0x10 && buf[nameEnd] != 0) nameEnd++;
        try
        {
            var enc = Encoding.GetEncoding("gb2312");
            _mapName = enc.GetString(buf, 3, nameEnd - 3);
        }
        catch { _mapName = "(unknown)"; }

        _mapWidth = buf[0x10];
        _mapHeight = buf[0x11];

        if (_mapWidth == 0 || _mapHeight == 0)
        {
            Debug.LogError($"[MapView] 地图尺寸无效（W={_mapWidth} H={_mapHeight}）");
            return false;
        }

        int dataLen = _mapWidth * _mapHeight * 2;
        if (buf.Length < 0x12 + dataLen)
        {
            Debug.LogError($"[MapView] 地图数据不足（需 {0x12 + dataLen} 字节，实 {buf.Length}）");
            return false;
        }

        _mapData = new byte[dataLen];
        Array.Copy(buf, 0x12, _mapData, 0, dataLen);
        return true;
    }

    // ══════════════════════════════════════════════════════════════
    //  解析 .til（Mode=1 不透明1bpp / Mode=2 透明2bpp）
    // ══════════════════════════════════════════════════════════════

    private bool ParseTil(byte[] buf)
    {
        if (buf.Length < 6)
        {
            Debug.LogError("[MapView] .til 文件过小");
            return false;
        }

        int iw = buf[2] & 0xFF;   // 期望 16
        int ih = buf[3] & 0xFF;   // 期望 16
        int num = buf[4] & 0xFF;   // 期望 127
        int mode = buf[5] & 0xFF;   // 1 = 不透明1bpp，2 = 透明2bpp

        if (iw != TILE_W || ih != TILE_H)
        {
            Debug.LogError($"[MapView] tile 尺寸非预期（{iw}×{ih}，期望 16×16）");
            return false;
        }
        if (mode != 1 && mode != 2)
        {
            Debug.LogError($"[MapView] 未知 mode={mode}");
            return false;
        }

        int rowBytes = (iw + 7) / 8;           // = 2
        int tileBytes = rowBytes * ih * mode;    // mode=1 → 32，mode=2 → 64
        int dataLen = num * tileBytes;

        if (buf.Length < 6 + dataLen)
        {
            Debug.LogError($"[MapView] .til 数据不足（需 {6 + dataLen}，实 {buf.Length}）");
            return false;
        }

        _tiles = new Color[num][];
        byte[] pixData = new byte[dataLen];
        Array.Copy(buf, 6, pixData, 0, dataLen);

        int iData = 0;

        if (mode == 1)
        {
            // 不透明 1bpp：0=白 1=黑，高位先行，行末不足字节丢弃
            for (int t = 0; t < num; t++)
            {
                Color[] px = new Color[iw * ih];
                int iOff = 0, bitCnt = 0;
                for (int y = 0; y < ih; y++)
                {
                    for (int x = 0; x < iw; x++)
                    {
                        px[iOff++] = ((pixData[iData] << bitCnt) & 0x80) != 0
                            ? BlackColor : WhiteColor;
                        if (++bitCnt >= 8) { bitCnt = 0; ++iData; }
                    }
                    if (bitCnt != 0) { bitCnt = 0; ++iData; } // 行末对齐
                }
                _tiles[t] = px;
            }
        }
        else
        {
            // 透明 2bpp：高位=透明标志，低位=颜色（0=白，1=黑）
            Color transpColor = new Color(1f, 0f, 1f, 0f); // 品红透明
            for (int t = 0; t < num; t++)
            {
                Color[] px = new Color[iw * ih];
                int iOff = 0, bitCnt = 0;
                for (int y = 0; y < ih; y++)
                {
                    for (int x = 0; x < iw; x++)
                    {
                        bool isTransp = ((pixData[iData] << bitCnt) & 0x80) != 0;
                        bool isDark = ((pixData[iData] << bitCnt << 1) & 0x80) != 0;
                        px[iOff++] = isTransp ? transpColor : (isDark ? BlackColor : WhiteColor);
                        bitCnt += 2;
                        if (bitCnt >= 8) { bitCnt = 0; ++iData; }
                    }
                    // 行末双字节对齐
                    if (bitCnt > 0 && bitCnt <= 7) { bitCnt = 0; ++iData; }
                    if (iData % 2 != 0) ++iData;
                }
                _tiles[t] = px;
            }
        }

        return true;
    }

    // ══════════════════════════════════════════════════════════════
    //  渲染地图 → _mapTexture
    //  对应 ResMap.DrawWholeMap：逐格绘制 tile，可叠加网格/行走/事件
    // ══════════════════════════════════════════════════════════════

    private void RebuildTexture()
    {
        if (_mapData == null || _tiles == null) return;

        int pixW = _mapWidth * TILE_W;
        int pixH = _mapHeight * TILE_H;

        // canvas 使用图像坐标（左上原点），最后统一翻转给 Unity（左下原点）
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

                Color[] tpx = (tileIdx < _tiles.Length) ? _tiles[tileIdx] : null;

                int baseX = tx * TILE_W;
                int baseY = ty * TILE_H;

                // ── 逐像素填充 tile ──────────────────────────────
                for (int py = 0; py < TILE_H; py++)
                {
                    for (int px = 0; px < TILE_W; px++)
                    {
                        int ci = (baseY + py) * pixW + (baseX + px);
                        Color c = tpx != null ? tpx[py * TILE_W + px] : WhiteColor;

                        if (showWalkable)
                            c = AlphaBlend(c, canW ? WalkColor : BlockColor);

                        canvas[ci] = c;
                    }
                }

                // ── 网格线 ───────────────────────────────────────
                if (showGrid)
                    DrawGridBorder(canvas, pixW, pixH, baseX, baseY, TILE_W, TILE_H);

                // ── 事件号 ───────────────────────────────────────
                if (showEvents && evtN != 0)
                    DrawEventNumber(canvas, pixW, pixH, baseX, baseY, evtN);
            }
        }

        // 图像坐标 → Unity 左下原点：垂直翻转
        Color[] flipped = new Color[pixW * pixH];
        for (int y = 0; y < pixH; y++)
            Array.Copy(canvas, y * pixW, flipped, (pixH - 1 - y) * pixW, pixW);

        // 复用或重建 Texture2D
        if (_mapTexture != null && (_mapTexture.width != pixW || _mapTexture.height != pixH))
        {
            Destroy(_mapTexture);
            _mapTexture = null;
        }
        if (_mapTexture == null)
        {
            _mapTexture = new Texture2D(pixW, pixH, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        _mapTexture.SetPixels(flipped);
        _mapTexture.Apply();
    }

    // ══════════════════════════════════════════════════════════════
    //  将纹理应用到 RawImage，并调整 RectTransform 尺寸
    // ══════════════════════════════════════════════════════════════

    private void ApplyToRawImage()
    {
        if (_mapTexture == null) return;

        // 若未在 Inspector 中指定，自动在子对象中查找
        if (mapRawImage == null)
            mapRawImage = GetComponentInChildren<RawImage>();

        if (mapRawImage == null)
        {
            Debug.LogWarning("[MapView] 未找到 RawImage 组件，请在 Inspector 中指定 mapRawImage。");
            return;
        }

        mapRawImage.texture = _mapTexture;

        // 按 pixelScale 设置 RawImage 的显示尺寸
        float dispW = _mapTexture.width * pixelScale;
        float dispH = _mapTexture.height * pixelScale;

        var rt = mapRawImage.rectTransform;
        rt.sizeDelta = new Vector2(dispW, dispH);
    }

    // ══════════════════════════════════════════════════════════════
    //  工具方法
    // ══════════════════════════════════════════════════════════════

    /// <summary>相对路径时以 StreamingAssets 为根进行解析。</summary>
    private static string ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (Path.IsPathRooted(path)) return path;
        return Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, path));
    }

    private void ClearState()
    {
        _mapData = null;
        _tiles = null;
        _mapName = null;
    }

    // ── Alpha 混合（src-over 简化版）──────────────────────────────
    private static Color AlphaBlend(Color baseC, Color overlay)
    {
        float a = overlay.a;
        return new Color(
            baseC.r * (1f - a) + overlay.r * a,
            baseC.g * (1f - a) + overlay.g * a,
            baseC.b * (1f - a) + overlay.b * a,
            1f);
    }

    // ── 网格线（tile 四边）────────────────────────────────────────
    private static void DrawGridBorder(Color[] canvas, int cw, int ch,
        int ox, int oy, int w, int h)
    {
        for (int px = 0; px < w; px++)
        {
            SetPixel(canvas, cw, ch, ox + px, oy, GridColor);
            SetPixel(canvas, cw, ch, ox + px, oy + h - 1, GridColor);
        }
        for (int py = 0; py < h; py++)
        {
            SetPixel(canvas, cw, ch, ox, oy + py, GridColor);
            SetPixel(canvas, cw, ch, ox + w - 1, oy + py, GridColor);
        }
    }

    private static void SetPixel(Color[] canvas, int cw, int ch, int x, int y, Color c)
    {
        if (x < 0 || y < 0 || x >= cw || y >= ch) return;
        canvas[y * cw + x] = AlphaBlend(canvas[y * cw + x], c);
    }

    // ── 3×5 像素字体（事件号显示）──────────────────────────────────
    private static readonly byte[,] Font3x5 = new byte[10, 5]
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
        string s = num.ToString();
        int drawX = ox + 1;
        int drawY = oy + 1;
        foreach (char ch2 in s)
        {
            int d = ch2 - '0';
            if (d < 0 || d > 9) continue;
            for (int row = 0; row < 5; row++)
                for (int col = 0; col < 3; col++)
                    if ((Font3x5[d, row] & (0b100 >> col)) != 0)
                        SetPixel(canvas, cw, ch, drawX + col, drawY + row, EventColor);
            drawX += 4; // 字符间距 3px + 1px 间隔
        }
    }
}
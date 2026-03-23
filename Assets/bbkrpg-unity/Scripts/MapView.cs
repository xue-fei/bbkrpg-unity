using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 运行时瓦片式地图视图
///
/// 分块渲染方案：
///   · 每种 tile（最多127种）解码为一张独立 Texture2D + Sprite，相同种类共享复用。
///   · 地图每个格子在 Canvas 下生成一个 Image GameObject，
///     通过 Sprite 引用对应 tile，不合并大图。
///   · 叠加层（行走/事件/网格）以独立子 Image 实现，可随时切换显隐。
///
/// 层级结构（自动创建）：
///   Canvas
///   └─ MapViewRoot            ← 本脚本挂载处（需有 RectTransform）
///      ├─ TileLayer           ← 地图格子父节点
///      └─ OverlayLayer        ← 叠加层父节点
///
/// 调用：
///   GetComponent&lt;MapView&gt;().LoadMap("path/to/1-1.map");
///   GetComponent&lt;MapView&gt;().RefreshOverlay(walkable:true, events:true, grid:false);
/// </summary>
public class MapView : MonoBehaviour
{
    // ══════════════════════════════════════════════════════════════
    //  Inspector 字段
    // ══════════════════════════════════════════════════════════════

    [Header("文件路径")]
    [Tooltip(".map 文件路径（绝对路径，或相对于 StreamingAssets 的路径）")]
    public string mapFilePath = "";

    [Tooltip(".til 文件路径；留空则自动推断（同级 ../til/1-{tilIndex}.til）")]
    public string tilFilePath = "";

    [Header("显示")]
    [Range(0.5f, 8f)]
    [Tooltip("每格显示像素 = 16 × pixelScale")]
    public float pixelScale = 2f;

    [Header("叠加层")]
    public bool showWalkable = false;
    public bool showEvents = false;
    public bool showGrid = false;

    // ══════════════════════════════════════════════════════════════
    //  常量
    // ══════════════════════════════════════════════════════════════

    private const int TILE_W = 16;
    private const int TILE_H = 16;

    private static readonly Color BlackColor = Color.black;
    private static readonly Color WhiteColor = Color.white;
    private static readonly Color WalkColor = new Color(0f, 1f, 0f, 0.25f);
    private static readonly Color BlockColor = new Color(1f, 0f, 0f, 0.18f);
    private static readonly Color GridColor = new Color(0.5f, 0.5f, 1f, 0.55f);
    private static readonly Color EventColor = new Color(1f, 1f, 0f, 1f);

    // 3×5 像素字体（事件号显示）
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

    // ══════════════════════════════════════════════════════════════
    //  私有状态
    // ══════════════════════════════════════════════════════════════

    // .map 元数据
    private int _mapResType, _mapResIndex;
    private int _tilIndex;
    private string _mapName;
    private int _mapWidth, _mapHeight;
    private byte[] _mapData;          // 每格 2 字节：低=tile信息，高=事件号

    // tile 种类资源（按 tileIndex 索引，最多127个）
    private Texture2D[] _tileTex;     // 每种 tile 独立纹理
    private Sprite[] _tileSprites; // 对应 Sprite

    // Canvas 层次节点
    private RectTransform _tileLayer;
    private RectTransform _overlayLayer;

    // 叠加层格子对象（索引 = ty * mapWidth + tx）
    private Image[] _walkImages;     // 行走/阻挡高亮
    private Image[] _eventImages;    // 事件号（null 表示该格无事件）
    private GameObject[] _gridObjects;   // 网格线容器

    // 事件号纹理缓存（按 evtN 1~255 懒建，相同号复用）
    private Texture2D[] _eventTexCache;

    // ══════════════════════════════════════════════════════════════
    //  公开属性
    // ══════════════════════════════════════════════════════════════

    public string MapName => _mapName;
    public int MapWidth => _mapWidth;
    public int MapHeight => _mapHeight;
    public bool IsLoaded => _mapData != null && _tileSprites != null;

    // ══════════════════════════════════════════════════════════════
    //  Unity 生命周期
    // ══════════════════════════════════════════════════════════════

    private void Start()
    {
        mapFilePath = Application.dataPath + "/../ExRes/map/1-1-三清宫.map";

        if (!string.IsNullOrEmpty(mapFilePath))
            LoadMap(mapFilePath, tilFilePath);
    }

    private void OnDestroy() => ClearAll();

    // ══════════════════════════════════════════════════════════════
    //  公开 API
    // ══════════════════════════════════════════════════════════════

    /// <summary>加载地图并在 Canvas 下分块生成所有 tile Image GameObject。</summary>
    public bool LoadMap(string mapPath, string tilPath = "")
    {
        ClearAll();

        mapPath = ResolvePath(mapPath);
        if (!File.Exists(mapPath))
        {
            Debug.LogError($"[MapView] .map 文件不存在：{mapPath}");
            return false;
        }

        byte[] mapBuf;
        try { mapBuf = File.ReadAllBytes(mapPath); }
        catch (Exception e) { Debug.LogError($"[MapView] 读取 .map 失败：{e.Message}"); return false; }

        if (!ParseMap(mapBuf)) return false;

        // 推断 .til 路径
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

        byte[] tilBuf;
        try { tilBuf = File.ReadAllBytes(tilPath); }
        catch (Exception e) { Debug.LogError($"[MapView] 读取 .til 失败：{e.Message}"); return false; }

        if (!ParseTil(tilBuf)) return false;

        BuildTileMap();

        Debug.Log($"[MapView] 地图加载完成：{_mapName}  {_mapWidth}×{_mapHeight} 格  " +
                  $"tile 种数={_tileSprites.Length}  格子数={_mapWidth * _mapHeight}");
        return true;
    }

    /// <summary>切换叠加显示（无需重新解析或重建 GameObject）。</summary>
    public void RefreshOverlay(bool walkable, bool events, bool grid)
    {
        showWalkable = walkable;
        showEvents = events;
        showGrid = grid;
        SetArrayActive(_walkImages, showWalkable);
        SetArrayActive(_eventImages, showEvents);
        SetArrayActive(_gridObjects, showGrid);
    }

    /// <summary>查询格子信息（x, y 为地图格坐标）。</summary>
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
            Debug.LogError($"[MapView] .map 文件过小（{buf.Length} 字节）");
            return false;
        }

        _mapResType = buf[0];
        _mapResIndex = buf[1];
        _tilIndex = buf[2];

        int nameEnd = 3;
        while (nameEnd < 0x10 && buf[nameEnd] != 0) nameEnd++;
        try { _mapName = Encoding.GetEncoding("gb2312").GetString(buf, 3, nameEnd - 3); }
        catch { _mapName = "(unknown)"; }

        _mapWidth = buf[0x10];
        _mapHeight = buf[0x11];

        if (_mapWidth == 0 || _mapHeight == 0)
        {
            Debug.LogError($"[MapView] 地图尺寸无效 W={_mapWidth} H={_mapHeight}");
            return false;
        }

        int dataLen = _mapWidth * _mapHeight * 2;
        if (buf.Length < 0x12 + dataLen)
        {
            Debug.LogError($"[MapView] 地图数据不足（需 {0x12 + dataLen}，实 {buf.Length}）");
            return false;
        }

        _mapData = new byte[dataLen];
        Array.Copy(buf, 0x12, _mapData, 0, dataLen);
        return true;
    }

    // ══════════════════════════════════════════════════════════════
    //  解析 .til → 每种 tile 独立 Texture2D + Sprite
    //
    //  Mode=1：不透明1bpp（0=白，1=黑，高位先行，行末字节丢弃）
    //  Mode=2：透明2bpp（高bit=透明标志，低bit=颜色：0=白，1=黑）
    // ══════════════════════════════════════════════════════════════

    private bool ParseTil(byte[] buf)
    {
        if (buf.Length < 6) { Debug.LogError("[MapView] .til 文件过小"); return false; }

        int iw = buf[2] & 0xFF;
        int ih = buf[3] & 0xFF;
        int num = buf[4] & 0xFF;
        int mode = buf[5] & 0xFF;

        if (iw != TILE_W || ih != TILE_H)
        {
            Debug.LogError($"[MapView] tile 尺寸非预期 {iw}×{ih}，期望 16×16");
            return false;
        }
        if (mode != 1 && mode != 2)
        {
            Debug.LogError($"[MapView] 未知 mode={mode}");
            return false;
        }

        int rowBytes = (iw + 7) / 8;
        int tileBytes = rowBytes * ih * mode;
        int dataLen = num * tileBytes;

        if (buf.Length < 6 + dataLen)
        {
            Debug.LogError($"[MapView] .til 数据不足（需 {6 + dataLen}，实 {buf.Length}）");
            return false;
        }

        _tileTex = new Texture2D[num];
        _tileSprites = new Sprite[num];

        byte[] pixData = new byte[dataLen];
        Array.Copy(buf, 6, pixData, 0, dataLen);
        int iData = 0;

        for (int t = 0; t < num; t++)
        {
            // 解码到图像坐标（左上原点，row-major）
            Color[] px = new Color[iw * ih];
            int iOff = 0, bitCnt = 0;

            if (mode == 1)
            {
                for (int y = 0; y < ih; y++)
                {
                    for (int x = 0; x < iw; x++)
                    {
                        px[iOff++] = ((pixData[iData] << bitCnt) & 0x80) != 0
                            ? BlackColor : WhiteColor;
                        if (++bitCnt >= 8) { bitCnt = 0; ++iData; }
                    }
                    if (bitCnt != 0) { bitCnt = 0; ++iData; } // 行末字节对齐
                }
            }
            else // mode == 2
            {
                Color transpColor = new Color(0f, 0f, 0f, 0f);
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
                    if (bitCnt > 0 && bitCnt <= 7) { bitCnt = 0; ++iData; }
                    if (iData % 2 != 0) ++iData;
                }
            }

            // 图像坐标 → Unity 左下原点：垂直翻转
            Color[] flipped = FlipVertical(px, iw, ih);

            // 每种 tile 独立一张 Texture2D
            var tex = new Texture2D(iw, ih, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            tex.SetPixels(flipped);
            tex.Apply();
            tex.name = $"Tile_{t}";

            // pivot 设在左上角（0, 1），pixelsPerUnit=1 由 sizeDelta 控制显示尺寸
            _tileTex[t] = tex;
            _tileSprites[t] = Sprite.Create(
                tex,
                new Rect(0, 0, iw, ih),
                new Vector2(0f, 1f),
                1f
            );
        }

        return true;
    }

    // ══════════════════════════════════════════════════════════════
    //  在 Canvas 下分块构建地图 GameObject 树
    // ══════════════════════════════════════════════════════════════

    private void BuildTileMap()
    {
        float cellW = TILE_W * pixelScale;
        float cellH = TILE_H * pixelScale;
        int total = _mapWidth * _mapHeight;

        // 容器节点（左上锚点，尺寸 = 整张地图像素大小）
        Vector2 mapSize = new Vector2(_mapWidth * cellW, _mapHeight * cellH);
        _tileLayer = CreateLayer("TileLayer", siblingIndex: 0, mapSize);
        _overlayLayer = CreateLayer("OverlayLayer", siblingIndex: 1, mapSize);

        _walkImages = new Image[total];
        _eventImages = new Image[total];
        _gridObjects = new GameObject[total];
        _eventTexCache = new Texture2D[256];

        for (int ty = 0; ty < _mapHeight; ty++)
        {
            for (int tx = 0; tx < _mapWidth; tx++)
            {
                int idx = ty * _mapWidth + tx;
                int di = idx * 2;

                int lo = _mapData[di] & 0xFF;
                int hi = _mapData[di + 1] & 0xFF;
                int tileIdx = lo & 0x7F;
                bool canWalk = (lo & 0x80) != 0;
                int evtN = hi;

                // Canvas 左上锚点坐标：X 向右正，Y 向下为负
                float posX = tx * cellW;
                float posY = -ty * cellH;

                // ── TileLayer：每格一个 Image，Sprite = 对应 tile ──
                Sprite sp = tileIdx < _tileSprites.Length ? _tileSprites[tileIdx] : null;
                var cellImg = MakeImage(
                    _tileLayer, $"T_{tx}_{ty}", sp,
                    posX, posY, cellW, cellH, Color.white);
                cellImg.raycastTarget = false;

                // ── OverlayLayer：行走高亮 ───────────────────────
                var walkImg = MakeImage(
                    _overlayLayer, $"W_{tx}_{ty}", null,
                    posX, posY, cellW, cellH,
                    canWalk ? WalkColor : BlockColor);
                walkImg.raycastTarget = false;
                walkImg.gameObject.SetActive(showWalkable);
                _walkImages[idx] = walkImg;

                // ── OverlayLayer：事件号 ─────────────────────────
                if (evtN != 0)
                {
                    Texture2D evtTex = GetOrBuildEventTex(evtN);
                    Sprite evtSp = Sprite.Create(
                        evtTex,
                        new Rect(0, 0, evtTex.width, evtTex.height),
                        new Vector2(0f, 1f), 1f);
                    var evtImg = MakeImage(
                        _overlayLayer, $"E_{tx}_{ty}", evtSp,
                        posX, posY, cellW, cellH, Color.white);
                    evtImg.raycastTarget = false;
                    evtImg.gameObject.SetActive(showEvents);
                    _eventImages[idx] = evtImg;
                }

                // ── OverlayLayer：网格线 ─────────────────────────
                var gridGo = MakeGridLines(
                    _overlayLayer, $"G_{tx}_{ty}",
                    posX, posY, cellW, cellH);
                gridGo.SetActive(showGrid);
                _gridObjects[idx] = gridGo;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  创建 Image GameObject（左上锚点/轴心）
    // ══════════════════════════════════════════════════════════════

    private static Image MakeImage(
        RectTransform parent, string name, Sprite sprite,
        float posX, float posY, float w, float h, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f); // 左上锚点
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(posX, posY);
        rt.sizeDelta = new Vector2(w, h);

        var img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.type = Image.Type.Simple;
        return img;
    }

    // ══════════════════════════════════════════════════════════════
    //  网格线：用4个细长 Image 子对象模拟单格边框
    // ══════════════════════════════════════════════════════════════

    private static GameObject MakeGridLines(
        RectTransform parent, string name,
        float posX, float posY, float cellW, float cellH)
    {
        var root = new GameObject(name, typeof(RectTransform));
        root.transform.SetParent(parent, false);

        var rt = root.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(posX, posY);
        rt.sizeDelta = new Vector2(cellW, cellH);

        // (localX, localY_下正, width, height) — 局部坐标，Y 轴向下表示
        (float lx, float ly, float sw, float sh)[] lines = {
            (0,         0,         cellW, 1),       // 上边
            (0,         cellH - 1, cellW, 1),       // 下边
            (0,         0,         1,     cellH),    // 左边
            (cellW - 1, 0,         1,     cellH),    // 右边
        };

        foreach (var (lx, ly, sw, sh) in lines)
        {
            var lineGo = new GameObject("l", typeof(RectTransform), typeof(Image));
            lineGo.transform.SetParent(root.transform, false);

            var lrt = lineGo.GetComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0f, 1f);
            lrt.anchorMax = new Vector2(0f, 1f);
            lrt.pivot = new Vector2(0f, 1f);
            lrt.anchoredPosition = new Vector2(lx, -ly); // Canvas Y 向上
            lrt.sizeDelta = new Vector2(sw, sh);

            var img = lineGo.GetComponent<Image>();
            img.color = GridColor;
            img.raycastTarget = false;
        }

        return root;
    }

    // ══════════════════════════════════════════════════════════════
    //  事件号纹理（按 evtN 懒建，相同号复用）
    // ══════════════════════════════════════════════════════════════

    private Texture2D GetOrBuildEventTex(int evtN)
    {
        if (_eventTexCache[evtN] != null) return _eventTexCache[evtN];

        Color[] px = new Color[TILE_W * TILE_H]; // 全透明底

        string s = evtN.ToString();
        int drawX = 1, drawY = 1;
        foreach (char ch in s)
        {
            int d = ch - '0';
            if (d < 0 || d > 9) continue;
            for (int row = 0; row < 5; row++)
                for (int col = 0; col < 3; col++)
                    if ((Font3x5[d, row] & (0b100 >> col)) != 0)
                    {
                        int px2 = drawX + col, py2 = drawY + row;
                        if (px2 < TILE_W && py2 < TILE_H)
                            px[py2 * TILE_W + px2] = EventColor;
                    }
            drawX += 4;
        }

        var tex = new Texture2D(TILE_W, TILE_H, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = $"Evt_{evtN}"
        };
        tex.SetPixels(FlipVertical(px, TILE_W, TILE_H));
        tex.Apply();

        _eventTexCache[evtN] = tex;
        return tex;
    }

    // ══════════════════════════════════════════════════════════════
    //  清理
    // ══════════════════════════════════════════════════════════════

    private void ClearAll()
    {
        // 销毁容器节点（子 GameObject 随之销毁）
        if (_tileLayer != null) { Destroy(_tileLayer.gameObject); _tileLayer = null; }
        if (_overlayLayer != null) { Destroy(_overlayLayer.gameObject); _overlayLayer = null; }

        // 销毁 tile 纹理（Sprite 依赖纹理，无需单独 Destroy）
        if (_tileTex != null)
        {
            foreach (var t in _tileTex) if (t) Destroy(t);
            _tileTex = null;
            _tileSprites = null;
        }

        // 销毁事件号纹理缓存
        if (_eventTexCache != null)
        {
            foreach (var t in _eventTexCache) if (t) Destroy(t);
            _eventTexCache = null;
        }

        _mapData = null;
        _mapName = null;
        _walkImages = null;
        _eventImages = null;
        _gridObjects = null;
    }

    // ══════════════════════════════════════════════════════════════
    //  工具方法
    // ══════════════════════════════════════════════════════════════

    /// <summary>创建一个左上锚点的容器 RectTransform 子节点。</summary>
    private RectTransform CreateLayer(string layerName, int siblingIndex, Vector2 size)
    {
        var go = new GameObject(layerName, typeof(RectTransform));
        go.transform.SetParent(transform, false);
        go.transform.SetSiblingIndex(siblingIndex);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
        return rt;
    }

    private static void SetArrayActive<T>(T[] arr, bool active) where T : Component
    {
        if (arr == null) return;
        foreach (var item in arr)
            if (item != null) item.gameObject.SetActive(active);
    }

    private static void SetArrayActive(GameObject[] arr, bool active)
    {
        if (arr == null) return;
        foreach (var go in arr)
            if (go != null) go.SetActive(active);
    }

    /// <summary>图像坐标（左上原点）→ Unity 坐标（左下原点）垂直翻转。</summary>
    private static Color[] FlipVertical(Color[] src, int w, int h)
    {
        Color[] dst = new Color[w * h];
        for (int y = 0; y < h; y++)
            Array.Copy(src, y * w, dst, (h - 1 - y) * w, w);
        return dst;
    }

    /// <summary>相对路径以 StreamingAssets 为根解析；绝对路径直接返回。</summary>
    private static string ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path)) return path;
        return Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, path));
    }
}
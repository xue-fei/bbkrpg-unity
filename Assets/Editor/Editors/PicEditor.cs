using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 杂类图片(PIC)查看器
///
/// PIC 文件格式（与 ResImage.SetData 完全一致）：
///   +0x00  Type   1字节
///   +0x01  Index  1字节
///   +0x02  Width  1字节（像素宽）
///   +0x03  Height 1字节（像素高）
///   +0x04  Number 1字节（切片数量）
///   +0x05  Mode   1字节（1=不透明1bpp, 2=透明2bpp）
///   +0x06  PixelData
///
/// 像素解码（来自 ResImage.CreateBitmaps）：
///   Mode=1 不透明：每像素1bit，高位先行，0=白 1=黑，行末不足1字节的位丢弃
///   Mode=2 透明  ：每像素2bit，高位先行
///                  高bit=1 → 透明(magenta)
///                  高bit=0, 低bit=1 → 黑
///                  高bit=0, 低bit=0 → 白
///                  行末不足1字节的位丢弃，且行字节数需对齐到偶数字节（iOfData%2!=0 则+1）
/// </summary>
public class PicEditor : EditorWindow
{
    // ── 文件信息 ──────────────────────────────────────────
    private int _picType;
    private int _picIndex;
    private int _width;
    private int _height;
    private int _number;
    private int _mode;

    // ── 显示状态 ──────────────────────────────────────────
    private Texture2D[] _frames;          // 每个切片一张 Texture2D
    private int _currentFrame;    // 当前显示的切片索引（0-based）
    private string _filePath;
    private string _statusMsg;

    // ── 显示参数 ──────────────────────────────────────────
    private float _zoom = 2f;             // 缩放倍数
    private Vector2 _scroll;

    // 透明像素用洋红色表示（与原游戏引擎 COLOR_TRANSP 一致）
    private static readonly Color TranspColor = new Color(1f, 0f, 1f, 1f);
    private static readonly Color BlackColor = Color.black;
    private static readonly Color WhiteColor = Color.white;

    [MenuItem("工具/杂类图片")]
    static void Init()
    {
        GetWindow<PicEditor>("杂类图片");
    }

    private void OnGUI()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        if (GUILayout.Button("打开 .pic 文件", EditorStyles.toolbarButton, GUILayout.Width(120)))
            OpenFile();

        EditorGUI.BeginDisabledGroup(_frames == null || _frames.Length <= 1);
        if (GUILayout.Button("◀", EditorStyles.toolbarButton, GUILayout.Width(24)))
            SetFrame(_currentFrame - 1);
        if (GUILayout.Button("▶", EditorStyles.toolbarButton, GUILayout.Width(24)))
            SetFrame(_currentFrame + 1);
        EditorGUI.EndDisabledGroup();

        GUILayout.Label(_frames != null ? $"切片 {_currentFrame + 1}/{_number}" : "", EditorStyles.toolbar, GUILayout.Width(70));

        GUILayout.FlexibleSpace();
        GUILayout.Label("缩放", EditorStyles.toolbar, GUILayout.Width(30));
        _zoom = EditorGUILayout.Slider(_zoom, 1f, 8f, GUILayout.Width(120));
        EditorGUILayout.EndHorizontal();

        // ── 文件信息栏 ────────────────────────────────────
        if (!string.IsNullOrEmpty(_filePath))
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("文件", Path.GetFileName(_filePath));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Type={_picType}  Index={_picIndex}", GUILayout.Width(150));
            EditorGUILayout.LabelField($"W={_width}  H={_height}  切片={_number}  Mode={_mode} ({(_mode == 2 ? "透明2bpp" : "不透明1bpp")})", GUILayout.Width(280));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // ── 状态/错误信息 ─────────────────────────────────
        if (!string.IsNullOrEmpty(_statusMsg))
        {
            EditorGUILayout.HelpBox(_statusMsg, MessageType.Warning);
        }

        // ── 图片显示区域 ──────────────────────────────────
        if (_frames != null && _frames.Length > 0 && _frames[_currentFrame] != null)
        {
            Texture2D tex = _frames[_currentFrame];
            float dispW = tex.width * _zoom;
            float dispH = tex.height * _zoom;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            Rect imgRect = GUILayoutUtility.GetRect(dispW, dispH);
            // 保持像素清晰（点采样）
            EditorGUI.DrawPreviewTexture(imgRect, tex, null, ScaleMode.ScaleToFit);
            EditorGUILayout.EndScrollView();
        }
        else if (_frames == null)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("← 点击打开.pic 文件加载图片", EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
        }
    }

    // ── 打开文件 ──────────────────────────────────────────
    private void OpenFile()
    {
        string path = EditorUtility.OpenFilePanelWithFilters(
            "选择杂类图片", "ExRes/pic", new[] { "PIC 文件", "pic", "所有文件", "*" }
        );
        if (string.IsNullOrEmpty(path)) return;

        _statusMsg = null;
        _frames = null;

        byte[] buf = File.ReadAllBytes(path);
        if (buf.Length < 6)
        {
            _statusMsg = $"文件过小（{buf.Length} 字节），无法解析";
            Repaint();
            return;
        }

        int offset = 0;
        _picType = buf[offset];
        _picIndex = buf[offset + 1] & 0xFF;
        _width = buf[offset + 2] & 0xFF;
        _height = buf[offset + 3] & 0xFF;
        _number = buf[offset + 4] & 0xFF;
        _mode = buf[offset + 5] & 0xFF;

        Debug.Log($"[PicEditor] type={_picType} idx={_picIndex} W={_width} H={_height} num={_number} mode={_mode}");

        if (_width == 0 || _height == 0 || _number == 0)
        {
            _statusMsg = $"宽/高/切片数为0，无法显示（W={_width} H={_height} num={_number}）";
            _filePath = path;
            Repaint();
            return;
        }
        if (_mode != 1 && _mode != 2)
        {
            _statusMsg = $"未知 Mode={_mode}，仅支持 1(不透明) 和 2(透明)";
            _filePath = path;
            Repaint();
            return;
        }

        // ── 计算像素数据长度（与 ResImage.SetData 完全一致）──
        int rowBytes = _width / 8 + (_width % 8 != 0 ? 1 : 0);  // ceil(W/8)
        int dataLen = _number * rowBytes * _height * _mode;
        int expectedTotal = 6 + dataLen;

        if (buf.Length < expectedTotal)
        {
            _statusMsg = $"文件长度不足：期望 {expectedTotal} 字节，实际 {buf.Length} 字节";
            _filePath = path;
            Repaint();
            return;
        }

        // ── 解码所有切片 ──────────────────────────────────
        _frames = new Texture2D[_number];
        _currentFrame = 0;
        _filePath = path;

        byte[] pixelData = new byte[dataLen];
        System.Array.Copy(buf, 6, pixelData, 0, dataLen);

        if (_mode == 1)
            DecodeOpaque(pixelData);
        else
            DecodeTransparent(pixelData);

        Repaint();
    }

    // ── Mode=1：不透明 1bpp ───────────────────────────────
    // 逐像素读1bit，高位先行，0=白 1=黑，行末不足1字节的位丢弃
    private void DecodeOpaque(byte[] data)
    {
        int iData = 0;
        for (int i = 0; i < _number; i++)
        {
            Color[] pixels = new Color[_width * _height];
            int iOfTmp = 0;
            int cnt = 0;

            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    bool isBlack = ((data[iData] << cnt) & 0x80) != 0;
                    pixels[iOfTmp++] = isBlack ? BlackColor : WhiteColor;
                    if (++cnt >= 8) { cnt = 0; ++iData; }
                }
                // 行末不足1字节的位丢弃
                if (cnt != 0) { cnt = 0; ++iData; }
            }

            _frames[i] = BuildTexture(pixels);
        }
    }

    // ── Mode=2：透明 2bpp ─────────────────────────────────
    // 逐像素读2bit，高位先行
    // 高bit=1 → 透明；高bit=0, 低bit=1 → 黑；高bit=0, 低bit=0 → 白
    // 行末不足1字节丢弃，且行结束后若 iData 为奇数则再跳1字节（偶数对齐）
    private void DecodeTransparent(byte[] data)
    {
        int iData = 0;
        for (int i = 0; i < _number; i++)
        {
            Color[] pixels = new Color[_width * _height];
            int iOfTmp = 0;
            int cnt = 0;

            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    bool highBit = ((data[iData] << cnt) & 0x80) != 0;
                    bool lowBit = ((data[iData] << cnt << 1) & 0x80) != 0;

                    if (highBit)
                        pixels[iOfTmp] = TranspColor;         // 透明
                    else
                        pixels[iOfTmp] = lowBit ? BlackColor : WhiteColor;

                    ++iOfTmp;
                    cnt += 2;
                    if (cnt >= 8) { cnt = 0; ++iData; }
                }
                // 行末不足1字节丢弃
                if (cnt > 0 && cnt <= 7) { cnt = 0; ++iData; }
                // 偶数字节对齐（ResImage.CreateBitmaps 原文：if (iOfData % 2 != 0) ++iOfData）
                if (iData % 2 != 0) { ++iData; }
            }

            _frames[i] = BuildTexture(pixels);
        }
    }

    // ── 像素数组 → Texture2D ─────────────────────────────
    // Unity Texture2D 坐标原点在左下角，图像数据是从左上角开始的，需要垂直翻转
    private Texture2D BuildTexture(Color[] pixels)
    {
        // 垂直翻转（Unity UV原点在左下，图像数据从左上开始）
        Color[] flipped = new Color[_width * _height];
        for (int y = 0; y < _height; y++)
        {
            int srcRow = y * _width;
            int dstRow = (_height - 1 - y) * _width;
            System.Array.Copy(pixels, srcRow, flipped, dstRow, _width);
        }

        var tex = new Texture2D(_width, _height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,   // 保持像素清晰，不模糊
            wrapMode = TextureWrapMode.Clamp
        };
        tex.SetPixels(flipped);
        tex.Apply();
        return tex;
    }

    // ── 切片切换 ──────────────────────────────────────────
    private void SetFrame(int idx)
    {
        if (_frames == null) return;
        _currentFrame = (idx + _number) % _number;
        Repaint();
    }

    private void OnDestroy()
    {
        if (_frames == null) return;
        foreach (var t in _frames)
            if (t != null) DestroyImmediate(t);
    }
}
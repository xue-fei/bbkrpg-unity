using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// SRS 特效资源查看器 / 序列帧播放器
///
/// ── SRS 格式（ResSrs.SetData）────────────────────────────────────
///   +0x00  Type          1字节
///   +0x01  Index         1字节
///   +0x02  FrameCount    1字节  动画帧数
///   +0x03  ImageCount    1字节  底层图片数
///   +0x04  StartFrame    1字节
///   +0x05  EndFrame      1字节
///   +0x06  FrameHeader[FrameCount][5]  每帧5字节：x  y  show  nshow  imgIdx
///   +0x06+FC*5  ResImage[ImageCount]   连续变长 ResImage 块
///
/// ── 播放逻辑（与 ResSrs.Update 完全一致）────────────────────────
///   showList 维护当前活跃帧列表，每个元素持有 (frameIdx, Show, NShow)
///   每个 tick：
///     1. 所有活跃帧 Show--, NShow--
///     2. NShow==0 且 frameIdx+1 < FrameCount → 把下一帧加入 showList
///     3. Show<=0 的帧从 showList 移除
///     4. showList 为空 → 动画播完（自动循环）
///   渲染：将 showList 中所有活跃帧的图片按 (x,y) 合成到画布
///
/// ── 像素解码（ResImage.CreateBitmaps）───────────────────────────
///   Mode=1 不透明1bpp：每像素1bit，高位先行，0=白 1=黑，行末不足1字节丢弃
///   Mode=2 透明2bpp  ：每像素2bit，高位先行
///                      高bit=1→透明  高bit=0低bit=1→黑  其余→白
///                      行末不足1字节丢弃，偶数字节对齐
/// </summary>
public class SrsEditor : EditorWindow
{
    // ══════════════════════════════════════════════════════
    //  数据
    // ══════════════════════════════════════════════════════
    private string _filePath;
    private string _statusMsg;

    // 文件头信息
    private int _srsType, _srsIndex;
    private int _frameCount, _imageCount, _startFrame, _endFrame;

    // frameHeader[i, 0..4] = x  y  show  nshow  imgIdx
    private int[,] _frameHeader;

    // 底层图片（已解码）
    private DecodedImage[] _images;

    // ══════════════════════════════════════════════════════
    //  播放状态（模拟 ResSrs.Update）
    // ══════════════════════════════════════════════════════
    private class ActiveFrame
    {
        public int FrameIdx;
        public int Show;
        public int NShow;
        public ActiveFrame(int[,] hdr, int idx)
        {
            FrameIdx = idx;
            Show = hdr[idx, 2];
            NShow = hdr[idx, 3];
        }
    }

    private List<ActiveFrame> _showList = new List<ActiveFrame>();
    private bool _isPlaying;
    private bool _looping = true;
    private float _tickInterval = 0.1f;   // 每 tick 对应的真实时间（秒，默认 10 tick/s）
    private float _speed = 1f;     // 播放速度倍率
    private double _lastUpdateTime;        // EditorApplication.timeSinceStartup 上次记录值
    private float _timeSinceLastTick;
    private int _totalTick;             // 已播放 tick 数（用于进度显示）
    private int _estimatedTotalTicks;   // 动画总长度估算

    // ══════════════════════════════════════════════════════
    //  显示
    // ══════════════════════════════════════════════════════
    private Texture2D _frameTex;    // 当前合成帧 Texture2D
    private float _zoom = 2f;
    private Vector2 _scroll;
    private int _canvasW, _canvasH;  // 包围盒大小

    // 手动浏览（暂停时单步）
    private int _manualTick;

    // 渲染模式：对应 ResSrs 的两种 Draw 方式
    //   Draw         — 帧坐标直接用作绝对位置（含 dx/dy 偏移，编辑器 dx=dy=0）
    //   DrawAbsolutely — 帧坐标相对 frame[0] 偏移（全屏/居中特效使用此方式）
    private bool _drawAbsolutely = false;
    // frame[0] 的坐标，DrawAbsolutely 时用作锚点
    private int _anchorX, _anchorY;
    // DrawAbsolutely 模式下的包围盒（相对坐标）
    private int _absCanvasW, _absCanvasH;

    // 背景色
    private static readonly Color BgColor = new Color(0.15f, 0.15f, 0.15f);
    private static readonly Color TranspColor = new Color(1f, 0f, 1f, 1f);
    private static readonly Color BlackColor = Color.black;
    private static readonly Color WhiteColor = Color.white;

    // ══════════════════════════════════════════════════════
    //  底层图片结构
    // ══════════════════════════════════════════════════════
    private struct DecodedImage
    {
        public int Width, Height;
        public Color[] Pixels;   // 图像坐标（左上原点），与 Unity 相反，合成时手动翻转
    }

    // ══════════════════════════════════════════════════════
    //  菜单入口
    // ══════════════════════════════════════════════════════
    [MenuItem("工具/SRS特效查看器")]
    static void Init() => GetWindow<SrsEditor>("SRS 特效");

    // ══════════════════════════════════════════════════════
    //  OnEnable / OnDisable
    // ══════════════════════════════════════════════════════
    private void OnEnable()
    {
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
    }

    // ══════════════════════════════════════════════════════
    //  Editor Update（驱动播放 tick）
    // ══════════════════════════════════════════════════════
    private void OnEditorUpdate()
    {
        if (!_isPlaying || _frameHeader == null) return;

        // Editor 中 Time.deltaTime 不可靠（静止时为0），改用 timeSinceStartup 差值
        double now = EditorApplication.timeSinceStartup;
        float delta = (float)(now - _lastUpdateTime);
        _lastUpdateTime = now;

        // delta 异常保护（首帧、切后台、极大值）
        if (delta <= 0f || delta > 1f) return;

        _timeSinceLastTick += delta * _speed;

        // 单次 OnEditorUpdate 可能累积多个 tick（速度较快时），逐一步进
        while (_timeSinceLastTick >= _tickInterval)
        {
            _timeSinceLastTick -= _tickInterval;
            bool alive = StepTick();
            if (!alive)
            {
                if (_looping)
                {
                    StartAni();
                }
                else
                {
                    _isPlaying = false;
                    _timeSinceLastTick = 0f;
                    break;
                }
            }
        }

        RenderFrame();
        Repaint();
    }

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
            return;
        }

        DrawControls();
        DrawCanvas();
    }

    // ── 工具栏 ────────────────────────────────────────────
    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        if (GUILayout.Button("打开 .srs", EditorStyles.toolbarButton, GUILayout.Width(80)))
            OpenFile();

        GUILayout.Space(8);

        // 播放 / 暂停
        EditorGUI.BeginDisabledGroup(_frameHeader == null);
        string playLabel = _isPlaying ? "⏸ 暂停" : "▶ 播放";
        if (GUILayout.Button(playLabel, EditorStyles.toolbarButton, GUILayout.Width(60)))
            TogglePlay();

        // 停止（归零）
        if (GUILayout.Button("⏹ 停止", EditorStyles.toolbarButton, GUILayout.Width(60)))
            StopAni();

        // 循环开关
        _looping = GUILayout.Toggle(_looping, "循环", EditorStyles.toolbarButton, GUILayout.Width(40));

        GUILayout.Space(4);

        // 渲染模式切换
        bool newMode = GUILayout.Toggle(_drawAbsolutely, "居中(DrawAbsolutely)", EditorStyles.toolbarButton, GUILayout.Width(140));
        if (newMode != _drawAbsolutely)
        {
            _drawAbsolutely = newMode;
            RenderFrame();
        }

        EditorGUI.EndDisabledGroup();

        GUILayout.Space(8);

        // 速度
        GUILayout.Label("速度", GUILayout.Width(30));
        _speed = EditorGUILayout.Slider(_speed, 0.1f, 10f, GUILayout.Width(120));

        GUILayout.FlexibleSpace();

        // 缩放
        GUILayout.Label("缩放", GUILayout.Width(30));
        _zoom = EditorGUILayout.Slider(_zoom, 1f, 8f, GUILayout.Width(110));

        EditorGUILayout.EndHorizontal();
    }

    // ── 信息栏 ────────────────────────────────────────────
    private void DrawInfoBar()
    {
        if (string.IsNullOrEmpty(_filePath)) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("文件", Path.GetFileName(_filePath));
        EditorGUILayout.LabelField(
            $"Type={_srsType}  Index={_srsIndex}  " +
            $"帧数={_frameCount}  图片数={_imageCount}  " +
            $"画布(Draw)={_canvasW}×{_canvasH}  " +
            $"画布(居中)={_absCanvasW}×{_absCanvasH}  " +
            $"估计总长={_estimatedTotalTicks} ticks");
        EditorGUILayout.EndVertical();
    }

    // ── 播放控制区 ────────────────────────────────────────
    private void DrawControls()
    {
        if (_frameHeader == null) return;

        EditorGUILayout.BeginHorizontal();

        // 进度条（只读，显示当前 tick / 总 tick）
        int cur = _isPlaying ? _totalTick : _manualTick;
        int tmax = Mathf.Max(1, _estimatedTotalTicks);
        EditorGUILayout.LabelField("Tick", GUILayout.Width(30));
        EditorGUI.BeginDisabledGroup(_isPlaying);
        int newTick = EditorGUILayout.IntSlider(cur, 0, tmax);
        if (!_isPlaying && newTick != _manualTick)
        {
            _manualTick = newTick;
            SeekToTick(_manualTick);
            RenderFrame();
        }
        EditorGUI.EndDisabledGroup();
        GUILayout.Label($"{cur}/{tmax}", GUILayout.Width(70));

        // 单步
        EditorGUI.BeginDisabledGroup(_isPlaying);
        if (GUILayout.Button("|◀", GUILayout.Width(28)))
        {
            _manualTick = Mathf.Max(0, _manualTick - 1);
            SeekToTick(_manualTick);
            RenderFrame();
        }
        if (GUILayout.Button("▶|", GUILayout.Width(28)))
        {
            _manualTick = Mathf.Min(tmax, _manualTick + 1);
            SeekToTick(_manualTick);
            RenderFrame();
        }
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.EndHorizontal();

        // 帧头表（折叠）
        DrawFrameTable();
    }

    // ── 帧头信息表（折叠显示）────────────────────────────
    private bool _showFrameTable;
    private Vector2 _tableScroll;

    private void DrawFrameTable()
    {
        _showFrameTable = EditorGUILayout.Foldout(_showFrameTable, $"帧头信息（{_frameCount} 帧）");
        if (!_showFrameTable) return;

        _tableScroll = EditorGUILayout.BeginScrollView(_tableScroll,
            GUILayout.Height(Mathf.Min(_frameCount * 18 + 22, 160)));

        // 表头
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("帧", GUILayout.Width(30));
        GUILayout.Label("x", GUILayout.Width(35));
        GUILayout.Label("y", GUILayout.Width(35));
        GUILayout.Label("show", GUILayout.Width(40));
        GUILayout.Label("nshow", GUILayout.Width(45));
        GUILayout.Label("imgIdx", GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();

        for (int i = 0; i < _frameCount; i++)
        {
            // 高亮当前活跃帧
            bool active = IsFrameActive(i);
            if (active)
            {
                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.3f, 0.6f, 1f);
                EditorGUILayout.BeginHorizontal("box");
                GUI.backgroundColor = oldBg;
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
            }
            GUILayout.Label(i.ToString(), GUILayout.Width(30));
            GUILayout.Label(_frameHeader[i, 0].ToString(), GUILayout.Width(35));
            GUILayout.Label(_frameHeader[i, 1].ToString(), GUILayout.Width(35));
            GUILayout.Label(_frameHeader[i, 2].ToString(), GUILayout.Width(40));
            GUILayout.Label(_frameHeader[i, 3].ToString(), GUILayout.Width(45));
            GUILayout.Label(_frameHeader[i, 4].ToString(), GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    // ── 画布区域 ──────────────────────────────────────────
    private void DrawCanvas()
    {
        if (_frameTex == null) return;

        // 当前活跃帧提示（多帧叠加是原游戏正常行为：showList 中所有帧同时绘制）
        if (_showList.Count > 0)
        {
            var indices = new System.Text.StringBuilder();
            foreach (var af in _showList)
                indices.Append(af.FrameIdx).Append(' ');
            EditorGUILayout.LabelField(
                $"当前活跃帧：{_showList.Count} 个（帧索引：{indices}）— 多帧叠加为正常的特效行为",
                EditorStyles.miniLabel);
        }
        else
        {
            EditorGUILayout.LabelField("（无活跃帧）", EditorStyles.miniLabel);
        }

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        Rect r = GUILayoutUtility.GetRect(
            _frameTex.width * _zoom,
            _frameTex.height * _zoom);
        EditorGUI.DrawPreviewTexture(r, _frameTex, null, ScaleMode.ScaleToFit);
        EditorGUILayout.EndScrollView();
    }

    // ══════════════════════════════════════════════════════
    //  文件打开
    // ══════════════════════════════════════════════════════
    private void OpenFile()
    {
        string path = EditorUtility.OpenFilePanelWithFilters(
            "选择 SRS 特效文件", "ExRes/srs",
            new[] { "SRS 特效", "srs", "所有文件", "*" }
        );
        if (string.IsNullOrEmpty(path)) return;

        ClearAll();
        _filePath = path;
        _statusMsg = null;

        byte[] buf;
        try { buf = File.ReadAllBytes(path); }
        catch (System.Exception e) { _statusMsg = $"读取失败：{e.Message}"; Repaint(); return; }

        LoadSrs(buf);
        Repaint();
    }

    // ── 解析 SRS ──────────────────────────────────────────
    private void LoadSrs(byte[] buf)
    {
        if (buf.Length < 6) { _statusMsg = $"文件过小（{buf.Length} 字节）"; return; }

        _srsType = buf[0];
        _srsIndex = buf[1] & 0xFF;
        _frameCount = buf[2] & 0xFF;
        _imageCount = buf[3] & 0xFF;
        _startFrame = buf[4] & 0xFF;
        _endFrame = buf[5] & 0xFF;

        int ptr = 6;

        // ── FrameHeader ───────────────────────────────────
        _frameHeader = new int[_frameCount, 5];
        for (int i = 0; i < _frameCount; i++)
        {
            if (ptr + 5 > buf.Length) { _statusMsg = $"FrameHeader[{i}] 超出文件范围"; return; }
            _frameHeader[i, 0] = buf[ptr++] & 0xFF; // x
            _frameHeader[i, 1] = buf[ptr++] & 0xFF; // y
            _frameHeader[i, 2] = buf[ptr++] & 0xFF; // show
            _frameHeader[i, 3] = buf[ptr++] & 0xFF; // nshow
            _frameHeader[i, 4] = buf[ptr++] & 0xFF; // imgIdx
        }

        // ── ResImage[] ────────────────────────────────────
        _images = new DecodedImage[_imageCount];
        for (int i = 0; i < _imageCount; i++)
        {
            if (ptr + 6 > buf.Length) { _statusMsg = $"Image[{i}] header 超出文件范围"; return; }

            int iw = buf[ptr + 2] & 0xFF;
            int ih = buf[ptr + 3] & 0xFF;
            int inum = buf[ptr + 4] & 0xFF;   // SRS 里 ResImage 通常 num=1
            int imod = buf[ptr + 5] & 0xFF;

            int rowBytes = iw / 8 + (iw % 8 != 0 ? 1 : 0);
            int dataLen = inum * rowBytes * ih * imod;
            int imgSize = 6 + dataLen;

            if (ptr + imgSize > buf.Length) { _statusMsg = $"Image[{i}] 数据超出文件范围"; return; }

            Color[] pixels = null;
            if (iw > 0 && ih > 0 && inum > 0 && (imod == 1 || imod == 2))
            {
                byte[] pixData = new byte[dataLen];
                System.Array.Copy(buf, ptr + 6, pixData, 0, dataLen);
                // 只取第一个切片（SRS num 通常=1）
                pixels = DecodeFirstSlice(pixData, iw, ih, imod);
            }
            pixels = pixels ?? new Color[Mathf.Max(1, iw * ih)];

            _images[i] = new DecodedImage { Width = iw, Height = ih, Pixels = pixels };
            ptr += imgSize;
        }

        // ── 计算画布包围盒（绝对坐标，对应 Draw）────────────
        _canvasW = 1; _canvasH = 1;
        for (int i = 0; i < _frameCount; i++)
        {
            int imgIdx = _frameHeader[i, 4];
            if (imgIdx >= _imageCount) continue;
            _canvasW = Mathf.Max(_canvasW, _frameHeader[i, 0] + _images[imgIdx].Width);
            _canvasH = Mathf.Max(_canvasH, _frameHeader[i, 1] + _images[imgIdx].Height);
        }

        // ── 计算居中包围盒（相对坐标，对应 DrawAbsolutely）──
        // DrawAbsolutely: 每帧坐标 = frameHeader[i].(x,y) - frameHeader[0].(x,y)
        // 因此相对坐标可为负，需先求整体范围再平移到 (0,0)
        _anchorX = _frameCount > 0 ? _frameHeader[0, 0] : 0;
        _anchorY = _frameCount > 0 ? _frameHeader[0, 1] : 0;
        int relMinX = 0, relMinY = 0, relMaxX = 1, relMaxY = 1;
        for (int i = 0; i < _frameCount; i++)
        {
            int imgIdx = _frameHeader[i, 4];
            if (imgIdx >= _imageCount) continue;
            int rx = _frameHeader[i, 0] - _anchorX;
            int ry = _frameHeader[i, 1] - _anchorY;
            relMinX = Mathf.Min(relMinX, rx);
            relMinY = Mathf.Min(relMinY, ry);
            relMaxX = Mathf.Max(relMaxX, rx + _images[imgIdx].Width);
            relMaxY = Mathf.Max(relMaxY, ry + _images[imgIdx].Height);
        }
        _absCanvasW = Mathf.Max(1, relMaxX - relMinX);
        _absCanvasH = Mathf.Max(1, relMaxY - relMinY);
        // 如果存在负相对坐标，需要整体右移/下移，把偏移记到 _anchorX/Y 中
        // 等价于：渲染时帧实际偏移 = (fx - _anchorX - relMinX)
        _anchorX += relMinX;   // 吸收负偏移，使所有相对坐标 >= 0
        _anchorY += relMinY;

        // ── 估算总 tick 长度 ──────────────────────────────
        // 最后一帧的启动时刻 + 该帧的 show 值
        int launchTick = 0;
        for (int i = 0; i < _frameCount - 1; i++)
            launchTick += _frameHeader[i, 3]; // 累加每帧的 nshow
        _estimatedTotalTicks = launchTick + (_frameCount > 0 ? _frameHeader[_frameCount - 1, 2] : 0);
        _estimatedTotalTicks = Mathf.Max(1, _estimatedTotalTicks);

        // ── 初始状态：停在第0帧 ───────────────────────────
        _manualTick = 0;
        _totalTick = 0;
        SeekToTick(0);
        RenderFrame();
    }

    // ══════════════════════════════════════════════════════
    //  播放控制
    // ══════════════════════════════════════════════════════
    private void TogglePlay()
    {
        if (_isPlaying)
        {
            _isPlaying = false;
            _manualTick = _totalTick;
        }
        else
        {
            _isPlaying = true;
            _timeSinceLastTick = 0f;
            _lastUpdateTime = EditorApplication.timeSinceStartup;
            // 若已播完则从头开始
            if (_showList.Count == 0)
                StartAni();
        }
    }

    private void StopAni()
    {
        _isPlaying = false;
        _manualTick = 0;
        _totalTick = 0;
        SeekToTick(0);
        RenderFrame();
        Repaint();
    }

    // 模拟 ResSrs.StartAni
    private void StartAni()
    {
        _showList.Clear();
        _totalTick = 0;
        if (_frameCount > 0)
            _showList.Add(new ActiveFrame(_frameHeader, 0));
    }

    // 模拟 ResSrs.Update 单步，与原版逻辑逐行对应（返回 false = 动画播完）
    private bool StepTick()
    {
        ++_totalTick;

        // Pass1：与原版 for 循环完全一致
        //   --current.Show; --current.NShow;
        //   if (NShow == 0 && Index+1 < frameCount) showList.Add(next)
        for (int i = 0; i < _showList.Count; i++)
        {
            var cur = _showList[i];
            --cur.Show;
            --cur.NShow;
            if (cur.NShow == 0 && cur.FrameIdx + 1 < _frameCount)
                _showList.Add(new ActiveFrame(_frameHeader, cur.FrameIdx + 1));
        }

        // Pass2：与原版 for 循环完全一致
        //   if (current.Show <= 0) { RemoveAt(i); i--; }
        for (int i = 0; i < _showList.Count; i++)
        {
            if (_showList[i].Show <= 0)
            {
                _showList.RemoveAt(i);
                i--;
            }
        }

        return _showList.Count > 0;
    }

    // Seek：重放到指定 tick（从头快进）
    private void SeekToTick(int targetTick)
    {
        StartAni();
        for (int t = 0; t < targetTick && _showList.Count > 0; t++)
            StepTick();
        _totalTick = targetTick;
    }

    // 查询某帧是否在当前 showList 中（用于表格高亮）
    private bool IsFrameActive(int frameIdx)
    {
        foreach (var af in _showList)
            if (af.FrameIdx == frameIdx) return true;
        return false;
    }

    // ══════════════════════════════════════════════════════
    //  渲染当前 showList → _frameTex
    //  对应 ResSrs 两种绘制方法：
    //    Draw          : ox = frameHeader[fi].(x,y)            （绝对坐标）
    //    DrawAbsolutely: ox = frameHeader[fi].(x,y) - anchor   （相对 frame[0] 偏移）
    // ══════════════════════════════════════════════════════
    private void RenderFrame()
    {
        if (_frameHeader == null || _images == null) return;

        int cw = _drawAbsolutely ? _absCanvasW : _canvasW;
        int ch = _drawAbsolutely ? _absCanvasH : _canvasH;
        cw = Mathf.Max(1, cw);
        ch = Mathf.Max(1, ch);

        // 填充背景
        Color[] canvas = new Color[cw * ch];
        for (int i = 0; i < canvas.Length; i++) canvas[i] = BgColor;

        // 将 showList 中所有活跃帧合成到画布
        foreach (var af in _showList)
        {
            int fi = af.FrameIdx;
            int imgIdx = _frameHeader[fi, 4];
            if (imgIdx >= _imageCount) continue;

            DecodedImage img = _images[imgIdx];
            if (img.Pixels == null) continue;

            // 根据渲染模式计算帧在画布上的偏移
            int ox, oy;
            if (_drawAbsolutely)
            {
                // 对应 DrawAbsolutely：帧坐标 - frame[0]坐标（anchor 已包含负偏移修正）
                ox = _frameHeader[fi, 0] - _anchorX;
                oy = _frameHeader[fi, 1] - _anchorY;
            }
            else
            {
                // 对应 Draw：直接用帧的绝对坐标
                ox = _frameHeader[fi, 0];
                oy = _frameHeader[fi, 1];
            }

            for (int py = 0; py < img.Height; py++)
                for (int px = 0; px < img.Width; px++)
                {
                    int cx = ox + px;
                    int cy = oy + py;
                    if (cx < 0 || cy < 0 || cx >= cw || cy >= ch) continue;

                    Color c = img.Pixels[py * img.Width + px];
                    if (c != TranspColor)
                        canvas[cy * cw + cx] = c;
                }
        }

        // 翻转整张 canvas（图像坐标→Unity 左下原点）
        Color[] flipped = new Color[cw * ch];
        for (int y = 0; y < ch; y++)
            System.Array.Copy(canvas, y * cw, flipped, (ch - 1 - y) * cw, cw);

        if (_frameTex == null || _frameTex.width != cw || _frameTex.height != ch)
        {
            if (_frameTex != null) DestroyImmediate(_frameTex);
            _frameTex = new Texture2D(cw, ch, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        _frameTex.SetPixels(flipped);
        _frameTex.Apply();
    }

    // ══════════════════════════════════════════════════════
    //  像素解码（只解第0个切片，与 ResImage.CreateBitmaps 完全一致）
    // ══════════════════════════════════════════════════════
    private static Color[] DecodeFirstSlice(byte[] data, int w, int h, int mode)
    {
        Color[] pixels = new Color[w * h];
        int iData = 0, iOfTmp = 0, cnt = 0;

        if (mode == 1)
        {
            // 不透明 1bpp
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    pixels[iOfTmp++] = ((data[iData] << cnt) & 0x80) != 0
                        ? BlackColor : WhiteColor;
                    if (++cnt >= 8) { cnt = 0; ++iData; }
                }
                if (cnt != 0) { cnt = 0; ++iData; }
            }
        }
        else
        {
            // 透明 2bpp
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool hi = ((data[iData] << cnt) & 0x80) != 0;
                    bool lo = ((data[iData] << cnt << 1) & 0x80) != 0;
                    pixels[iOfTmp++] = hi ? TranspColor : (lo ? BlackColor : WhiteColor);
                    cnt += 2;
                    if (cnt >= 8) { cnt = 0; ++iData; }
                }
                if (cnt > 0 && cnt <= 7) { cnt = 0; ++iData; }
                if (iData % 2 != 0) { ++iData; }
            }
        }

        return pixels;
    }

    // ══════════════════════════════════════════════════════
    //  内存清理
    // ══════════════════════════════════════════════════════
    private void ClearAll()
    {
        _isPlaying = false;
        _showList.Clear();
        _frameHeader = null;
        _images = null;
        if (_frameTex != null) { DestroyImmediate(_frameTex); _frameTex = null; }
        _statusMsg = null;
    }

    private void OnDestroy() => ClearAll();
}
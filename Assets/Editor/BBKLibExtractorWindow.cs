#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// BBK RPG Lib 提取工具 - Unity Editor 窗口
/// 菜单入口: Tools → BBK RPG Lib提取工具
/// </summary>
public class BBKLibExtractorWindow : EditorWindow
{
    // ── 状态 ────────────────────────────────────────────────────────────────

    private string _gamPath = "";
    private string _libPath = "";
    private string _gameTitle = "";
    private string _statusMsg = "请选择 .gam 文件";
    private bool _statusOk = true;

    // 样式缓存
    private GUIStyle _titleStyle;
    private GUIStyle _monoStyle;
    private bool _stylesReady;

    // ── 菜单 & 窗口 ──────────────────────────────────────────────────────────

    [MenuItem("工具/BBK RPG Lib提取工具")]
    public static void ShowWindow()
    {
        var win = GetWindow<BBKLibExtractorWindow>("BBK Lib提取");
        win.minSize = new Vector2(480, 280);
    }

    // ── GUI ─────────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        InitStyles();

        // ── 标题 ──
        EditorGUILayout.Space(8);
        GUILayout.Label("BBK RPG  .gam → .lib  提取工具", _titleStyle);
        EditorGUILayout.Space(4);
        DrawSeparator();

        // ── 输入文件 ──
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("输入文件 (.gam)", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        _gamPath = EditorGUILayout.TextField(_gamPath);
        if (GUILayout.Button("浏览…", GUILayout.Width(60)))
            BrowseGam();
        EditorGUILayout.EndHorizontal();

        // 游戏标题预览
        if (!string.IsNullOrEmpty(_gameTitle))
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(2);
            EditorGUILayout.LabelField("游戏标题:", GUILayout.Width(60));
            EditorGUILayout.LabelField(_gameTitle, EditorStyles.helpBox);
            EditorGUILayout.EndHorizontal();
        }

        // ── 输出文件 ──
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("输出文件 (.lib)", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        _libPath = EditorGUILayout.TextField(_libPath);
        if (GUILayout.Button("浏览…", GUILayout.Width(60)))
            BrowseLib();
        EditorGUILayout.EndHorizontal();

        // ── 格式信息 ──
        EditorGUILayout.Space(8);
        DrawFormatInfo();

        // ── 提取按钮 ──
        EditorGUILayout.Space(8);
        GUI.enabled = !string.IsNullOrEmpty(_gamPath);
        if (GUILayout.Button("▶  开始提取", GUILayout.Height(36)))
            DoExtract();
        GUI.enabled = true;

        // ── 状态栏 ──
        EditorGUILayout.Space(6);
        var msgType = _statusOk ? MessageType.Info : MessageType.Error;
        EditorGUILayout.HelpBox(_statusMsg, msgType);
    }

    // ── 操作方法 ─────────────────────────────────────────────────────────────

    private void BrowseGam()
    {
        string path = EditorUtility.OpenFilePanel("选择 BBK RPG .gam 文件", "", "gam");
        if (string.IsNullOrEmpty(path)) return;

        _gamPath = path;
        _libPath = Path.ChangeExtension(path, ".lib");

        // 预读标题
        _gameTitle = BBKLibExtractor.ReadGameTitle(path);
        bool valid = BBKLibExtractor.IsValidGamFile(path, out string reason);
        _statusOk = valid;
        _statusMsg = valid ? $"已选择文件，游戏: {_gameTitle}" : $"文件无效: {reason}";
        Repaint();
    }

    private void BrowseLib()
    {
        string defaultName = string.IsNullOrEmpty(_gamPath)
            ? "output"
            : Path.GetFileNameWithoutExtension(_gamPath);
        string path = EditorUtility.SaveFilePanel("保存 .lib 文件", "", defaultName, "lib");
        if (!string.IsNullOrEmpty(path))
        {
            _libPath = path;
            Repaint();
        }
    }

    private void DoExtract()
    {
        if (string.IsNullOrEmpty(_libPath))
            _libPath = Path.ChangeExtension(_gamPath, ".lib");

        var result = BBKLibExtractor.Extract(_gamPath, _libPath);

        _statusOk = result.Success;
        _statusMsg = result.Success
            ? $"✓ 提取成功！\n" +
              $"  游戏标题: {result.GameTitle}\n" +
              $"  GAM 大小: {result.GamSize:N0} bytes\n" +
              $"  LIB 偏移: 0x{result.LibOffset:X}  ({result.LibOffset:N0})\n" +
              $"  LIB 大小: {result.LibSize:N0} bytes\n" +
              $"  输出路径: {result.OutputPath}"
            : $"✗ 失败: {result.Message}";

        if (result.Success)
            Debug.Log($"[BBKLibExtractor] {_statusMsg}");
        else
            Debug.LogError($"[BBKLibExtractor] {_statusMsg}");

        // 刷新 Project 视图
        AssetDatabase.Refresh();
        Repaint();
    }

    // ── 辅助 UI ──────────────────────────────────────────────────────────────

    private void DrawFormatInfo()
    {
        var style = new GUIStyle(EditorStyles.helpBox)
        {
            fontSize = 10,
            richText = true,
            wordWrap = true
        };

        string info =
            "<b>BBK RPG .gam 格式</b>\n" +
            $"• [0x000] Magic \"GAM\\0\"\n" +
            $"• [0x006] 游戏标题 (GBK编码)\n" +
            $"• [0x{BBKLibExtractor.LIB_DATA_OFFSET:X}] LIB 数据开始（固定偏移）\n" +
            "• LIB = gam[0x48000 .. EOF] + 0x00";

        EditorGUILayout.LabelField(info, style, GUILayout.MinHeight(70));
    }

    private static void DrawSeparator()
    {
        var rect = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.4f));
    }

    private void InitStyles()
    {
        if (_stylesReady) return;

        _titleStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleCenter
        };

        _monoStyle = new GUIStyle(EditorStyles.label)
        {
            font = Font.CreateDynamicFontFromOSFont("Courier New", 11),
            wordWrap = true
        };

        _stylesReady = true;
    }
}
#endif
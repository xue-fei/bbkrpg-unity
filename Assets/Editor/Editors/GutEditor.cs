using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using I18N.CJK;

/// <summary>
/// GUT 脚本查看器/反编译器
/// </summary>
public class GutEditor : EditorWindow
{
    // ══════════════════════════════════════════════════════
    //  文件信息
    // ══════════════════════════════════════════════════════
    private string _filePath;
    private byte[] _fileData;          // 【修复】添加文件数据缓冲区
    private string _statusMsg;
    private int _fileType;
    private int _fileIndex;
    private int _scriptLength;
    private int _numSceneEvent;
    private int[] _sceneEventTable;

    // 解析后的指令列表
    private List<DisassembledInstruction> _instructions;

    // ══════════════════════════════════════════════════════
    //  显示选项
    // ══════════════════════════════════════════════════════
    private Vector2 _scrollPos;
    private bool _showHex = false;
    private bool _showJumpTable = true;
    private bool _showLabels = true;
    private bool _highlightCurrent = false;
    private int _currentInstruction = -1;
    private float _zoom = 1f;

    // 编码
    private static Encoding _gb2312;

    // ══════════════════════════════════════════════════════
    //  指令结构
    // ══════════════════════════════════════════════════════
    private class DisassembledInstruction
    {
        public int Offset;
        public int FileOffset;
        public byte Opcode;
        public string Mnemonic;
        public string[] Arguments;
        public string Disassembly;
        public bool IsLabelTarget;
        public string LabelName;
        public byte[] RawBytes;  // 原始字节（用于十六进制显示）
    }

    // ══════════════════════════════════════════════════════
    //  Opcode 映射表
    // ══════════════════════════════════════════════════════
    private static readonly Dictionary<byte, OpcodeInfo> _opcodeMap = new Dictionary<byte, OpcodeInfo>
    {
        { 0x00, new OpcodeInfo("Music", "NN") },
        { 0x01, new OpcodeInfo("LoadMap", "NNNN") },
        { 0x02, new OpcodeInfo("CreateActor", "NNN") },
        { 0x03, new OpcodeInfo("DeleteNpc", "N") },
        { 0x04, new OpcodeInfo("MapEvent", "NNNNNN") },
        { 0x05, new OpcodeInfo("ActorEvent", "NA") },
        { 0x06, new OpcodeInfo("Move", "NNN") },
        { 0x07, new OpcodeInfo("ActorMove", "NNNNNN") },
        { 0x08, new OpcodeInfo("ActorSpeed", "NN") },
        { 0x09, new OpcodeInfo("Callback", "") },
        { 0x0A, new OpcodeInfo("Goto", "A") },
        { 0x0B, new OpcodeInfo("If", "NA") },
        { 0x0C, new OpcodeInfo("Set", "NN") },
        { 0x0D, new OpcodeInfo("Say", "NC") },
        { 0x0E, new OpcodeInfo("StartChapter", "NN") },
        { 0x0F, new OpcodeInfo("ScreenR", "N") },
        { 0x10, new OpcodeInfo("ScreenS", "NN") },
        { 0x11, new OpcodeInfo("ScreenA", "N") },
        { 0x12, new OpcodeInfo("Event", "NA") },
        { 0x13, new OpcodeInfo("Money", "N") },
        { 0x14, new OpcodeInfo("Gameover", "") },
        { 0x15, new OpcodeInfo("IfCmp", "NNA") },
        { 0x16, new OpcodeInfo("Add", "NN") },
        { 0x17, new OpcodeInfo("Sub", "NN") },
        { 0x18, new OpcodeInfo("SetControlId", "N") },
        { 0x19, new OpcodeInfo("GutEvent", "NE") },
        { 0x1A, new OpcodeInfo("SetEvent", "N") },
        { 0x1B, new OpcodeInfo("ClrEvent", "N") },
        { 0x1C, new OpcodeInfo("Buy", "U") },
        { 0x1D, new OpcodeInfo("FaceToFace", "NN") },
        { 0x1E, new OpcodeInfo("Movie", "NNNNN") },
        { 0x1F, new OpcodeInfo("Choice", "CCA") },
        { 0x20, new OpcodeInfo("CreateBox", "NNNN") },
        { 0x21, new OpcodeInfo("DeleteBox", "N") },
        { 0x22, new OpcodeInfo("GainGoods", "NN") },
        { 0x23, new OpcodeInfo("InitFight", "NNNNNNNNNNN") },
        { 0x24, new OpcodeInfo("FightEnable", "") },
        { 0x25, new OpcodeInfo("FightDisenable", "") },
        { 0x26, new OpcodeInfo("CreateNpc", "NNNN") },
        { 0x27, new OpcodeInfo("EnterFight", "NNNNNNNNNNNNNAA") },
        { 0x28, new OpcodeInfo("DeleteActor", "N") },
        { 0x29, new OpcodeInfo("GainMoney", "L") },
        { 0x2A, new OpcodeInfo("UseMoney", "L") },
        { 0x2B, new OpcodeInfo("SetMoney", "L") },
        { 0x2C, new OpcodeInfo("LearnMagic", "NNN") },
        { 0x2D, new OpcodeInfo("Sale", "") },
        { 0x2E, new OpcodeInfo("NpcMoveMod", "NN") },
        { 0x2F, new OpcodeInfo("Message", "C") },
        { 0x30, new OpcodeInfo("DeleteGoods", "NNA") },
        { 0x31, new OpcodeInfo("ResumeActorHp", "NN") },
        { 0x32, new OpcodeInfo("ActorLayerUp", "NN") },
        { 0x33, new OpcodeInfo("BoxOpen", "N") },
        { 0x34, new OpcodeInfo("DelAllNpc", "") },
        { 0x35, new OpcodeInfo("NpcStep", "NNN") },
        { 0x36, new OpcodeInfo("SetSceneName", "C") },
        { 0x37, new OpcodeInfo("ShowSceneName", "") },
        { 0x38, new OpcodeInfo("ShowScreen", "") },
        { 0x39, new OpcodeInfo("UseGoods", "NNA") },
        { 0x3A, new OpcodeInfo("AttribTest", "NNNAA") },
        { 0x3B, new OpcodeInfo("AttribSet", "NNN") },
        { 0x3C, new OpcodeInfo("AttribAdd", "NNN") },
        { 0x3D, new OpcodeInfo("ShowGut", "NNC") },
        { 0x3E, new OpcodeInfo("UseGoodsNum", "NNNA") },
        { 0x3F, new OpcodeInfo("Randrade", "NA") },
        { 0x40, new OpcodeInfo("Menu", "NC") },
        { 0x41, new OpcodeInfo("TestMoney", "LA") },
        { 0x42, new OpcodeInfo("CallChapter", "NN") },
        { 0x43, new OpcodeInfo("DisCmp", "NNAA") },
        { 0x44, new OpcodeInfo("Return", "") },
        { 0x45, new OpcodeInfo("TimeMsg", "NC") },
        { 0x46, new OpcodeInfo("DisableSave", "") },
        { 0x47, new OpcodeInfo("EnableSave", "") },
        { 0x48, new OpcodeInfo("GameSave", "") },
        { 0x49, new OpcodeInfo("SetEventTimer", "NN") },
        { 0x4A, new OpcodeInfo("EnableShowPos", "") },
        { 0x4B, new OpcodeInfo("DisableShowPos", "") },
        { 0x4C, new OpcodeInfo("SetTo", "NN") },
        { 0x4D, new OpcodeInfo("TestGoodsNum", "NNNAA") },
    };

    private class OpcodeInfo
    {
        public string Name;
        public string ParamFormat;

        public OpcodeInfo(string name, string paramFormat)
        {
            Name = name;
            ParamFormat = paramFormat;
        }
    }

    // ══════════════════════════════════════════════════════
    //  菜单入口
    // ══════════════════════════════════════════════════════
    [MenuItem("工具/GUT 脚本查看器")]
    static void Init()
    {
        _gb2312 = _gb2312 ?? new CP936();
        GetWindow<GutEditor>("GUT 脚本查看器");
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
        }

        if (_instructions != null && _instructions.Count > 0)
        {
            DrawInstructionList();
        }
        else if (_filePath == null)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("← 点击打开.gut 文件加载脚本", EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
        }
    }

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        if (GUILayout.Button("打开 .gut", EditorStyles.toolbarButton, GUILayout.Width(80)))
            OpenFile();

        GUILayout.Space(6);

        EditorGUI.BeginDisabledGroup(_instructions == null);

        bool hex2 = GUILayout.Toggle(_showHex, "十六进制", EditorStyles.toolbarButton, GUILayout.Width(60));
        bool lbl2 = GUILayout.Toggle(_showLabels, "标签", EditorStyles.toolbarButton, GUILayout.Width(40));
        bool jmp2 = GUILayout.Toggle(_showJumpTable, "跳转表", EditorStyles.toolbarButton, GUILayout.Width(60));

        if (hex2 != _showHex || lbl2 != _showLabels || jmp2 != _showJumpTable)
        {
            _showHex = hex2;
            _showLabels = lbl2;
            _showJumpTable = jmp2;
            Repaint();
        }

        GUILayout.Space(4);

        bool cur2 = GUILayout.Toggle(_highlightCurrent, "调试", EditorStyles.toolbarButton, GUILayout.Width(40));
        if (cur2 != _highlightCurrent)
        {
            _highlightCurrent = cur2;
            Repaint();
        }

        EditorGUI.BeginDisabledGroup(!_highlightCurrent);
        if (GUILayout.Button("◀", EditorStyles.toolbarButton, GUILayout.Width(24)))
            StepInstruction(-1);
        if (GUILayout.Button("▶", EditorStyles.toolbarButton, GUILayout.Width(24)))
            StepInstruction(1);
        EditorGUI.EndDisabledGroup();

        EditorGUI.EndDisabledGroup();

        GUILayout.FlexibleSpace();

        GUILayout.Label("缩放", GUILayout.Width(30));
        _zoom = EditorGUILayout.Slider(_zoom, 0.5f, 2f, GUILayout.Width(100));

        EditorGUILayout.EndHorizontal();
    }

    private void DrawInfoBar()
    {
        if (_filePath == null) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("文件", Path.GetFileName(_filePath));
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"Type={_fileType}  Index={_fileIndex}", GUILayout.Width(150));
        EditorGUILayout.LabelField($"脚本长度={_scriptLength}  跳转表={_numSceneEvent} 项", GUILayout.Width(200));
        EditorGUILayout.LabelField($"指令数={(_instructions != null ? _instructions.Count : 0)}", GUILayout.Width(100));
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    private void DrawInstructionList()
    {
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        if (_showJumpTable && _sceneEventTable != null && _sceneEventTable.Length > 0)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("跳转表 (GutEvent)", EditorStyles.boldLabel);

            for (int i = 0; i < _sceneEventTable.Length; i++)
            {
                int slot = i + 1;
                int offset = _sceneEventTable[i];
                string target = offset > 0 ? FindLabelByOffset(offset) : "未使用";

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label($"槽{slot:D3}", GUILayout.Width(50));
                GUILayout.Label($"偏移={offset:D5}", GUILayout.Width(80));
                GUILayout.Label($"→ {target}", GUILayout.Width(150));
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("偏移", GUILayout.Width(60));
        if (_showLabels) GUILayout.Label("标签", GUILayout.Width(80));
        GUILayout.Label("指令", GUILayout.Width(120));
        GUILayout.Label("参数", GUILayout.Width(200));
        if (_showHex) GUILayout.Label("原始字节", GUILayout.Width(150));
        EditorGUILayout.EndHorizontal();

        for (int i = 0; i < _instructions.Count; i++)
        {
            var instr = _instructions[i];

            if (_highlightCurrent && i == _currentInstruction)
            {
                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.8f, 0f);
                EditorGUILayout.BeginHorizontal("box");
                GUI.backgroundColor = oldBg;
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
            }

            string offsetStr = _showHex ? $"0x{instr.Offset:X4}" : $"{instr.Offset:D5}";
            if (GUILayout.Button(offsetStr, EditorStyles.miniButton, GUILayout.Width(60)))
            {
                _currentInstruction = i;
                _highlightCurrent = true;
            }

            if (_showLabels)
            {
                if (instr.IsLabelTarget)
                {
                    GUI.color = new Color(0f, 0.8f, 1f);
                    GUILayout.Label($"{instr.LabelName}:", GUILayout.Width(80));
                    GUI.color = Color.white;
                }
                else
                {
                    GUILayout.Label("", GUILayout.Width(80));
                }
            }

            GUI.color = GetOpcodeColor(instr.Opcode);
            GUILayout.Label(instr.Mnemonic, GUILayout.Width(120));
            GUI.color = Color.white;

            GUILayout.Label(instr.Disassembly, GUILayout.Width(400));

            if (_showHex)
            {
                GUILayout.Label(GetHexRepresentation(instr), GUILayout.Width(150));
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.EndScrollView();
    }

    private void OpenFile()
    {
        string path = EditorUtility.OpenFilePanelWithFilters(
            "选择 GUT 脚本文件", "ExRes/gut",
            new[] { "GUT 文件", "gut", "所有文件", "*" }
        );
        if (string.IsNullOrEmpty(path)) return;

        ClearAll();
        _filePath = path;
        _statusMsg = null;

        byte[] buf;
        try { buf = File.ReadAllBytes(path); }
        catch (System.Exception e)
        {
            _statusMsg = $"读取失败：{e.Message}";
            Repaint();
            return;
        }

        _fileData = buf;  // 【修复】保存文件数据

        if (!ParseGut(buf))
        {
            Repaint();
            return;
        }

        Disassemble();
        Repaint();
    }

    private bool ParseGut(byte[] buf)
    {
        if (buf.Length < 0x18)
        {
            _statusMsg = $"文件过小（{buf.Length} 字节）";
            return false;
        }

        _fileType = buf[0];
        _fileIndex = buf[1];
        _scriptLength = buf[0x18] | (buf[0x19] << 8);
        _numSceneEvent = buf[0x1A];

        int expectedMinLen = 0x1B + _numSceneEvent * 2;
        if (buf.Length < expectedMinLen)
        {
            _statusMsg = $"文件长度不足（期望至少{expectedMinLen}，实际{buf.Length}）";
            return false;
        }

        _sceneEventTable = new int[_numSceneEvent];
        for (int i = 0; i < _numSceneEvent; i++)
        {
            int offset = 0x1B + i * 2;
            _sceneEventTable[i] = buf[offset] | (buf[offset + 1] << 8);
        }

        return true;
    }

    private void Disassemble()
    {
        _instructions = new List<DisassembledInstruction>();

        int scriptStart = 0x1B + _numSceneEvent * 2;

        var labelTargets = new HashSet<int>();
        foreach (int target in _sceneEventTable)
        {
            if (target > 0)
                labelTargets.Add(target);
        }

        // 第一遍：收集跳转目标
        int ptr = scriptStart;
        int scriptEnd = 0x18 + _scriptLength;

        while (ptr < scriptEnd && ptr < _fileData.Length)  // 【修复】使用_fileData
        {
            int instrOffset = ptr - scriptStart;

            if (!_opcodeMap.TryGetValue(_fileData[ptr], out OpcodeInfo opInfo))  // 【修复】
            {
                ptr++;
                continue;
            }

            CheckJumpTargets(ptr + 1, opInfo.ParamFormat, labelTargets, scriptStart);
            ptr += 1 + GetInstructionLength(opInfo.ParamFormat, ptr + 1, scriptEnd);
        }

        // 第二遍：生成指令列表
        ptr = scriptStart;
        int labelCounter = 0;
        var labelMap = new Dictionary<int, string>();

        while (ptr < scriptEnd && ptr < _fileData.Length)  // 【修复】
        {
            int instrOffset = ptr - scriptStart;
            int fileOffset = ptr;
            byte opcode = _fileData[ptr];  // 【修复】

            if (!_opcodeMap.TryGetValue(opcode, out OpcodeInfo opInfo))
            {
                ptr++;
                continue;
            }

            var instr = new DisassembledInstruction
            {
                Offset = instrOffset,
                FileOffset = fileOffset,
                Opcode = opcode,
                Mnemonic = opInfo.Name,
                IsLabelTarget = labelTargets.Contains(instrOffset)
            };

            if (instr.IsLabelTarget)
            {
                if (!labelMap.TryGetValue(instrOffset, out string labelName))
                {
                    labelName = $"label_{labelCounter++:D4}";
                    labelMap[instrOffset] = labelName;
                }
                instr.LabelName = labelName;
            }

            ParseInstructionParameters(ptr + 1, opInfo.ParamFormat, instr, labelMap, scriptStart);
            _instructions.Add(instr);

            ptr += 1 + GetInstructionLength(opInfo.ParamFormat, ptr + 1, scriptEnd);
        }
    }

    private void CheckJumpTargets(int ptr, string paramFormat, HashSet<int> labelTargets, int scriptStart)
    {
        foreach (char c in paramFormat)
        {
            if (ptr >= _fileData.Length) break;  // 【修复】

            switch (c)
            {
                case 'N':
                    ptr += 2;
                    break;
                case 'L':
                    ptr += 4;
                    break;
                case 'A':
                    if (ptr + 1 < _fileData.Length)  // 【修复】
                    {
                        int addr = _fileData[ptr] | (_fileData[ptr + 1] << 8);  // 【修复】
                        int targetOffset = addr - (2 + 1 + _numSceneEvent * 2);
                        if (targetOffset >= 0)
                            labelTargets.Add(targetOffset);
                    }
                    ptr += 2;
                    break;
                case 'C':
                    while (ptr < _fileData.Length && _fileData[ptr] != 0)  // 【修复】
                        ptr++;
                    ptr++;
                    break;
                case 'E':
                    ptr += 2;
                    break;
                case 'U':
                    while (ptr + 1 < _fileData.Length)  // 【修复】
                    {
                        int val = _fileData[ptr] | (_fileData[ptr + 1] << 8);  // 【修复】
                        ptr += 2;
                        if (val == 0) break;
                    }
                    break;
            }
        }
    }

    private void ParseInstructionParameters(int ptr, string paramFormat,
        DisassembledInstruction instr, Dictionary<int, string> labelMap, int scriptStart)
    {
        var args = new List<string>();
        int startPtr = ptr;
        var rawBytes = new List<byte> { instr.Opcode };  // 保存原始字节

        foreach (char c in paramFormat)
        {
            if (ptr >= _fileData.Length) break;  // 【修复】

            switch (c)
            {
                case 'N':
                    int valN = _fileData[ptr] | (_fileData[ptr + 1] << 8);  // 【修复】
                    args.Add(valN.ToString());
                    rawBytes.Add(_fileData[ptr]);  // 【修复】
                    rawBytes.Add(_fileData[ptr + 1]);  // 【修复】
                    ptr += 2;
                    break;
                case 'L':
                    int valL = _fileData[ptr] | (_fileData[ptr + 1] << 8) |
                               (_fileData[ptr + 2] << 16) | (_fileData[ptr + 3] << 24);  // 【修复】
                    args.Add(valL.ToString());
                    for (int i = 0; i < 4; i++) rawBytes.Add(_fileData[ptr + i]);  // 【修复】
                    ptr += 4;
                    break;
                case 'A':
                    int addr = _fileData[ptr] | (_fileData[ptr + 1] << 8);  // 【修复】
                    int targetOffset = addr - (2 + 1 + _numSceneEvent * 2);
                    string label = labelMap.TryGetValue(targetOffset, out string l) ? l : $"0x{addr:X4}";
                    args.Add(label);
                    rawBytes.Add(_fileData[ptr]);  // 【修复】
                    rawBytes.Add(_fileData[ptr + 1]);  // 【修复】
                    ptr += 2;
                    break;
                case 'C':
                    var sb = new StringBuilder();
                    while (ptr < _fileData.Length && _fileData[ptr] != 0)  // 【修复】
                    {
                        sb.Append((char)_fileData[ptr]);  // 【修复】显式转换
                        rawBytes.Add(_fileData[ptr]);  // 【修复】
                        ptr++;
                    }
                    rawBytes.Add(0);  // 终止符
                    ptr++;
                    try
                    {
                        byte[] bytes = new byte[sb.Length];
                        for (int i = 0; i < sb.Length; i++)
                            bytes[i] = (byte)sb[i];
                        string decoded = _gb2312.GetString(bytes);
                        args.Add($"\"{decoded}\"");
                    }
                    catch
                    {
                        args.Add($"\"{sb.ToString()}\"");
                    }
                    break;
                case 'E':
                    int evtIdx = _fileData[ptr] | (_fileData[ptr + 1] << 8);  // 【修复】
                    args.Add($"Event[{evtIdx}]");
                    rawBytes.Add(_fileData[ptr]);  // 【修复】
                    rawBytes.Add(_fileData[ptr + 1]);  // 【修复】
                    ptr += 2;
                    break;
                case 'U':
                    var buyList = new List<string>();
                    while (ptr + 1 < _fileData.Length)  // 【修复】
                    {
                        int buyVal = _fileData[ptr] | (_fileData[ptr + 1] << 8);  // 【修复】
                        rawBytes.Add(_fileData[ptr]);  // 【修复】
                        rawBytes.Add(_fileData[ptr + 1]);  // 【修复】
                        ptr += 2;
                        if (buyVal == 0) break;
                        buyList.Add(buyVal.ToString());
                    }
                    args.Add($"[{string.Join(", ", buyList)}]");
                    break;
            }
        }

        instr.Arguments = args.ToArray();
        instr.Disassembly = string.Join(" ", args);
        instr.FileOffset = startPtr - 1;
        instr.RawBytes = rawBytes.ToArray();
    }

    private int GetInstructionLength(string paramFormat, int ptr, int maxPtr)
    {
        int len = 0;
        foreach (char c in paramFormat)
        {
            if (ptr + len >= maxPtr || ptr + len >= _fileData.Length) break;  // 【修复】

            switch (c)
            {
                case 'N': len += 2; break;
                case 'L': len += 4; break;
                case 'A': len += 2; break;
                case 'E': len += 2; break;
                case 'C':
                    while (ptr + len < maxPtr && ptr + len < _fileData.Length && _fileData[ptr + len] != 0)  // 【修复】
                        len++;
                    len++;
                    break;
                case 'U':
                    while (ptr + len + 1 < maxPtr && ptr + len + 1 < _fileData.Length)  // 【修复】
                    {
                        int val = _fileData[ptr + len] | (_fileData[ptr + len + 1] << 8);  // 【修复】
                        len += 2;
                        if (val == 0) break;
                    }
                    break;
            }
        }
        return len;
    }

    private string GetHexRepresentation(DisassembledInstruction instr)
    {
        if (instr.RawBytes == null || instr.RawBytes.Length == 0)
            return $"0x{instr.Opcode:X2}";

        var sb = new StringBuilder();
        foreach (byte b in instr.RawBytes)
        {
            if (sb.Length > 0) sb.Append(" ");
            sb.Append($"{b:X2}");
        }
        return sb.ToString();
    }

    private string FindLabelByOffset(int fileOffset)
    {
        if (_instructions == null) return "未知";

        int scriptStart = 0x1B + _numSceneEvent * 2;
        int instrOffset = fileOffset - scriptStart;

        foreach (var instr in _instructions)
        {
            if (instr.Offset == instrOffset)
                return instr.IsLabelTarget ? instr.LabelName : $"偏移{instrOffset}";
        }

        return $"偏移{instrOffset}";
    }

    private Color GetOpcodeColor(byte opcode)
    {
        if (opcode >= 0x0A && opcode <= 0x0B)
            return new Color(1f, 0.5f, 0f);
        if (opcode >= 0x15 && opcode <= 0x15)
            return new Color(1f, 0.3f, 0.3f);
        if (opcode >= 0x2F && opcode <= 0x2F)
            return new Color(0.3f, 1f, 0.3f);
        return Color.white;
    }

    private void StepInstruction(int delta)
    {
        if (_instructions == null || _instructions.Count == 0) return;

        _currentInstruction += delta;
        if (_currentInstruction < 0) _currentInstruction = 0;
        if (_currentInstruction >= _instructions.Count) _currentInstruction = _instructions.Count - 1;

        Repaint();
    }

    private void ClearAll()
    {
        _filePath = null;
        _fileData = null;  // 【修复】清理文件数据
        _statusMsg = null;
        _instructions = null;
        _sceneEventTable = null;
        _currentInstruction = -1;
        _highlightCurrent = false;
    }

    private void OnDestroy()
    {
        ClearAll();
    }
}
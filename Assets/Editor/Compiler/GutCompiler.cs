using I18N.CJK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// GUT 脚本编译器：将 .txt 脚本编译为 .gut 二进制文件
///
/// 完整 opcode 表（来自 ScriptProcess.cs CommandFactory）：
///   0x00 MUSIC           0参数
///   0x01 loadmap         4×u16
///   0x02 CREATEACTOR     3×u16
///   0x03 DELETENPC       1×u16
///   0x06 MOVE            3×u16
///   0x09 callback        0参数
///   0x0A goto            1×u16（字节地址）
///   0x0B if              参数可变（见EmitIf）
///   0x0C set             2×u16
///   0x0D say             1×u16 + GB2312字符串+\0
///   0x0E STARTCHAPTER    2×u16
///   0x10 SCREENSET       参数可变
///   0x14 GAMEOVER        0参数
///   0x15 IFCMP           参数可变
///   0x16 ATTRIBADD       3×u16
///   0x17 ATTRIBSUB       3×u16
///   0x1A setevent        1×u16
///   0x1B CLEAREVENT      1×u16
///   0x1C BUY             0参数
///   0x1D FACETOFACE      2×u16
///   0x1E MOVIE           5×u16
///   0x1F CHOICE          参数可变（字符串列表）
///   0x20 CREATEBOX       4×u16
///   0x21 DELETEBOX       1×u16
///   0x22 GAINGOODS       3×u16
///   0x23 INITFIGHT       参数可变
///   0x24 FIGHTENABLE     0参数
///   0x25 FIGHTDISENABLE  0参数
///   0x26 CREATENPC       4×u16
///   0x27 ENTERFIGHT      参数可变
///   0x28 DELETECHARACTER 1×u16
///   0x29 GAINMONEY       1×u32
///   0x2A USEMONEY        1×u32
///   0x2B SETMONEY        1×u32
///   0x2C LEARNMAGIC      3×u16
///   0x2D SALE            0参数
///   0x2E NPCMOVEMOD      2×u16
///   0x2F MESSAGE         GB2312字符串+\0
///   0x30 DELETEGOODS     2×u16
///   0x31 RESTOREHP       1×u16
///   0x32 ACTORLAYERUP    0参数
///   0x33 BOXOPEN         1×u16
///   0x34 DELALLNPC       0参数
///   0x35 NPCSTEP         3×u16
///   0x36 SETSCENENAME    GB2312字符串+\0
///   0x37 SHOWSCENENAME   0参数
///   0x38 SHOWSCREEN      0参数
///   0x39 USEGOODS        2×u16
///   0x3A ATTRIBTEST      5×u16（伏魔记未用）
///   0x3B ATTRIBSET       3×u16（伏魔记未用）
///   0x3C ATTRIBADD       3×u16（伏魔记未用，同0x16?）
///   0x3D SHOWGUT         3×u16 + GB2312字符串+\0
///   0x3E USEGOODSNUM     3×u16
///   0x3F RANDRADE        2×u16
///   0x40 MENU            参数可变（字符串列表）
///   0x41 TESTMONEY       3×u16
///   0x42 CALLCHAPTER     2×u16（伏魔记未用）
///   0x43 DISCMP          3×u16
///   0x44 return          0参数
///   0x45 TIMEMSG         参数可变（伏魔记未用）
///   0x46 DISABLESAVE     0参数
///   0x47 ENABLESAVE      0参数
///   0x48 GAMESAVE        0参数（伏魔记未用）
///   0x49 SETEVENTTIMER   参数可变（伏魔记未用）
///   0x4A ENABLESHOWPOS   0参数
///   0x4B DISABLESHOWPOS  0参数
///   0x4C SETTO           3×u16
///   0x4D TESTGOODSNUM    4×u16
///   0x4E SETFIGHTMISS    2×u16（未实现）
///   0x4F SETARMSTOSS     2×u16（未实现）
/// </summary>
public class GutCompiler
{
    static Encoding gb2312 = null;

    [MenuItem("工具/编译GUT脚本")]
    public static void CompileAll()
    {
        gb2312 = new CP936();

        string srcDir = Application.dataPath + "/../ExRes/gut_src";
        string outDir = Application.dataPath + "/../ExRes/gut_test";

        if (!Directory.Exists(srcDir))
        {
            Debug.LogError($"脚本源目录不存在: {srcDir}");
            return;
        }
        if (!Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);

        int ok = 0, fail = 0;
        foreach (string txtPath in Directory.GetFiles(srcDir, "*.txt"))
        {
            string name = Path.GetFileNameWithoutExtension(txtPath);
            string outPath = Path.Combine(outDir, name + ".gut");
            try
            {
                CompileFile(txtPath, outPath);
                Debug.Log($"[GutCompiler] ✓ {name}.gut");
                ok++;
            }
            catch (Exception e)
            {
                Debug.LogError($"[GutCompiler] ✗ {name}: {e.Message}");
                fail++;
            }
        }
        Debug.Log($"[GutCompiler] 完成：成功 {ok}，失败 {fail}");
    }

    // ── 单文件编译入口 ─────────────────────────────────────
    public static void CompileFile(string txtPath, string outPath)
    {
        gb2312 = gb2312 ?? new CP936();

        string stem = Path.GetFileNameWithoutExtension(txtPath);
        var parts = stem.Split('-');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out int fileType)
            || !int.TryParse(parts[1], out int fileIndex))
            throw new Exception($"文件名格式错误，应为 type-index.txt，实际: {stem}");

        string[] lines = File.ReadAllLines(txtPath, gb2312);

        // ── 第一遍：删除 @开头的注释 ─────────────── 
        var sceneEvents = new List<int>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            string t = line.Trim();
            if (t.StartsWith("@"))
            {
                lines[i] = "";
            }
        }

        // ── 第二遍：编译指令字节流（两遍处理 goto/if 标签） ──
        // Pass A：收集标签位置
        var labelPos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Pass B：生成字节，goto/if 先填占位符，最后回填
        var script = new List<byte>();
        // 记录需要回填的位置：(scriptByteOffset, labelName)
        var patchList = new List<(int patchOffset, string label)>();

        int lineNum = 0;
        foreach (string rawLine in lines)
        {
            lineNum++;
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("@")) continue;

            // 标签行（如 "chapnext:"）
            if (Regex.IsMatch(line, @"^\w+:$"))
            {
                string label = line.TrimEnd(':');
                labelPos[label] = script.Count;
                continue;
            }

            ParseLine(line, out string cmd, out string[] args);
            EmitInstruction(script, patchList, labelPos, cmd, args, lineNum, line);
        }

        // 回填 goto/if 中的标签地址（u16 LE）
        foreach (var (patchOffset, label) in patchList)
        {
            if (!labelPos.TryGetValue(label, out int addr))
                throw new Exception($"未定义的标签: {label}");
            // ScriptExecutor 中地址是相对 ScriptData 起始的字节偏移
            // 但实际存储的是相对于 Length 字段定义的段偏移，加上 NumSceneEvent*2+3
            int rawAddr = addr + sceneEvents.Count * 2 + 3;
            script[patchOffset] = (byte)(rawAddr & 0xFF);
            script[patchOffset + 1] = (byte)((rawAddr >> 8) & 0xFF);
        }

        // ── 组装 GUT 文件头 ───────────────────────────────────
        byte[] scriptBytes = script.ToArray();
        int length = sceneEvents.Count * 2 + 3 + scriptBytes.Length;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((byte)fileType);
        w.Write((byte)fileIndex);
        w.Write((byte)(length & 0xFF));                 // Length lo
        w.Write((byte)((length >> 8) & 0xFF));          // Length hi
        w.Write((byte)sceneEvents.Count);               // NumSceneEvent
        foreach (int ev in sceneEvents)                 // SceneEvent[]
        {
            w.Write((byte)(ev & 0xFF));
            w.Write((byte)((ev >> 8) & 0xFF));
        }
        w.Write(scriptBytes);

        File.WriteAllBytes(outPath, ms.ToArray());
    }

    // ── 指令分发 ──────────────────────────────────────────
    static void EmitInstruction(List<byte> buf,
        List<(int, string)> patchList,
        Dictionary<string, int> labelPos,
        string cmd, string[] args, int lineNum, string line)
    {
        switch (cmd.ToLowerInvariant())
        {
            // ── 0参数指令 ────────────────────────────────────
            case "music": buf.Add(0x00); break;
            case "callback": buf.Add(0x09); break;
            case "gameover": buf.Add(0x14); break;
            case "buy": buf.Add(0x1C); break;
            case "fightenable": buf.Add(0x24); break;
            case "fightdisenable": buf.Add(0x25); break;
            case "sale": buf.Add(0x2D); break;
            case "actorlayerup": buf.Add(0x32); break;
            case "delallnpc": buf.Add(0x34); break;
            case "showscenename": buf.Add(0x37); break;
            case "showscreen": buf.Add(0x38); break;
            case "return": buf.Add(0x44); break;
            case "disablesave": buf.Add(0x46); break;
            case "enablesave": buf.Add(0x47); break;
            case "gamesave": buf.Add(0x48); break;
            case "enableshowpos": buf.Add(0x4A); break;
            case "disableshowpos": buf.Add(0x4B); break;

            // ── N×u16 定长指令 ───────────────────────────────
            case "loadmap": Emit(buf, 0x01, args, 4, lineNum, line); break;
            case "createactor": Emit(buf, 0x02, args, 3, lineNum, line); break;
            case "deletenpc": Emit(buf, 0x03, args, 1, lineNum, line); break;
            case "move": Emit(buf, 0x06, args, 3, lineNum, line); break;
            case "set": Emit(buf, 0x0C, args, 2, lineNum, line); break;
            case "startchapter": Emit(buf, 0x0E, args, 2, lineNum, line); break;
            case "screenset": Emit(buf, 0x10, args, 2, lineNum, line); break;
            case "attribadd": Emit(buf, 0x16, args, 3, lineNum, line); break;
            case "attribsub": Emit(buf, 0x17, args, 3, lineNum, line); break;
            case "setevent": Emit(buf, 0x1A, args, 1, lineNum, line); break;
            case "clearevent": Emit(buf, 0x1B, args, 1, lineNum, line); break;
            case "facetoface": Emit(buf, 0x1D, args, 2, lineNum, line); break;
            case "movie": Emit(buf, 0x1E, args, 5, lineNum, line); break;
            case "createbox": Emit(buf, 0x20, args, 4, lineNum, line); break;
            case "deletebox": Emit(buf, 0x21, args, 1, lineNum, line); break;
            case "gaingoods": Emit(buf, 0x22, args, 3, lineNum, line); break;
            case "createnpc": Emit(buf, 0x26, args, 4, lineNum, line); break;
            case "deletecharacter": Emit(buf, 0x28, args, 1, lineNum, line); break;
            case "learnmagic": Emit(buf, 0x2C, args, 3, lineNum, line); break;
            case "npcmovemod": Emit(buf, 0x2E, args, 2, lineNum, line); break;
            case "deletegoods": Emit(buf, 0x30, args, 2, lineNum, line); break;
            case "restorehp": Emit(buf, 0x31, args, 1, lineNum, line); break;
            case "boxopen": Emit(buf, 0x33, args, 1, lineNum, line); break;
            case "npcstep": Emit(buf, 0x35, args, 3, lineNum, line); break;
            case "usegoods": Emit(buf, 0x39, args, 2, lineNum, line); break;
            case "attribtest": Emit(buf, 0x3A, args, 5, lineNum, line); break;
            case "attribset": Emit(buf, 0x3B, args, 3, lineNum, line); break;
            case "usegoodsnum": Emit(buf, 0x3E, args, 3, lineNum, line); break;
            case "randrade": Emit(buf, 0x3F, args, 2, lineNum, line); break;
            case "testmoney": Emit(buf, 0x41, args, 3, lineNum, line); break;
            case "callchapter": Emit(buf, 0x42, args, 2, lineNum, line); break;
            case "discmp": Emit(buf, 0x43, args, 3, lineNum, line); break;
            case "setto": Emit(buf, 0x4C, args, 3, lineNum, line); break;
            case "testgoodsnum": Emit(buf, 0x4D, args, 4, lineNum, line); break;
            case "setfightmiss": Emit(buf, 0x4E, args, 2, lineNum, line); break;
            case "setarmstoss": Emit(buf, 0x4F, args, 2, lineNum, line); break;

            // ── u32 参数指令 ─────────────────────────────────
            case "gainmoney": EmitU32(buf, 0x29, args, lineNum, line); break;
            case "usemoney": EmitU32(buf, 0x2A, args, lineNum, line); break;
            case "setmoney": EmitU32(buf, 0x2B, args, lineNum, line); break;

            // ── 带字符串指令 ─────────────────────────────────
            case "say": EmitSay(buf, args, lineNum, line); break;
            case "message": EmitStringOnly(buf, 0x2F, args, lineNum, line); break;
            case "setscenename": EmitStringOnly(buf, 0x36, args, lineNum, line); break;

            // ── SHOWGUT: 3×u16 + 字符串 ──────────────────────
            case "showgut": EmitShowGut(buf, args, lineNum, line); break;

            // ── 跳转指令（需要回填标签）────────────────────────
            case "goto": EmitGoto(buf, patchList, labelPos, args, lineNum, line); break;

            // ── INITFIGHT / ENTERFIGHT：变长参数 ─────────────
            case "initfight": EmitVarU16(buf, 0x23, args, lineNum, line); break;
            case "enterfight": EmitVarU16(buf, 0x27, args, lineNum, line); break;

            // ── MENU / CHOICE：count + 字符串列表 ─────────────
            case "menu": EmitStringList(buf, 0x40, args, lineNum, line); break;
            case "choice": EmitStringList(buf, 0x1F, args, lineNum, line); break;

            // ── IF / IFCMP：条件跳转，带标签回填 ─────────────
            case "if": EmitIf(buf, patchList, labelPos, args, lineNum, line); break;
            case "ifcmp": EmitIfCmp(buf, patchList, labelPos, args, lineNum, line); break;

            default:
                Debug.LogWarning($"[GutCompiler] 第{lineNum}行：未知指令 '{cmd}'，跳过: {line}");
                break;
        }
    }

    // ── 通用 N×u16 ────────────────────────────────────────
    static void Emit(List<byte> buf, byte op, string[] args, int n, int lineNum, string line)
    {
        if (args.Length < n)
            throw new Exception($"第{lineNum}行：{line} 需要{n}个参数，实际{args.Length}个");
        buf.Add(op);
        for (int i = 0; i < n; i++) WriteU16(buf, ParseInt(args[i], lineNum, line));
    }

    // ── 变长 u16（参数个数由 args.Length 决定）──────────────
    static void EmitVarU16(List<byte> buf, byte op, string[] args, int lineNum, string line)
    {
        buf.Add(op);
        WriteU16(buf, args.Length); // 先写参数个数
        foreach (var a in args) WriteU16(buf, ParseInt(a, lineNum, line));
    }

    // ── u32 参数 ──────────────────────────────────────────
    static void EmitU32(List<byte> buf, byte op, string[] args, int lineNum, string line)
    {
        if (args.Length < 1)
            throw new Exception($"第{lineNum}行：{line} 需要1个参数");
        buf.Add(op);
        WriteU32(buf, ParseInt(args[0], lineNum, line));
    }

    // ── SAY：opcode + u16 actor + 字符串+\0 ─────────────────
    static void EmitSay(List<byte> buf, string[] args, int lineNum, string line)
    {
        if (args.Length < 2)
            throw new Exception($"第{lineNum}行：say 需要 actorId 和文本: {line}");
        buf.Add(0x0D);
        WriteU16(buf, ParseInt(args[0], lineNum, line));
        WriteGBString(buf, args[1]);
    }

    // ── 纯字符串指令：opcode + 字符串+\0 ────────────────────
    static void EmitStringOnly(List<byte> buf, byte op, string[] args, int lineNum, string line)
    {
        if (args.Length < 1)
            throw new Exception($"第{lineNum}行：{line} 需要字符串参数");
        buf.Add(op);
        WriteGBString(buf, args[0]);
    }

    // ── SHOWGUT：opcode + 3×u16 + 字符串+\0 ─────────────────
    static void EmitShowGut(List<byte> buf, string[] args, int lineNum, string line)
    {
        if (args.Length < 4)
            throw new Exception($"第{lineNum}行：SHOWGUT 需要3个数字参数和1个字符串: {line}");
        buf.Add(0x3D);
        for (int i = 0; i < 3; i++) WriteU16(buf, ParseInt(args[i], lineNum, line));
        WriteGBString(buf, args[3]);
    }

    // ── GOTO：opcode + u16 地址（回填）──────────────────────
    static void EmitGoto(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, string[] args, int lineNum, string line)
    {
        if (args.Length < 1)
            throw new Exception($"第{lineNum}行：goto 需要标签参数: {line}");
        buf.Add(0x0A);
        string label = args[0];
        if (labelPos.TryGetValue(label, out int addr))
        {
            WriteU16(buf, addr); // 前向标签已知，直接写
        }
        else
        {
            patchList.Add((buf.Count, label)); // 后向标签，占位
            WriteU16(buf, 0);
        }
    }

    // ── IF：opcode + var1 + var2 + op + lowLabel + highLabel ─
    // TXT格式: if var1 cmpOp var2 lowLabel highLabel
    // 暂用简化格式: if varIdx cmpVal lowLabel highLabel
    static void EmitIf(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, string[] args, int lineNum, string line)
    {
        if (args.Length < 4)
            throw new Exception($"第{lineNum}行：if 需要4个参数: varIdx val lowLabel highLabel");
        buf.Add(0x0B);
        WriteU16(buf, ParseInt(args[0], lineNum, line)); // varIdx
        WriteU16(buf, ParseInt(args[1], lineNum, line)); // val
        EmitLabel(buf, patchList, labelPos, args[2]);   // lowLabel
        EmitLabel(buf, patchList, labelPos, args[3]);   // highLabel
    }

    // ── IFCMP：opcode + var1 + var2 + lowLabel + highLabel ───
    // TXT格式: IFCMP var1 var2 lowLabel highLabel
    static void EmitIfCmp(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, string[] args, int lineNum, string line)
    {
        if (args.Length < 4)
            throw new Exception($"第{lineNum}行：ifcmp 需要4个参数: var1 var2 lowLabel highLabel");
        buf.Add(0x15);
        WriteU16(buf, ParseInt(args[0], lineNum, line));
        WriteU16(buf, ParseInt(args[1], lineNum, line));
        EmitLabel(buf, patchList, labelPos, args[2]);
        EmitLabel(buf, patchList, labelPos, args[3]);
    }

    // ── MENU/CHOICE：opcode + count×u16 + count个字符串+\0 ──
    // TXT格式: menu "选项1" "选项2" ...
    static void EmitStringList(List<byte> buf, byte op, string[] args, int lineNum, string line)
    {
        buf.Add(op);
        WriteU16(buf, args.Length);
        foreach (var s in args) WriteGBString(buf, s);
    }

    // ── 标签辅助：写地址或占位 ───────────────────────────────
    static void EmitLabel(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, string label)
    {
        if (labelPos.TryGetValue(label, out int addr))
            WriteU16(buf, addr);
        else
        {
            patchList.Add((buf.Count, label));
            WriteU16(buf, 0);
        }
    }

    // ── 底层写入 ──────────────────────────────────────────
    static void WriteU16(List<byte> buf, int v)
    {
        buf.Add((byte)(v & 0xFF));
        buf.Add((byte)((v >> 8) & 0xFF));
    }

    static void WriteU32(List<byte> buf, int v)
    {
        buf.Add((byte)(v & 0xFF));
        buf.Add((byte)((v >> 8) & 0xFF));
        buf.Add((byte)((v >> 16) & 0xFF));
        buf.Add((byte)((v >> 24) & 0xFF));
    }

    static void WriteGBString(List<byte> buf, string s)
    {
        buf.AddRange(gb2312.GetBytes(s));
        buf.Add(0x00);
    }

    // ── Description 字段（固定23字节，0xCC填充）─────────────
    static byte[] EncodeDescription(string desc)
    {
        byte[] result = new byte[23];
        for (int i = 0; i < 23; i++) result[i] = 0xCC;
        if (!string.IsNullOrEmpty(desc))
        {
            byte[] encoded = gb2312.GetBytes(desc);
            int copyLen = Math.Min(encoded.Length, 21);
            Array.Copy(encoded, result, copyLen);
            result[copyLen] = 0x00;
        }
        else
        {
            result[0] = 0x00;
        }
        return result;
    }

    // ── 行解析：提取指令名 + 参数（支持引号字符串）──────────
    static void ParseLine(string line, out string cmd, out string[] args)
    {
        var argList = new List<string>();
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        int start = i;
        while (i < line.Length && line[i] != ' ') i++;
        cmd = line.Substring(start, i - start);

        while (i < line.Length)
        {
            while (i < line.Length && line[i] == ' ') i++;
            if (i >= line.Length) break;
            if (line[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < line.Length && line[i] != '"') sb.Append(line[i++]);
                if (i < line.Length) i++;
                argList.Add(sb.ToString());
            }
            else
            {
                start = i;
                while (i < line.Length && line[i] != ' ') i++;
                argList.Add(line.Substring(start, i - start));
            }
        }
        args = argList.ToArray();
    }

    static int ParseInt(string s, int lineNum, string line)
    {
        if (int.TryParse(s, out int v)) return v;
        throw new Exception($"第{lineNum}行：'{s}' 不是整数: {line}");
    }
}
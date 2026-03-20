using I18N.CJK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// GUT 脚本反编译器：将 .gut 二进制文件反编译为 .txt 脚本
///
/// 文件格式（对照 GutCompiler.cs / Engine.h uBaseAddr = 0x18）：
///   Offset 0x00 : byte  fileType
///   Offset 0x01 : byte  fileIndex
///   Offset 0x02~0x17 : 22字节固定头（描述/保留）
///   Offset 0x18 : u16   Length（脚本段总长度 = 2 + 1 + NumSceneEvent*2 + scriptBytes.Length）
///   Offset 0x1A : byte  NumSceneEvent
///   Offset 0x1B : u16[] SceneEvent[NumSceneEvent]（跳转表，0表示未使用）
///   Offset 0x1B+NumSceneEvent*2 : 脚本字节流
/// </summary>
public class GutDres
{
    static Encoding gb2312 = null;

    // 文件头常量（与 GutCompiler 保持一致）
    const int HEADER_DESC_SIZE = 22; // type(1)+index(1) 之后，length 之前的填充字节数
    const int BASE_ADDR = 0x18;       // uBaseAddr，length 字段所在偏移

    [MenuItem("工具/反编译GUT脚本")]
    public static void DecompileAll()
    {
        gb2312 = new CP936();

        string srcDir = Application.dataPath + "/../ExRes/gut";
        string outDir = Application.dataPath + "/../ExRes/gut_src_new";

        if (!Directory.Exists(srcDir))
        {
            Debug.LogError($"GUT 文件目录不存在: {srcDir}");
            return;
        }
        if (!Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);

        int ok = 0, fail = 0;
        foreach (string gutPath in Directory.GetFiles(srcDir, "*.gut"))
        {
            string stem = Path.GetFileNameWithoutExtension(gutPath);
            string outPath = Path.Combine(outDir, stem + ".txt");
            try
            {
                DecompileFile(gutPath, outPath);
                Debug.Log($"[GutDres] ✓ {stem}.txt");
                ok++;
            }
            catch (Exception e)
            {
                Debug.LogError($"[GutDres] ✗ {stem}: {e.Message}\n{e.StackTrace}");
                fail++;
            }
        }
        Debug.Log($"[GutDres] 完成：成功 {ok}，失败 {fail}");
    }

    // ── 单文件反编译入口 ─────────────────────────────────────
    public static void DecompileFile(string gutPath, string outPath)
    {
        gb2312 = gb2312 ?? new CP936();

        byte[] data = File.ReadAllBytes(gutPath);
        if (data.Length < BASE_ADDR + 3)
            throw new Exception($"文件太短，不是有效的 GUT 文件: {gutPath}");

        // ── 解析文件头 ───────────────────────────────────────
        int fileType = data[0];
        int fileIndex = data[1];

        // 描述区 [2..23]：22字节，可含以\0结尾的字符串
        string descStr = "";
        int descEnd = 2;
        while (descEnd < 2 + HEADER_DESC_SIZE && data[descEnd] != 0x00 && data[descEnd] != 0xCC)
            descEnd++;
        if (descEnd > 2)
            descStr = gb2312.GetString(data, 2, descEnd - 2);

        // Length 字段 @0x18（u16 LE）
        int length = data[BASE_ADDR] | (data[BASE_ADDR + 1] << 8);

        // NumSceneEvent @0x1A
        int numSceneEvent = data[BASE_ADDR + 2];

        // 跳转表 @0x1B，每项 u16 LE
        int jumpTableOffset = BASE_ADDR + 3; // 0x1B
        var jumpTable = new int[numSceneEvent];
        for (int i = 0; i < numSceneEvent; i++)
        {
            int off = jumpTableOffset + i * 2;
            jumpTable[i] = data[off] | (data[off + 1] << 8);
        }

        // 脚本字节流起始偏移
        // length = 2(Length字段本身) + 1(NumSceneEvent) + numSceneEvent*2 + scriptBytes.Length
        // => scriptBytes.Length = length - 2 - 1 - numSceneEvent*2
        int scriptStart = jumpTableOffset + numSceneEvent * 2;
        int scriptBytesLen = length - 2 - 1 - numSceneEvent * 2;
        // 容错：部分文件 length 可能不含自身2字节，导致差2；直接用文件末尾兜底
        int scriptEnd = scriptStart + scriptBytesLen;
        if (scriptEnd > data.Length || scriptBytesLen < 0)
        {
            // 尝试不减2的解释（length = 1 + numSceneEvent*2 + scriptBytes.Length）
            scriptBytesLen = length - 1 - numSceneEvent * 2;
            scriptEnd = scriptStart + scriptBytesLen;
            if (scriptEnd > data.Length || scriptBytesLen < 0)
                scriptEnd = data.Length; // 最终兜底
        }

        // ── 第一遍：收集所有被跳转指向的地址，生成标签名 ──
        // 地址值 = script内偏移 + scriptDataOffset
        // scriptDataOffset = 2 + 1 + numSceneEvent*2
        int scriptDataOffset = 2 + 1 + numSceneEvent * 2;

        var labelAddrs = new HashSet<int>(); // 存储 script 内偏移

        // 扫描跳转表
        foreach (int addr in jumpTable)
        {
            if (addr != 0)
            {
                int scriptOff = addr - scriptDataOffset;
                if (scriptOff >= 0)
                    labelAddrs.Add(scriptOff);
            }
        }

        // 扫描脚本内所有跳转指令
        ScanLabels(data, scriptStart, scriptEnd, scriptDataOffset, labelAddrs);

        // 为每个地址分配标签名（按偏移排序，格式 label_XXX 与编译器一致）
        var labelMap = new SortedDictionary<int, string>();
        foreach (int off in labelAddrs)
        {
            if (off >= 0 && off <= (scriptEnd - scriptStart))
                labelMap[off] = $"label_{off + scriptDataOffset}";
        }

        // ── 生成输出文本 ─────────────────────────────────────
        var sb = new StringBuilder();

        // 文件头注释
        sb.AppendLine($"@ fileType={fileType} fileIndex={fileIndex}");
        if (!string.IsNullOrEmpty(descStr))
            sb.AppendLine($"@ desc={descStr}");
        sb.AppendLine();

        // GutEvent 跳转表（只输出有效项，unused省略，与编译器第一遍扫描一致）
        bool hasValidEvent = false;
        for (int i = 0; i < numSceneEvent; i++)
            if (jumpTable[i] != 0) { hasValidEvent = true; break; }
        if (hasValidEvent)
        {
            for (int i = 0; i < numSceneEvent; i++)
            {
                if (jumpTable[i] != 0)
                {
                    int scriptOff = jumpTable[i] - scriptDataOffset;
                    string lbl = labelMap.ContainsKey(scriptOff) ? labelMap[scriptOff] : $"label_{jumpTable[i]}";
                    sb.AppendLine($"GutEvent {i + 1} {lbl}");
                }
            }
            sb.AppendLine();
        }

        // ── 反编译指令流 ─────────────────────────────────────
        int pos = scriptStart;
        int scriptPos = 0; // 相对脚本起始的偏移
        bool lastWasTerminator = false; // 上一条指令是否为 Callback/Return/Gameover

        while (pos < scriptEnd)
        {
            // 输出此位置的标签（如果有）
            if (labelMap.TryGetValue(scriptPos, out string labelName))
            {
                sb.AppendLine();
                sb.AppendLine($"{labelName}:");
                lastWasTerminator = false;
            }
            else if (lastWasTerminator)
            {
                // 上一条是终止指令且当前位置无label：dead code块开始，加注释
                sb.AppendLine();
                sb.AppendLine($"@ --- unreachable code at script+{scriptPos} ---");
            }

            byte opcode = data[pos];
            pos++;
            scriptPos++;

            string line;
            try
            {
                line = DisassembleInstruction(opcode, data, ref pos, ref scriptPos,
                                              scriptDataOffset, labelMap, gb2312, scriptEnd);
            }
            catch (Exception ex)
            {
                // 越界时：输出已解析内容 + 剩余字节的十六进制转储，供调试
                sb.AppendLine($"@ ERROR at script+{scriptPos - 1} opcode=0x{opcode:X2}: {ex.Message}");
                sb.AppendLine($"@ Remaining bytes (pos={pos} scriptEnd={scriptEnd}):");
                var hexDump = new System.Text.StringBuilder("@  ");
                for (int di = pos; di < scriptEnd && di < data.Length; di++)
                    hexDump.Append($" {data[di]:X2}");
                sb.AppendLine(hexDump.ToString());
                // 转储从 scriptStart 开始的原始字节用于全局分析
                sb.AppendLine($"@ Full script hex dump (scriptStart={scriptStart} scriptEnd={scriptEnd}):");
                var fullDump = new System.Text.StringBuilder("@  ");
                for (int di = scriptStart; di < scriptEnd && di < data.Length; di++)
                    fullDump.Append($" {data[di]:X2}");
                sb.AppendLine(fullDump.ToString());
                File.WriteAllText(outPath, sb.ToString(), gb2312);
                throw; // 仍然抛出让调用方记录错误
            }

            if (line != null)
                sb.AppendLine(line);
            else
            {
                // 未知指令：输出原始字节注释
                sb.AppendLine($"@ unknown opcode 0x{opcode:X2} at script+{scriptPos - 1}");
            }

            // 记录终止指令：Callback(0x09)、Return(0x44)、Gameover(0x14)、Goto(0x0A)
            lastWasTerminator = (opcode == 0x09 || opcode == 0x44 || opcode == 0x14 || opcode == 0x0A);
        }

        File.WriteAllText(outPath, sb.ToString(), gb2312);
    }

    // ── 第一遍扫描：收集所有跳转目标地址（script 内偏移）──────
    static void ScanLabels(byte[] data, int scriptStart, int scriptEnd,
                           int scriptDataOffset, HashSet<int> labelAddrs)
    {
        int pos = scriptStart;
        while (pos < scriptEnd)
        {
            byte op = data[pos++];
            switch (op)
            {
                // 无参数
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
                    break;

                // N×1 (1个u16)
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
                    pos += 2; break;

                // N×2 (2个u16)
                case 0x00:
                case 0x08:
                case 0x0C:
                case 0x0E:
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
                    pos += 4; break;

                // N×3
                case 0x02:
                case 0x06:
                case 0x2C:
                case 0x35:
                case 0x3B:
                case 0x3C:
                    pos += 6; break;

                // N×4
                case 0x01:
                case 0x20:
                case 0x26:
                    pos += 8; break;

                // N×5
                case 0x1E:
                    pos += 10; break;

                // N×6
                case 0x04:
                case 0x07:
                    pos += 12; break;

                // N×11
                case 0x23:
                    pos += 22; break;

                // L (u32)
                case 0x29:
                case 0x2A:
                case 0x2B:
                    pos += 4; break;

                // 0x0A Goto A
                case 0x0A:
                    {
                        int addr = ReadU16(data, pos); pos += 2;
                        int soff = addr - scriptDataOffset;
                        if (soff >= 0) labelAddrs.Add(soff);
                        break;
                    }

                // 0x05 ActorEvent NA / 0x12 Event NA / 0x3F Randrade NA
                case 0x05:
                case 0x12:
                case 0x3F:
                    {
                        pos += 2; // N
                        int addr = ReadU16(data, pos); pos += 2;
                        int soff = addr - scriptDataOffset;
                        if (soff >= 0) labelAddrs.Add(soff);
                        break;
                    }

                // 0x0B If NA
                case 0x0B:
                    {
                        pos += 2; // N
                        int addr = ReadU16(data, pos); pos += 2;
                        int soff = addr - scriptDataOffset;
                        if (soff >= 0) labelAddrs.Add(soff);
                        break;
                    }

                // 0x15 IfCmp NNA
                case 0x15:
                    {
                        pos += 4; // N N
                        int addr = ReadU16(data, pos); pos += 2;
                        int soff = addr - scriptDataOffset;
                        if (soff >= 0) labelAddrs.Add(soff);
                        break;
                    }

                // 0x0D Say NC
                case 0x0D:
                    pos += 2;
                    SkipString(data, ref pos, scriptEnd);
                    break;

                // 0x2F Message C / 0x36 SetSceneName C
                case 0x2F:
                case 0x36:
                    SkipString(data, ref pos, scriptEnd);
                    break;

                // 0x3D ShowGut NNC
                case 0x3D:
                    pos += 4;
                    SkipString(data, ref pos, scriptEnd);
                    break;

                // 0x40 Menu NC / 0x45 TimeMsg NC
                case 0x40:
                case 0x45:
                    pos += 2;
                    SkipString(data, ref pos, scriptEnd);
                    break;

                // 0x1F Choice CCA
                case 0x1F:
                    {
                        SkipString(data, ref pos, scriptEnd);
                        SkipString(data, ref pos, scriptEnd);
                        int addr = ReadU16(data, pos); pos += 2;
                        int soff = addr - scriptDataOffset;
                        if (soff >= 0) labelAddrs.Add(soff);
                        break;
                    }

                // 0x1C Buy U — 单字节 0x00 终止
                case 0x1C:
                    while (pos < scriptEnd)
                    {
                        if (data[pos] == 0x00) { pos++; break; } // 单字节终止
                        pos += 2; // 跳过 u16 数据
                    }
                    break;

                // 0x30 DeleteGoods NNA / 0x39 UseGoods NNA
                case 0x30:
                case 0x39:
                    {
                        pos += 4; // N N
                        int addr = ReadU16(data, pos); pos += 2;
                        int soff = addr - scriptDataOffset;
                        if (soff >= 0) labelAddrs.Add(soff);
                        break;
                    }

                // 0x3A AttribTest NNNAA
                case 0x3A:
                    {
                        pos += 6; // N N N
                        int a1 = ReadU16(data, pos); pos += 2;
                        int a2 = ReadU16(data, pos); pos += 2;
                        int s1 = a1 - scriptDataOffset; if (s1 >= 0) labelAddrs.Add(s1);
                        int s2 = a2 - scriptDataOffset; if (s2 >= 0) labelAddrs.Add(s2);
                        break;
                    }

                // 0x3E UseGoodsNum NNNA  (3 N + 1 A，编译器写4参但第4个是标签)
                case 0x3E:
                    {
                        pos += 6; // N N N
                        int addr = ReadU16(data, pos); pos += 2;
                        int soff = addr - scriptDataOffset;
                        if (soff >= 0) labelAddrs.Add(soff);
                        break;
                    }

                // 0x41 TestMoney LA
                case 0x41:
                    {
                        pos += 4; // u32
                        int addr = ReadU16(data, pos); pos += 2;
                        int soff = addr - scriptDataOffset;
                        if (soff >= 0) labelAddrs.Add(soff);
                        break;
                    }

                // 0x43 DisCmp NNAA
                case 0x43:
                    {
                        pos += 4; // N N
                        int a1 = ReadU16(data, pos); pos += 2;
                        int a2 = ReadU16(data, pos); pos += 2;
                        int s1 = a1 - scriptDataOffset; if (s1 >= 0) labelAddrs.Add(s1);
                        int s2 = a2 - scriptDataOffset; if (s2 >= 0) labelAddrs.Add(s2);
                        break;
                    }

                // 0x27 EnterFight NNNNNNNNNNNNNAA
                case 0x27:
                    {
                        pos += 26; // 13 × u16
                        int a1 = ReadU16(data, pos); pos += 2;
                        int a2 = ReadU16(data, pos); pos += 2;
                        int s1 = a1 - scriptDataOffset; if (s1 >= 0) labelAddrs.Add(s1);
                        int s2 = a2 - scriptDataOffset; if (s2 >= 0) labelAddrs.Add(s2);
                        break;
                    }

                // 0x4D TestGoodsNum NNNAA
                case 0x4D:
                    {
                        pos += 6; // N N N
                        int a1 = ReadU16(data, pos); pos += 2;
                        int a2 = ReadU16(data, pos); pos += 2;
                        int s1 = a1 - scriptDataOffset; if (s1 >= 0) labelAddrs.Add(s1);
                        int s2 = a2 - scriptDataOffset; if (s2 >= 0) labelAddrs.Add(s2);
                        break;
                    }

                default:
                    // 未知指令，停止扫描以免错位
                    return;
            }
        }
    }

    // ── 单条指令反编译 ───────────────────────────────────────
    // 返回反编译后的文本行，未知指令返回 null
    static string DisassembleInstruction(byte opcode, byte[] data, ref int pos, ref int scriptPos,
        int scriptDataOffset, SortedDictionary<int, string> labelMap, Encoding enc, int limit = int.MaxValue)
    {
        switch (opcode)
        {
            // ── 无参数指令 ───────────────────────────────────
            case 0x09: return "Callback";
            case 0x14: return "Gameover";
            case 0x24: return "FightEnable";
            case 0x25: return "FightDisenable";
            case 0x2D: return "Sale";
            case 0x34: return "DelAllNpc";
            case 0x37: return "ShowSceneName";
            case 0x38: return "ShowScreen";
            case 0x44: return "Return";
            case 0x46: return "DisableSave";
            case 0x47: return "EnableSave";
            case 0x48: return "GameSave";
            case 0x4A: return "EnableShowPos";
            case 0x4B: return "DisableShowPos";

            // ── N×1 ─────────────────────────────────────────
            case 0x03: return $"DeleteNpc {ReadU16Adv(data, ref pos, ref scriptPos, limit)}";
            case 0x0F: return $"ScreenR {ReadU16Adv(data, ref pos, ref scriptPos, limit)}";
            case 0x11: return $"ScreenA {ReadU16Adv(data, ref pos, ref scriptPos, limit)}";
            case 0x13: return $"Money {ReadU16Adv(data, ref pos, ref scriptPos, limit)}";
            case 0x18: return $"SetControlId {ReadU16Adv(data, ref pos, ref scriptPos, limit)}";
            case 0x1A: return $"SetEvent {ReadU16Adv(data, ref pos, ref scriptPos, limit)}";
            case 0x1B: return $"ClrEvent {ReadU16Adv(data, ref pos, ref scriptPos, limit)}";
            case 0x21: return $"DeleteBox {ReadU16Adv(data, ref pos, ref scriptPos, limit)}";
            case 0x28: return $"DeleteActor {ReadU16Adv(data, ref pos, ref scriptPos, limit)}";
            case 0x33: return $"BoxOpen {ReadU16Adv(data, ref pos, ref scriptPos, limit)}";

            // ── N×2 ─────────────────────────────────────────
            case 0x00: return ReadNN("Music", data, ref pos, ref scriptPos);
            case 0x08: return ReadNN("ActorSpeed", data, ref pos, ref scriptPos);
            case 0x0C: return ReadNN("Set", data, ref pos, ref scriptPos);
            case 0x0E: return ReadNN("StartChapter", data, ref pos, ref scriptPos);
            case 0x10: return ReadNN("ScreenS", data, ref pos, ref scriptPos);
            case 0x16: return ReadNN("Add", data, ref pos, ref scriptPos);
            case 0x17: return ReadNN("Sub", data, ref pos, ref scriptPos);
            case 0x1D: return ReadNN("FaceToFace", data, ref pos, ref scriptPos);
            case 0x22: return ReadNN("GainGoods", data, ref pos, ref scriptPos);
            case 0x2E: return ReadNN("NpcMoveMod", data, ref pos, ref scriptPos);
            case 0x31: return ReadNN("ResumeActorHp", data, ref pos, ref scriptPos);
            case 0x32: return ReadNN("ActorLayerUp", data, ref pos, ref scriptPos);
            case 0x42: return ReadNN("CallChapter", data, ref pos, ref scriptPos);
            case 0x49: return ReadNN("SetEventTimer", data, ref pos, ref scriptPos);
            case 0x4C: return ReadNN("SetTo", data, ref pos, ref scriptPos);

            // ── N×3 ─────────────────────────────────────────
            case 0x02: return ReadNx("CreateActor", 3, data, ref pos, ref scriptPos);
            case 0x06: return ReadNx("Move", 3, data, ref pos, ref scriptPos);
            case 0x2C: return ReadNx("LearnMagic", 3, data, ref pos, ref scriptPos);
            case 0x35: return ReadNx("NpcStep", 3, data, ref pos, ref scriptPos);
            case 0x3B: return ReadNx("AttribSet", 3, data, ref pos, ref scriptPos);
            case 0x3C: return ReadNx("AttribAdd", 3, data, ref pos, ref scriptPos);

            // ── N×4 ─────────────────────────────────────────
            case 0x01: return ReadNx("LoadMap", 4, data, ref pos, ref scriptPos);
            case 0x20: return ReadNx("CreateBox", 4, data, ref pos, ref scriptPos);
            case 0x26: return ReadNx("CreateNpc", 4, data, ref pos, ref scriptPos);

            // ── N×5 ─────────────────────────────────────────
            case 0x1E: return ReadNx("Movie", 5, data, ref pos, ref scriptPos);

            // ── N×6 ─────────────────────────────────────────
            case 0x04: return ReadNx("MapEvent", 6, data, ref pos, ref scriptPos);
            case 0x07: return ReadNx("ActorMove", 6, data, ref pos, ref scriptPos);

            // ── N×11 ────────────────────────────────────────
            case 0x23: return ReadNx("InitFight", 11, data, ref pos, ref scriptPos);

            // ── L (u32) ─────────────────────────────────────
            case 0x29: return $"GainMoney {ReadU32Adv(data, ref pos, ref scriptPos, limit)}";
            case 0x2A: return $"UseMoney {ReadU32Adv(data, ref pos, ref scriptPos, limit)}";
            case 0x2B: return $"SetMoney {ReadU32Adv(data, ref pos, ref scriptPos, limit)}";

            // ── 0x0A Goto A ──────────────────────────────────
            case 0x0A:
                {
                    int addr = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    string lbl = AddrToLabel(addr, scriptDataOffset, labelMap);
                    return $"Goto {lbl}";
                }

            // ── NA：ActorEvent / Event / Randrade ────────────
            case 0x05:
                {
                    int n = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int addr = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"ActorEvent {n} {AddrToLabel(addr, scriptDataOffset, labelMap)}";
                }
            case 0x12:
                {
                    int n = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int addr = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"Event {n} {AddrToLabel(addr, scriptDataOffset, labelMap)}";
                }
            case 0x3F:
                {
                    int n = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int addr = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"Randrade {n} {AddrToLabel(addr, scriptDataOffset, labelMap)}";
                }

            // ── 0x0B If NA ───────────────────────────────────
            case 0x0B:
                {
                    int n = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int addr = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"If {n} {AddrToLabel(addr, scriptDataOffset, labelMap)}";
                }

            // ── 0x15 IfCmp NNA ───────────────────────────────
            case 0x15:
                {
                    int n1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int n2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int addr = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"IfCmp {n1} {n2} {AddrToLabel(addr, scriptDataOffset, labelMap)}";
                }

            // ── 0x0D Say NC ──────────────────────────────────
            case 0x0D:
                {
                    int n = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    string s = ReadStringAdv(data, ref pos, ref scriptPos, enc, limit);
                    return $"Say {n} \"{s}\"";
                }

            // ── 0x2F Message C ───────────────────────────────
            case 0x2F:
                {
                    string s = ReadStringAdv(data, ref pos, ref scriptPos, enc, limit);
                    return $"Message \"{s}\"";
                }

            // ── 0x36 SetSceneName C ──────────────────────────
            case 0x36:
                {
                    string s = ReadStringAdv(data, ref pos, ref scriptPos, enc, limit);
                    return $"SetSceneName \"{s}\"";
                }

            // ── 0x3D ShowGut NNC ─────────────────────────────
            case 0x3D:
                {
                    int n1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int n2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    string s = ReadStringAdv(data, ref pos, ref scriptPos, enc, limit);
                    return $"ShowGut {n1} {n2} \"{s}\"";
                }

            // ── 0x40 Menu NC / 0x45 TimeMsg NC ──────────────
            case 0x40:
                {
                    int n = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    string s = ReadStringAdv(data, ref pos, ref scriptPos, enc, limit);
                    return $"Menu {n} \"{s}\"";
                }
            case 0x45:
                {
                    int n = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    string s = ReadStringAdv(data, ref pos, ref scriptPos, enc, limit);
                    return $"TimeMsg {n} \"{s}\"";
                }

            // ── 0x1F Choice CCA ──────────────────────────────
            case 0x1F:
                {
                    string s1 = ReadStringAdv(data, ref pos, ref scriptPos, enc, limit);
                    string s2 = ReadStringAdv(data, ref pos, ref scriptPos, enc, limit);
                    int addr = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"Choice \"{s1}\" \"{s2}\" {AddrToLabel(addr, scriptDataOffset, labelMap)}";
                }

            // ── 0x1C Buy U ───────────────────────────────────
            // 终止符是单字节 0x00（引擎 peek 一字节判断，非 u16）
            case 0x1C:
                {
                    var items = new List<int>();
                    int cap = Math.Min(data.Length, limit);
                    while (pos < cap)
                    {
                        if (data[pos] == 0x00) { pos++; scriptPos++; break; } // 单字节终止
                        if (pos + 2 > cap) break; // 数据不足，防止越界
                        items.Add(ReadU16Adv(data, ref pos, ref scriptPos, limit));
                    }
                    return $"Buy \"{string.Join(" ", items)}\"";
                }

            // ── 0x30 DeleteGoods NNA / 0x39 UseGoods NNA ────
            case 0x30:
                {
                    int n1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int n2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int addr = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"DeleteGoods {n1} {n2} {AddrToLabel(addr, scriptDataOffset, labelMap)}";
                }
            case 0x39:
                {
                    int n1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int n2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int addr = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"UseGoods {n1} {n2} {AddrToLabel(addr, scriptDataOffset, labelMap)}";
                }

            // ── 0x3A AttribTest NNNAA ────────────────────────
            case 0x3A:
                {
                    int n1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int n2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int n3 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int a1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int a2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"AttribTest {n1} {n2} {n3} {AddrToLabel(a1, scriptDataOffset, labelMap)} {AddrToLabel(a2, scriptDataOffset, labelMap)}";
                }

            // ── 0x3E UseGoodsNum NNNA ────────────────────────
            case 0x3E:
                {
                    int n1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int n2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int n3 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int addr = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"UseGoodsNum {n1} {n2} {n3} {AddrToLabel(addr, scriptDataOffset, labelMap)}";
                }

            // ── 0x41 TestMoney LA ────────────────────────────
            case 0x41:
                {
                    int l = ReadU32Adv(data, ref pos, ref scriptPos, limit);
                    int addr = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"TestMoney {l} {AddrToLabel(addr, scriptDataOffset, labelMap)}";
                }

            // ── 0x43 DisCmp NNAA ─────────────────────────────
            case 0x43:
                {
                    int n1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int n2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int a1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int a2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"DisCmp {n1} {n2} {AddrToLabel(a1, scriptDataOffset, labelMap)} {AddrToLabel(a2, scriptDataOffset, labelMap)}";
                }

            // ── 0x27 EnterFight NNNNNNNNNNNNNAA ─────────────
            case 0x27:
                {
                    var ns = new int[13];
                    for (int i = 0; i < 13; i++)
                        ns[i] = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int a1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int a2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"EnterFight {string.Join(" ", ns)} {AddrToLabel(a1, scriptDataOffset, labelMap)} {AddrToLabel(a2, scriptDataOffset, labelMap)}";
                }

            // ── 0x4D TestGoodsNum NNNAA ──────────────────────
            case 0x4D:
                {
                    int n1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int n2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int n3 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int a1 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    int a2 = ReadU16Adv(data, ref pos, ref scriptPos, limit);
                    return $"TestGoodsNum {n1} {n2} {n3} {AddrToLabel(a1, scriptDataOffset, labelMap)} {AddrToLabel(a2, scriptDataOffset, labelMap)}";
                }

            default:
                return null;
        }
    }

    // ── 地址转标签名 ──────────────────────────────────────────
    static string AddrToLabel(int addr, int scriptDataOffset, SortedDictionary<int, string> labelMap)
    {
        int scriptOff = addr - scriptDataOffset;
        if (labelMap.TryGetValue(scriptOff, out string lbl))
            return lbl;
        // 地址不在已知标签中，直接用 label_<addr> 格式（与编译器兼容）
        return $"label_{addr}";
    }

    // ── 读取辅助（带 pos/scriptPos 推进）──────────────────────
    static int ReadU16Adv(byte[] data, ref int pos, ref int scriptPos, int limit = int.MaxValue)
    {
        int cap = Math.Min(data.Length, limit);
        if (pos + 2 > cap)
            throw new Exception($"ReadU16 越界: pos={pos} limit={cap} scriptPos={scriptPos}");
        int v = data[pos] | (data[pos + 1] << 8);
        pos += 2; scriptPos += 2;
        return v;
    }

    static int ReadU32Adv(byte[] data, ref int pos, ref int scriptPos, int limit = int.MaxValue)
    {
        int cap = Math.Min(data.Length, limit);
        if (pos + 4 > cap)
            throw new Exception($"ReadU32 越界: pos={pos} limit={cap} scriptPos={scriptPos}");
        int v = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);
        pos += 4; scriptPos += 4;
        return v;
    }

    static string ReadStringAdv(byte[] data, ref int pos, ref int scriptPos, Encoding enc, int limit = int.MaxValue)
    {
        int cap = Math.Min(data.Length, limit);
        int start = pos;
        while (pos < cap && data[pos] != 0x00) pos++;
        string s = enc.GetString(data, start, pos - start);
        int consumed = pos - start + (pos < cap ? 1 : 0);
        if (pos < cap) pos++; // skip null terminator
        scriptPos += consumed;
        return s;
    }

    static int ReadU16(byte[] data, int pos)
    {
        if (pos + 2 > data.Length) return 0;
        return data[pos] | (data[pos + 1] << 8);
    }

    static void SkipString(byte[] data, ref int pos, int end)
    {
        while (pos < end && data[pos] != 0x00) pos++;
        if (pos < end) pos++;
    }

    // ── 便捷格式化 ────────────────────────────────────────────
    static string ReadNN(string name, byte[] data, ref int pos, ref int scriptPos)
    {
        int n1 = ReadU16Adv(data, ref pos, ref scriptPos);
        int n2 = ReadU16Adv(data, ref pos, ref scriptPos);
        return $"{name} {n1} {n2}";
    }

    static string ReadNx(string name, int count, byte[] data, ref int pos, ref int scriptPos)
    {
        var sb = new StringBuilder(name);
        for (int i = 0; i < count; i++)
            sb.Append(' ').Append(ReadU16Adv(data, ref pos, ref scriptPos));
        return sb.ToString();
    }
}
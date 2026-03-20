using I18N.CJK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// GUT 脚本编译器：将 .txt 脚本编译为 .gut 二进制文件
///
/// 文件格式（对照 C++ Engine.h uBaseAddr = 0x18）：
///   Offset 0x00 : byte  fileType
///   Offset 0x01 : byte  fileIndex
///   Offset 0x02~0x17 : 22字节固定头（0xCC填充，可含描述文字）
///   Offset 0x18 : u16   Length（脚本段总长度 = NumSceneEvent*2+1 + scriptBytes.Length）
///   Offset 0x1A : byte  NumSceneEvent
///   Offset 0x1B : u16[] SceneEvent[NumSceneEvent]（跳转表，0表示未使用）
///   Offset 0x1B+NumSceneEvent*2 : 脚本字节流
///
/// 标签格式：反编译器输出 label_123:，编译器同样支持此格式以及普通 word: 格式。
///
/// 完整 opcode 表（来自 TagResources.h）：
///   0x00 Music           NN
///   0x01 LoadMap         NNNN
///   0x02 CreateActor     NNN
///   0x03 DeleteNpc       N
///   0x04 MapEvent        NNNNNN
///   0x05 ActorEvent      NA
///   0x06 Move            NNN
///   0x07 ActorMove       NNNNNN
///   0x08 ActorSpeed      NN
///   0x09 Callback        (无参数)
///   0x0A Goto            A
///   0x0B If              NA
///   0x0C Set             NN
///   0x0D Say             NC
///   0x0E StartChapter    NN
///   0x0F ScreenR         N
///   0x10 ScreenS         NN
///   0x11 ScreenA         N
///   0x12 Event           NA
///   0x13 Money           N
///   0x14 Gameover        (无参数)
///   0x15 IfCmp           NNA
///   0x16 Add             NN
///   0x17 Sub             NN
///   0x18 SetControlId    N
///   0x19 GutEvent        NE  （跳转表索引，编译时写入跳转表）
///   0x1A SetEvent        N
///   0x1B ClrEvent        N
///   0x1C Buy             U   （多个u16，以0终止）
///   0x1D FaceToFace      NN
///   0x1E Movie           NNNNN
///   0x1F Choice          CCA （两个字符串+地址）
///   0x20 CreateBox       NNNN
///   0x21 DeleteBox       N
///   0x22 GainGoods       NN
///   0x23 InitFight       NNNNNNNNNNN
///   0x24 FightEnable     (无参数)
///   0x25 FightDisenable  (无参数)
///   0x26 CreateNpc       NNNN
///   0x27 EnterFight      NNNNNNNNNNNNNAA
///   0x28 DeleteActor     N
///   0x29 GainMoney       L
///   0x2A UseMoney        L
///   0x2B SetMoney        L
///   0x2C LearnMagic      NNN
///   0x2D Sale            (无参数)
///   0x2E NpcMoveMod      NN
///   0x2F Message         C
///   0x30 DeleteGoods     NNA
///   0x31 ResumeActorHp   NN
///   0x32 ActorLayerUp    NN
///   0x33 BoxOpen         N
///   0x34 DelAllNpc       (无参数)
///   0x35 NpcStep         NNN
///   0x36 SetSceneName    C
///   0x37 ShowSceneName   (无参数)
///   0x38 ShowScreen      (无参数)
///   0x39 UseGoods        NNA
///   0x3A AttribTest      NNNAA
///   0x3B AttribSet       NNN
///   0x3C AttribAdd       NNN
///   0x3D ShowGut         NNC
///   0x3E UseGoodsNum     NNNA
///   0x3F Randrade        NA
///   0x40 Menu            NC
///   0x41 TestMoney       LA
///   0x42 CallChapter     NN
///   0x43 DisCmp          NNAA
///   0x44 Return          (无参数)
///   0x45 TimeMsg         NC
///   0x46 DisableSave     (无参数)
///   0x47 EnableSave      (无参数)
///   0x48 GameSave        (无参数)
///   0x49 SetEventTimer   NN
///   0x4A EnableShowPos   (无参数)
///   0x4B DisableShowPos  (无参数)
///   0x4C SetTo           NN
///   0x4D TestGoodsNum    NNNAA
/// </summary>
public class GutCompiler
{
    static Encoding gb2312 = null;

    // 文件头固定大小（参见 C++ Engine.h: uBaseAddr = 0x18 = 24）
    // 布局：[0] type, [1] index, [2..0x17] 22字节描述/保留, [0x18] length_lo, [0x19] length_hi
    // 然后是 NumSceneEvent(1) + SceneEvent[](每项2字节) + 脚本流
    const int HEADER_DESC_SIZE = 22; // type(1)+index(1)之后，length之前的填充字节数

    [MenuItem("工具/编译GUT脚本")]
    public static void CompileAll()
    {
        gb2312 = new CP936();

        string srcDir = Application.dataPath + "/../ExRes/gut_src_new";
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
            string stem = Path.GetFileNameWithoutExtension(txtPath);
            // 从文件名提取 type 和 index（如 "2-1M1-1" → type=2, index=1 → "2-1.gut"）
            var parts = stem.Split('-');
            string gutName = stem; // 默认回退到原始文件名
            if (parts.Length >= 2 && int.TryParse(parts[0], out int t))
            {
                string idxDigits = System.Text.RegularExpressions.Regex.Match(parts[1], @"^\d+").Value;
                if (!string.IsNullOrEmpty(idxDigits))
                    gutName = $"{t}-{idxDigits}";
            }
            string outPath = Path.Combine(outDir, gutName + ".gut");
            try
            {
                CompileFile(txtPath, outPath);
                Debug.Log($"[GutCompiler] ✓ {gutName}.gut  (src: {stem})");
                ok++;
            }
            catch (Exception e)
            {
                Debug.LogError($"[GutCompiler] ✗ {stem}: {e.Message}");
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
        if (parts.Length < 2 || !int.TryParse(parts[0], out int fileType))
            throw new Exception($"文件名格式错误，应为 type-index[...].txt，实际: {stem}");
        // parts[1] 可能是 "10M2" 这样的形式，只取开头的数字部分
        string indexStr = System.Text.RegularExpressions.Regex.Match(parts[1], @"^\d+").Value;
        if (!int.TryParse(indexStr, out int fileIndex))
            throw new Exception($"文件名格式错误，无法解析索引: {stem}");

        string[] lines = File.ReadAllLines(txtPath, gb2312);

        // ── 第一遍：删除注释行，收集 GutEvent 跳转表 ──────────
        // GutEvent 行格式（来自反编译器输出）：GutEvent <slotIndex> label_<addr>
        // 跳转表是稀疏的，按最大槽位分配，未使用的槽填0
        var gutEventSlots = new Dictionary<int, string>(); // slot(1-based) -> label

        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("@"))
            {
                lines[i] = "";
                continue;
            }
            // 识别 GutEvent 行（在 @======GutEvent====== 注释段内或正文中）
            if (t.StartsWith("GutEvent ", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("gutevent ", StringComparison.OrdinalIgnoreCase))
            {
                ParseLine(t, out string gcmd, out string[] gargs);
                if (gargs.Length >= 2 && int.TryParse(gargs[0], out int slot))
                {
                    gutEventSlots[slot] = gargs[1]; // label name
                }
                lines[i] = ""; // GutEvent 行不生成指令字节，已记录到跳转表
            }
        }

        // 计算跳转表大小：取最大槽位号（1-based），空槽填0
        int jumpTableSize = gutEventSlots.Count > 0 ? 0 : 0;
        foreach (int slot in gutEventSlots.Keys)
            if (slot > jumpTableSize) jumpTableSize = slot;

        // ── 第二遍：编译指令字节流（两遍处理 goto/if 标签） ──
        var labelPos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var script = new List<byte>();
        // 记录需要回填的位置：(scriptByteOffset, labelName)
        var patchList = new List<(int patchOffset, string label)>();

        int lineNum = 0;
        foreach (string rawLine in lines)
        {
            lineNum++;
            string line = rawLine.Trim();
            // 裁掉行内 @ 注释（@ 之前的内容保留）
            int atIdx = line.IndexOf('@');
            if (atIdx >= 0) line = line.Substring(0, atIdx).TrimEnd();
            if (string.IsNullOrEmpty(line)) continue;

            // 标签行：支持 label_123:、word:、中文标签:、含连字符的标签:
            // 匹配以非空白字符开头、以冒号结尾、冒号后无其他内容的行
            if (line.EndsWith(":") && !line.Contains(' '))
            {
                string label = line.TrimEnd(':');
                labelPos[label] = script.Count;
                continue;
            }

            ParseLine(line, out string cmd, out string[] args);
            EmitInstruction(script, patchList, labelPos, cmd, args, lineNum, line);
        }

        // 回填 goto/if 中的标签地址（u16 LE）
        // 地址是相对脚本段起始的偏移，脚本段起始 = uBaseAddr(0x18) + 2(length) + 1(numSceneEvent) + jumpTableSize*2
        // 即存储值 = script字节偏移 + jumpTableSize*2 + 1（numSceneEvent字节本身不计入length偏移起点）
        // C++中：脚本段数据从 (uBaseAddr + 2 + 1 + jumpTableSize*2) 开始读，
        //        但length字段定义的段长度包含了 (1 + jumpTableSize*2 + scriptBytes.Length)
        //        地址回填值 = 脚本内偏移 + (1 + jumpTableSize*2)
        // 地址值 = file_offset - uBaseAddr(24)
        // file_offset of script[i] = 24 + 2(Length字段) + 1(NumSceneEvent) + jumpTableSize*2 + i
        // 所以 rawAddr = script内偏移 + 2 + 1 + jumpTableSize*2
        int scriptDataOffset = 2 + 1 + jumpTableSize * 2;

        // 先解析跳转表中的标签，得到实际地址（需要在回填完patchList之后才能知道，
        // 但跳转表地址也是脚本内偏移，同样需要加 scriptDataOffset）
        // 注意：跳转表标签回填必须在所有labelPos已知后进行
        var jumpTable = new int[jumpTableSize]; // 存储原始脚本内偏移（待加offset后写入文件）
        for (int i = 0; i < jumpTableSize; i++)
        {
            int slot1Based = i + 1;
            if (gutEventSlots.TryGetValue(slot1Based, out string evLabel))
            {
                if (!labelPos.TryGetValue(evLabel, out int evAddr))
                    throw new Exception($"GutEvent 槽 {slot1Based} 引用了未定义的标签: {evLabel}");
                jumpTable[i] = evAddr + scriptDataOffset;
            }
            else
            {
                jumpTable[i] = 0; // 未使用的槽填0
            }
        }

        // 回填脚本内的标签地址
        foreach (var (patchOffset, label) in patchList)
        {
            if (!labelPos.TryGetValue(label, out int addr))
                throw new Exception($"未定义的标签: {label}");
            int rawAddr = addr + scriptDataOffset;
            script[patchOffset] = (byte)(rawAddr & 0xFF);
            script[patchOffset + 1] = (byte)((rawAddr >> 8) & 0xFF);
        }

        // ── 组装 GUT 文件 ─────────────────────────────────────
        // 文件布局：
        //   [0]     fileType
        //   [1]     fileIndex
        //   [2..23] 22字节描述/保留（0xCC填充）
        //   [24]    length lo  （length = 1 + jumpTableSize*2 + scriptBytes.Length）
        //   [25]    length hi
        //   [26]    numSceneEvent (= jumpTableSize)
        //   [27..27+jumpTableSize*2-1]  jumpTable (u16 LE each)
        //   [27+jumpTableSize*2 ..]     scriptBytes
        byte[] scriptBytes = script.ToArray();
        // Length = fileSize - 24 = 2(Length字段本身) + 1(NumSceneEvent) + jumpTableSize*2 + script长度
        int length = 2 + 1 + jumpTableSize * 2 + scriptBytes.Length;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((byte)fileType);
        w.Write((byte)fileIndex);
        // 22字节描述区 [2..23]：前21字节0xCC，最后1字节固定0x00
        // 格式与原始GUT文件一致（原始文件此区为：描述字符串+\0+0xCC填充+\0）
        byte[] desc = new byte[HEADER_DESC_SIZE];
        for (int i = 0; i < HEADER_DESC_SIZE - 1; i++) desc[i] = 0xCC;
        desc[HEADER_DESC_SIZE - 1] = 0x00; // 最后一字节必须是\0
        w.Write(desc);

        // Length 字段位于 uBaseAddr = 0x18
        w.Write((byte)(length & 0xFF));
        w.Write((byte)((length >> 8) & 0xFF));

        // 跳转表
        w.Write((byte)jumpTableSize);
        for (int i = 0; i < jumpTableSize; i++)
        {
            w.Write((byte)(jumpTable[i] & 0xFF));
            w.Write((byte)((jumpTable[i] >> 8) & 0xFF));
        }

        // 脚本字节流
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
            case "callback": buf.Add(0x09); break;
            case "gameover": buf.Add(0x14); break;
            case "sale": buf.Add(0x2D); break;
            case "delallnpc": buf.Add(0x34); break;
            case "showscenename": buf.Add(0x37); break;
            case "showscreen": buf.Add(0x38); break;
            case "fightenable": buf.Add(0x24); break;
            case "fightdisenable": buf.Add(0x25); break;
            case "return": buf.Add(0x44); break;
            case "disablesave": buf.Add(0x46); break;
            case "enablesave": buf.Add(0x47); break;
            case "gamesave": buf.Add(0x48); break;
            case "enableshowpos": buf.Add(0x4A); break;
            case "disableshowpos": buf.Add(0x4B); break;

            // ── NN 定长指令 ──────────────────────────────────
            case "music": Emit(buf, 0x00, args, 2, lineNum, line); break;
            case "actorspeed": Emit(buf, 0x08, args, 2, lineNum, line); break;
            case "set": Emit(buf, 0x0C, args, 2, lineNum, line); break;
            case "startchapter": Emit(buf, 0x0E, args, 2, lineNum, line); break;
            case "screens": Emit(buf, 0x10, args, 2, lineNum, line); break; // ScreenS NN
            case "add": Emit(buf, 0x16, args, 2, lineNum, line); break;
            case "sub": Emit(buf, 0x17, args, 2, lineNum, line); break;
            case "facetoface": Emit(buf, 0x1D, args, 2, lineNum, line); break;
            case "npcmovemod": Emit(buf, 0x2E, args, 2, lineNum, line); break;
            case "resumeactorhp": Emit(buf, 0x31, args, 2, lineNum, line); break;
            case "actorlayerup": Emit(buf, 0x32, args, 2, lineNum, line); break;
            case "seteventtimer": Emit(buf, 0x49, args, 2, lineNum, line); break;
            case "setto": Emit(buf, 0x4C, args, 2, lineNum, line); break;

            // ── N 定长指令 ───────────────────────────────────
            case "deletenpc": Emit(buf, 0x03, args, 1, lineNum, line); break;
            case "screenr": Emit(buf, 0x0F, args, 1, lineNum, line); break; // ScreenR N
            case "screena": Emit(buf, 0x11, args, 1, lineNum, line); break; // ScreenA N
            case "money": Emit(buf, 0x13, args, 1, lineNum, line); break;
            case "setcontrolid": Emit(buf, 0x18, args, 1, lineNum, line); break;
            case "setevent": Emit(buf, 0x1A, args, 1, lineNum, line); break;
            case "clrevent": Emit(buf, 0x1B, args, 1, lineNum, line); break;
            case "deletebox": Emit(buf, 0x21, args, 1, lineNum, line); break;
            case "deleteactor": Emit(buf, 0x28, args, 1, lineNum, line); break;
            case "boxopen": Emit(buf, 0x33, args, 1, lineNum, line); break;

            // ── NNN 定长指令 ─────────────────────────────────
            case "createactor": Emit(buf, 0x02, args, 3, lineNum, line); break;
            case "move": Emit(buf, 0x06, args, 3, lineNum, line); break;
            case "learnmagic": Emit(buf, 0x2C, args, 3, lineNum, line); break;
            case "npcstep": Emit(buf, 0x35, args, 3, lineNum, line); break;
            case "attribset": Emit(buf, 0x3B, args, 3, lineNum, line); break;
            case "attribadd": Emit(buf, 0x3C, args, 3, lineNum, line); break; // 0x3C AttribAdd NNN
            case "usegoodsnum": Emit(buf, 0x3E, args, 3, lineNum, line); break;
            case "callchapter": Emit(buf, 0x42, args, 2, lineNum, line); break;

            // ── NNNN 定长指令 ────────────────────────────────
            case "loadmap": Emit(buf, 0x01, args, 4, lineNum, line); break;
            case "createbox": Emit(buf, 0x20, args, 4, lineNum, line); break;
            case "createnpc": Emit(buf, 0x26, args, 4, lineNum, line); break;

            // ── NNNNNN 定长指令 ──────────────────────────────
            case "mapevent": Emit(buf, 0x04, args, 6, lineNum, line); break;
            case "actormove": Emit(buf, 0x07, args, 6, lineNum, line); break;

            // ── NNNNN 定长指令 ───────────────────────────────
            case "movie": Emit(buf, 0x1E, args, 5, lineNum, line); break;

            // ── gaingoods: NN（注意原C++TAG_PARAM为"NN"，非NNN）──
            case "gaingoods": Emit(buf, 0x22, args, 2, lineNum, line); break;

            // ── NNNNNNNNNNN 定长指令 ─────────────────────────
            case "initfight": Emit(buf, 0x23, args, 11, lineNum, line); break;

            // ── u32 参数指令 ─────────────────────────────────
            case "gainmoney": EmitU32(buf, 0x29, args, lineNum, line); break;
            case "usemoney": EmitU32(buf, 0x2A, args, lineNum, line); break;
            case "setmoney": EmitU32(buf, 0x2B, args, lineNum, line); break;

            // ── 带地址参数指令（需要标签回填）────────────────
            // ActorEvent NA
            case "actorevent":
                EmitNA(buf, patchList, labelPos, 0x05, args, lineNum, line); break;
            // Event NA
            case "event":
                EmitNA(buf, patchList, labelPos, 0x12, args, lineNum, line); break;
            // Goto A
            case "goto":
                EmitGoto(buf, patchList, labelPos, args, lineNum, line); break;
            // If NA
            case "if":
                EmitIf(buf, patchList, labelPos, args, lineNum, line); break;
            // IfCmp NNA
            case "ifcmp":
                EmitIfCmp(buf, patchList, labelPos, args, lineNum, line); break;
            // DeleteGoods NNA
            case "deletegoods":
                EmitNNA(buf, patchList, labelPos, 0x30, args, lineNum, line); break;
            // UseGoods NNA
            case "usegoods":
                EmitNNA(buf, patchList, labelPos, 0x39, args, lineNum, line); break;
            // AttribTest NNNAA
            case "attribtest":
                EmitNNNAA(buf, patchList, labelPos, 0x3A, args, lineNum, line); break;
            // Randrade NA
            case "randrade":
                EmitNA(buf, patchList, labelPos, 0x3F, args, lineNum, line); break;
            // TestMoney LA
            case "testmoney":
                EmitLA(buf, patchList, labelPos, 0x41, args, lineNum, line); break;
            // DisCmp NNAA
            case "discmp":
                EmitNNAA(buf, patchList, labelPos, 0x43, args, lineNum, line); break;
            // TestGoodsNum NNNAA
            case "testgoodsnum":
                EmitNNNAA(buf, patchList, labelPos, 0x4D, args, lineNum, line); break;
            // EnterFight NNNNNNNNNNNNNAA
            case "enterfight":
                EmitEnterFight(buf, patchList, labelPos, args, lineNum, line); break;

            // ── 带字符串指令 ─────────────────────────────────
            // Say NC
            case "say":
                EmitSay(buf, args, lineNum, line); break;
            // Message C
            case "message":
                EmitStringOnly(buf, 0x2F, args, lineNum, line); break;
            // SetSceneName C
            case "setscenename":
                EmitStringOnly(buf, 0x36, args, lineNum, line); break;
            // ShowGut NNC
            case "showgut":
                EmitShowGut(buf, args, lineNum, line); break;
            // Menu NC（opcode + u16 actorId + 字符串+\0）
            case "menu":
                EmitNC(buf, 0x40, args, lineNum, line); break;
            // TimeMsg NC
            case "timemsg":
                EmitNC(buf, 0x45, args, lineNum, line); break;

            // ── Choice CCA（两个字符串+地址）────────────────
            case "choice":
                EmitChoice(buf, patchList, labelPos, args, lineNum, line); break;

            // ── Buy U（多个u16，以0终止）──────────────────────
            case "buy":
                EmitBuy(buf, args, lineNum, line); break;

            // ── GutEvent NE（跳转表引用，在头部处理，此处跳过）──
            case "gutevent":
                // 已在第一遍处理，此处不生成字节
                break;

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

    // ── u32 参数 ──────────────────────────────────────────
    static void EmitU32(List<byte> buf, byte op, string[] args, int lineNum, string line)
    {
        if (args.Length < 1)
            throw new Exception($"第{lineNum}行：{line} 需要1个参数");
        buf.Add(op);
        WriteU32(buf, ParseInt(args[0], lineNum, line));
    }

    // ── NA：opcode + u16 + 地址回填 ─────────────────────────
    static void EmitNA(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, byte op, string[] args, int lineNum, string line)
    {
        if (args.Length < 2)
            throw new Exception($"第{lineNum}行：{line} 需要2个参数（数值 + 标签）");
        buf.Add(op);
        WriteU16(buf, ParseInt(args[0], lineNum, line));
        EmitLabel(buf, patchList, labelPos, args[1]);
    }

    // ── NNA：opcode + u16 + u16 + 地址回填 ──────────────────
    static void EmitNNA(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, byte op, string[] args, int lineNum, string line)
    {
        if (args.Length < 3)
            throw new Exception($"第{lineNum}行：{line} 需要3个参数（N N 标签）");
        buf.Add(op);
        WriteU16(buf, ParseInt(args[0], lineNum, line));
        WriteU16(buf, ParseInt(args[1], lineNum, line));
        EmitLabel(buf, patchList, labelPos, args[2]);
    }

    // ── NNAA：opcode + u16 + u16 + addr + addr ───────────────
    static void EmitNNAA(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, byte op, string[] args, int lineNum, string line)
    {
        if (args.Length < 4)
            throw new Exception($"第{lineNum}行：{line} 需要4个参数（N N 标签 标签）");
        buf.Add(op);
        WriteU16(buf, ParseInt(args[0], lineNum, line));
        WriteU16(buf, ParseInt(args[1], lineNum, line));
        EmitLabel(buf, patchList, labelPos, args[2]);
        EmitLabel(buf, patchList, labelPos, args[3]);
    }

    // ── NNNAA：opcode + u16×3 + addr + addr ─────────────────
    static void EmitNNNAA(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, byte op, string[] args, int lineNum, string line)
    {
        if (args.Length < 5)
            throw new Exception($"第{lineNum}行：{line} 需要5个参数（N N N 标签 标签）");
        buf.Add(op);
        for (int i = 0; i < 3; i++) WriteU16(buf, ParseInt(args[i], lineNum, line));
        EmitLabel(buf, patchList, labelPos, args[3]);
        EmitLabel(buf, patchList, labelPos, args[4]);
    }

    // ── LA：opcode + u32 + 地址回填 ─────────────────────────
    static void EmitLA(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, byte op, string[] args, int lineNum, string line)
    {
        if (args.Length < 2)
            throw new Exception($"第{lineNum}行：{line} 需要2个参数（u32值 + 标签）");
        buf.Add(op);
        WriteU32(buf, ParseInt(args[0], lineNum, line));
        EmitLabel(buf, patchList, labelPos, args[1]);
    }

    // ── EnterFight NNNNNNNNNNNNNAA ───────────────────────────
    static void EmitEnterFight(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, string[] args, int lineNum, string line)
    {
        if (args.Length < 15)
            throw new Exception($"第{lineNum}行：EnterfFight 需要15个参数（13×N + 2×A）: {line}");
        buf.Add(0x27);
        for (int i = 0; i < 13; i++) WriteU16(buf, ParseInt(args[i], lineNum, line));
        EmitLabel(buf, patchList, labelPos, args[13]);
        EmitLabel(buf, patchList, labelPos, args[14]);
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

    // ── NC：opcode + u16 + 字符串+\0 ────────────────────────
    static void EmitNC(List<byte> buf, byte op, string[] args, int lineNum, string line)
    {
        if (args.Length < 2)
            throw new Exception($"第{lineNum}行：{line} 需要数值参数和字符串参数");
        buf.Add(op);
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

    // ── SHOWGUT：opcode + 2×u16 + 字符串+\0 ─────────────────
    // C++ TAG_PARAM: "NNC"（2个u16 + 字符串）
    static void EmitShowGut(List<byte> buf, string[] args, int lineNum, string line)
    {
        if (args.Length < 3)
            throw new Exception($"第{lineNum}行：ShowGut 需要2个数字参数和1个字符串: {line}");
        buf.Add(0x3D);
        WriteU16(buf, ParseInt(args[0], lineNum, line));
        WriteU16(buf, ParseInt(args[1], lineNum, line));
        WriteGBString(buf, args[2]);
    }

    // ── GOTO：opcode + u16 地址（回填）──────────────────────
    static void EmitGoto(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, string[] args, int lineNum, string line)
    {
        if (args.Length < 1)
            throw new Exception($"第{lineNum}行：goto 需要标签参数: {line}");
        buf.Add(0x0A);
        EmitLabel(buf, patchList, labelPos, args[0]);
    }

    // ── IF：opcode + u16(varIdx) + u16(val) + addr(low) + addr(high) ──
    // C++ TAG_PARAM: "NA" → N=varIdx, A=目标地址
    // 反编译输出: If N A
    static void EmitIf(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, string[] args, int lineNum, string line)
    {
        if (args.Length < 2)
            throw new Exception($"第{lineNum}行：if 需要2个参数: varIdx label");
        buf.Add(0x0B);
        WriteU16(buf, ParseInt(args[0], lineNum, line)); // varIdx
        EmitLabel(buf, patchList, labelPos, args[1]);    // 目标地址
    }

    // ── IFCMP：opcode + u16(var1) + u16(var2) + addr ─────────
    // C++ TAG_PARAM: "NNA" → N N A
    static void EmitIfCmp(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, string[] args, int lineNum, string line)
    {
        if (args.Length < 3)
            throw new Exception($"第{lineNum}行：ifcmp 需要3个参数: var1 var2 label");
        buf.Add(0x15);
        WriteU16(buf, ParseInt(args[0], lineNum, line));
        WriteU16(buf, ParseInt(args[1], lineNum, line));
        EmitLabel(buf, patchList, labelPos, args[2]);
    }

    // ── CHOICE：opcode + 字符串1+\0 + 字符串2+\0 + 地址 ──────
    // C++ TAG_PARAM: "CCA" → C C A
    static void EmitChoice(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, string[] args, int lineNum, string line)
    {
        if (args.Length < 3)
            throw new Exception($"第{lineNum}行：Choice 需要3个参数: 字符串1 字符串2 标签");
        buf.Add(0x1F);
        WriteGBString(buf, args[0]);
        WriteGBString(buf, args[1]);
        EmitLabel(buf, patchList, labelPos, args[2]);
    }

    // ── BUY U：opcode + u16... + 0×u16（以0终止的u16列表）────
    // C++ TAG_PARAM: "U" → 读多个u16直到peek到0，然后跳过0字节
    // 反编译器输出格式：buy "7007 7008 7021 ..."（空格分隔的整数，包在引号内）
    static void EmitBuy(List<byte> buf, string[] args, int lineNum, string line)
    {
        buf.Add(0x1C);
        // 支持两种格式：
        // 1. buy "7007 7008 7021 ..."  → args[0] 是整个空格分隔字符串
        // 2. buy 7007 7008 7021 ...    → args 是多个独立整数
        if (args.Length == 1 && args[0].Contains(' '))
        {
            // 格式1：引号内空格分隔的字符串
            foreach (var part in args[0].Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                WriteU16(buf, ParseInt(part.Trim(), lineNum, line));
        }
        else
        {
            // 格式2：多个独立参数
            foreach (var a in args) WriteU16(buf, ParseInt(a, lineNum, line));
        }
        WriteU16(buf, 0); // 终止符
    }

    // ── 标签辅助：写地址占位，统一通过patchList回填（含scriptDataOffset）──
    static void EmitLabel(List<byte> buf, List<(int, string)> patchList,
        Dictionary<string, int> labelPos, string label)
    {
        // 无论前向还是后向标签，都走patchList回填
        // 因为最终地址 = script内偏移 + scriptDataOffset，此时scriptDataOffset尚未计算
        patchList.Add((buf.Count, label));
        WriteU16(buf, 0);
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
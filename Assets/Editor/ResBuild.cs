using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// ResBuild.cs — 将 ExRes/ 目录下解压的所有资源文件重新打包为 .lib 文件
///
/// ── .lib 文件完整格式（经二进制逆向全面验证）─────────────────────
///
///   0x0000~0x000F  文件头（16字节，原样写回，含 "LIB" 签名 + GB2312 游戏名）
///
///   0x0010~0x1FFE  索引区A（2725条 × 3字节）
///                    每条：[resType(1)] [type(1)] [index(1)]
///                    空条目：0xFF 0xFF 0xFF
///
///   0x2000~0x3FEE  索引区B（2725条 × 3字节）
///                    每条：[block(1)] [low(1)] [high(1)]
///                    偏移编码：offset = block×0x4000 | (high<<8|low)
///                    反算：block = offset/0x4000
///                          low   = (offset%0x4000) & 0xFF
///                          high  = (offset%0x4000) >> 8
///                    空条目：0x00 0x00 0x00
///
///   0x3FEF~0x3FFF  17字节 0xFF padding
///
///   0x4000~        数据区，若干 block，每 block 固定 0x4000 字节
///                    block 内布局：
///                      [+0x00:+0x04]  签名（ASCII，如 GUT\0 / MAP\0 / SRS\0 …）
///                      [+0x04:+0x0C]  全0（8字节保留）
///                      [+0x0C:+0x10]  block 内数据大小 uint32 LE
///                                      = 该 block 最后一条资源末尾 - block 起始地址
///                      [+0x10:...]    资源数据（顺序紧密排列，无内部 gap）
///                      [...:+0x4000]  0xFF padding
///
/// ── 打包规则 ──────────────────────────────────────────────────────
///   1. 同一 resType 的所有资源连续写入同一组 block（不同 resType 不混 block）
///   2. block 放满后自动续开新 block，签名相同
///   3. 资源不跨 block 边界：若剩余空间不足，先补 0xFF 再开新 block
///   4. 每种 resType 全部写完后，最后一个 block 末尾补 0xFF 到 0x4000 对齐
///   5. 索引区固定 2725 槽，有效条目按 (resType 写入顺序, type, index) 填入前部，
///      剩余槽位用 0xFF/0x00 填充
///
/// ── resType → block 签名对照 ─────────────────────────────────────
///   1=GUT  2=MAP  3=ARS  4=MRS  5=SRS  6=GRS
///   7=TIL  8=ACP  9=GDP  11=SUN(PIC)  12=MLR
/// </summary>
public class ResBuild
{
    // ══════════════════════════════════════════════════════
    //  常量
    // ══════════════════════════════════════════════════════
    private const int INDEX_COUNT = 2725;
    private const int BLOCK_SIZE = 0x4000;
    private const int BLOCK_HDR_SIZE = 0x0010;
    private const int BLOCK_DATA_CAP = BLOCK_SIZE - BLOCK_HDR_SIZE; // 0x3FF0

    // resType 写入顺序（与原版 block 排列完全一致）
    private static readonly int[] ResTypeOrder = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12 };

    // resType → block 签名（4字节，末尾补 \0）
    private static readonly Dictionary<int, string> ResTypeSig = new Dictionary<int, string>
    {
        { 1,"GUT" },{ 2,"MAP" },{ 3,"ARS" },{ 4,"MRS" },{ 5,"SRS" },{ 6,"GRS" },
        { 7,"TIL" },{ 8,"ACP" },{ 9,"GDP" },{ 11,"SUN" },{ 12,"MLR" },
    };

    // resType → ExRes 子目录名
    private static readonly Dictionary<int, string> ResTypeSubDir = new Dictionary<int, string>
    {
        { 1,"gut" },{ 2,"map" },{ 3,"ars" },{ 4,"mrs" },{ 5,"srs" },{ 6,"grs" },
        { 7,"til" },{ 8,"acp" },{ 9,"gdp" },{ 11,"pic" },{ 12,"mlr" },
    };

    // resType → 文件扩展名（不带点）
    private static readonly Dictionary<int, string> ResTypeExt = new Dictionary<int, string>
    {
        { 1,"gut" },{ 2,"map" },{ 3,"ars" },{ 4,"mrs" },{ 5,"srs" },{ 6,"grs" },
        { 7,"til" },{ 8,"acp" },{ 9,"gdp" },{ 11,"pic" },{ 12,"mlr" },
    };

    // 原始文件头（16字节）：直接从原版 lib 取得，含 LIB 签名 + GB2312 游戏名
    private static readonly byte[] LibHeader = {
        0x4C,0x49,0x42, 0xB7,0xFC,0xC4,0xA7,0xBC,0xC7, 0x00,0x00,0x00, 0x61,0x0E, 0xFF,0xFF
    };

    // ══════════════════════════════════════════════════════
    //  菜单入口
    // ══════════════════════════════════════════════════════
    [MenuItem("工具/打包资源为 .lib")]
    public static void Build()
    {
        string exResDir = Application.dataPath + "/../ExRes";
        string outPath = Application.dataPath + "/../ExRes/output.lib";

        if (!Directory.Exists(exResDir))
        {
            Debug.LogError($"[ResBuild] ExRes 目录不存在: {exResDir}");
            return;
        }

        try
        {
            double t0 = EditorApplication.timeSinceStartup;
            BuildLib(exResDir, outPath);
            double elapsed = EditorApplication.timeSinceStartup - t0;
            Debug.Log($"[ResBuild] 耗时 {elapsed:F2}s");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ResBuild] 打包异常: {e}");
        }
    }

    // ══════════════════════════════════════════════════════
    //  主打包流程
    // ══════════════════════════════════════════════════════
    private static void BuildLib(string exResDir, string outPath)
    {
        // ── Step 1：扫描 ExRes/ 目录，收集所有资源文件 ──────
        // groups[resType] = [(fileType, fileIndex, rawBytes), ...]
        // 按 (type, index) 排序，与原版排列方向一致
        var groups = new Dictionary<int, List<(int type, int index, byte[] data)>>();
        foreach (int rt in ResTypeOrder) groups[rt] = new List<(int, int, byte[])>();

        int total = 0, skipped = 0;

        foreach (int rt in ResTypeOrder)
        {
            string subDir = ResTypeSubDir[rt];
            string dir = Path.Combine(exResDir, subDir);
            if (!Directory.Exists(dir)) { Debug.LogWarning($"[ResBuild] 目录不存在，跳过: {dir}"); continue; }

            string ext = ResTypeExt[rt];
            foreach (string fpath in Directory.GetFiles(dir))
            {
                if (!Path.GetExtension(fpath).TrimStart('.').Equals(ext, StringComparison.OrdinalIgnoreCase))
                    continue;

                byte[] raw;
                try { raw = File.ReadAllBytes(fpath); }
                catch (Exception e) { Debug.LogWarning($"[ResBuild] 读取失败 {fpath}: {e.Message}"); skipped++; continue; }

                if (raw.Length < 2) { skipped++; continue; }

                int fileType = raw[0] & 0xFF;
                int fileIndex = raw[1] & 0xFF;

                // 精确截取有效数据长度（文件可能含尾部冗余字节）
                int sz = GetResourceSize(rt, raw);
                if (sz <= 0)
                {
                    Debug.LogWarning($"[ResBuild] 大小计算失败，跳过: {fpath}  resType={rt}");
                    skipped++; continue;
                }

                byte[] payload;
                if (sz <= raw.Length)
                {
                    payload = new byte[sz];
                    Array.Copy(raw, payload, sz);
                }
                else
                {
                    Debug.LogWarning($"[ResBuild] 文件数据不足（需{sz}，有{raw.Length}），跳过: {fpath}");
                    skipped++; continue;
                }

                groups[rt].Add((fileType, fileIndex, payload));
                total++;
            }

            // 按 (type, index) 升序排列，保证 block 分配可重复
            groups[rt].Sort((a, b) =>
            {
                int c = a.type.CompareTo(b.type);
                return c != 0 ? c : a.index.CompareTo(b.index);
            });
        }

        Debug.Log($"[ResBuild] 收集: {total} 个文件，跳过: {skipped} 个");

        // ── Step 2：构建输出字节流 ──────────────────────────
        // 先占位整个文件头区域（0x4000 字节 = 文件头+索引区+padding）
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, true);

        w.Write(new byte[BLOCK_SIZE]); // 0x0000~0x3FFF 占位，后续回填

        // 索引条目列表（写完数据区后填入索引区）
        var indexList = new List<(int resType, int type, int index, int offset)>();

        // 逐 resType 写入数据区
        foreach (int rt in ResTypeOrder)
        {
            var list = groups[rt];
            if (list.Count == 0) continue;

            // 准备 block header 签名（4字节 ASCII + \0）
            byte[] sigBytes = new byte[4];
            byte[] sigAscii = Encoding.ASCII.GetBytes(ResTypeSig[rt]);
            Array.Copy(sigAscii, sigBytes, sigAscii.Length);

            int blockStart = -1; // 当前 block 在输出文件中的绝对起始偏移
            long blockSizePos = -1; // 当前 block 的 size 字段在文件中的偏移（用于回填）

            foreach (var (fileType, fileIndex, payload) in list)
            {
                int payloadSize = payload.Length;

                // 判断是否需要开新 block
                bool needNewBlock = (blockStart < 0);
                if (!needNewBlock)
                {
                    long dataWritten = ms.Position - (blockStart + BLOCK_HDR_SIZE);
                    if (dataWritten + payloadSize > BLOCK_DATA_CAP)
                        needNewBlock = true;
                }

                if (needNewBlock)
                {
                    // 回填上一个 block 的 size，并补 0xFF 到 block 边界
                    if (blockStart >= 0)
                        FinalizeBlock(w, ms, blockStart, blockSizePos);

                    // 写新 block header
                    blockStart = (int)ms.Position;
                    w.Write(sigBytes);          // [+0x00] 签名 4字节
                    w.Write(new byte[8]);       // [+0x04] 保留 8字节，全0
                    blockSizePos = ms.Position; // [+0x0C] size 字段位置，先写0占位
                    w.Write((uint)0);           // [+0x0C] size 占位 4字节
                    // 此时 ms.Position = blockStart + 0x10，数据区从这里开始
                }

                // 记录索引条目（使用写入前的当前位置作为偏移）
                indexList.Add((rt, fileType, fileIndex, (int)ms.Position));

                // 写入资源数据
                w.Write(payload);
            }

            // 当前 resType 全部写完：回填最后一个 block 的 size，补 0xFF 对齐
            if (blockStart >= 0)
                FinalizeBlock(w, ms, blockStart, blockSizePos);
        }

        w.Flush();
        byte[] fileData = ms.ToArray();

        // ── Step 3：填写索引区 A 和 B ──────────────────────
        // 所有槽先初始化为空条目
        // 索引A 空条目 = FF FF FF
        // 索引B 空条目 = 00 00 00
        for (int i = 0; i < INDEX_COUNT; i++)
        {
            int posA = 0x0010 + i * 3;
            fileData[posA] = fileData[posA + 1] = fileData[posA + 2] = 0xFF;

            int posB = 0x2000 + i * 3;
            fileData[posB] = fileData[posB + 1] = fileData[posB + 2] = 0x00;
        }

        // 按 indexList 顺序（即写入顺序）填入有效索引槽
        int slot = 0;
        foreach (var (resType, type, index, offset) in indexList)
        {
            if (slot >= INDEX_COUNT)
            {
                Debug.LogError($"[ResBuild] 索引槽不足（最大{INDEX_COUNT}，已用{slot}）！");
                break;
            }

            // 索引区A
            int posA = 0x0010 + slot * 3;
            fileData[posA] = (byte)resType;
            fileData[posA + 1] = (byte)type;
            fileData[posA + 2] = (byte)index;

            // 索引区B：编码偏移
            int block = offset / BLOCK_SIZE;
            int rem = offset % BLOCK_SIZE;
            fileData[0x2000 + slot * 3] = (byte)block;
            fileData[0x2000 + slot * 3 + 1] = (byte)(rem & 0xFF);
            fileData[0x2000 + slot * 3 + 2] = (byte)((rem >> 8) & 0xFF);

            slot++;
        }

        // ── Step 4：填写文件头和 padding ────────────────────
        // 文件头（0x0000~0x000F）
        Array.Copy(LibHeader, 0, fileData, 0, LibHeader.Length);

        // 索引B结束到 0x4000 的 padding（0x3FEF~0x3FFF = 17字节 FF）
        int padStart = 0x2000 + INDEX_COUNT * 3; // = 0x3FEF
        for (int i = padStart; i < BLOCK_SIZE && i < fileData.Length; i++)
            fileData[i] = 0xFF;

        // ── Step 5：写出文件 ─────────────────────────────────
        string outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);

        File.WriteAllBytes(outPath, fileData);

        Debug.Log($"[ResBuild] ✓ 打包完成");
        Debug.Log($"  输出路径:   {outPath}");
        Debug.Log($"  文件大小:   {fileData.Length:N0} 字节 (0x{fileData.Length:X})");
        Debug.Log($"  有效索引:   {slot} / {INDEX_COUNT} 槽");
        Debug.Log($"  资源总数:   {total} 个");

        // 各 resType 统计
        var sb = new System.Text.StringBuilder("[ResBuild] 各类型数量: ");
        foreach (int rt in ResTypeOrder)
            sb.Append($"{ResTypeSig[rt]}={groups[rt].Count} ");
        Debug.Log(sb.ToString());
    }

    // ══════════════════════════════════════════════════════
    //  回填 block size + 补 0xFF padding 到 block 边界
    //  block size = 该 block 最后一条资源末尾 - block 起始地址
    // ══════════════════════════════════════════════════════
    private static void FinalizeBlock(BinaryWriter w, MemoryStream ms,
        int blockStart, long blockSizePos)
    {
        long dataEnd = ms.Position;
        uint sizeVal = (uint)(dataEnd - blockStart); // = last_entry_end - block_start

        // 回填 size 字段
        long savedPos = ms.Position;
        ms.Seek(blockSizePos, SeekOrigin.Begin);
        w.Write(sizeVal);
        ms.Seek(savedPos, SeekOrigin.Begin);

        // 补 0xFF 到下一个 BLOCK_SIZE 对齐边界
        long next = ((dataEnd + BLOCK_SIZE - 1) / BLOCK_SIZE) * BLOCK_SIZE;
        int pad = (int)(next - dataEnd);
        if (pad > 0 && pad < BLOCK_SIZE)
        {
            var ff = new byte[pad];
            Array.Fill(ff, (byte)0xFF);
            w.Write(ff);
        }
    }

    // ══════════════════════════════════════════════════════
    //  计算各 resType 的精确数据字节数
    //  data 必须是从资源起始偏移到文件末尾的完整切片（不能截断），
    //  尤其对变长的 SRS 需要足够长的数据才能遍历完所有 ResImage
    // ══════════════════════════════════════════════════════
    public static int GetResourceSize(int resType, byte[] data)
    {
        if (data == null || data.Length < 2) return -1;

        switch (resType)
        {
            // ── GUT：变长 ────────────────────────────────
            // header = 0x1B，length 字段在 [0x18:0x1A]（uint16 LE）
            // length = numSceneEvent*2 + 3 + scriptLen
            case 1:
                {
                    if (data.Length < 0x1B) return -1;
                    int length = ((data[0x19] & 0xFF) << 8) | (data[0x18] & 0xFF);
                    int numEv = data[0x1A] & 0xFF;
                    int scrLen = length - numEv * 2 - 3;
                    if (scrLen < 0) return -1;
                    return 0x1B + numEv * 2 + scrLen;
                }

            // ── MAP：变长 ────────────────────────────────
            // data size = MapWidth × MapHeight × 2，尺寸在 [0x10][0x11]
            case 2:
                {
                    if (data.Length < 0x12) return -1;
                    int w = data[0x10] & 0xFF;
                    int h = data[0x11] & 0xFF;
                    if (w == 0 || h == 0) return -1;
                    return 0x12 + w * h * 2;
                }

            // ── ARS：固定（按 subType 区分）──────────────
            case 3:
                {
                    int st = data[0] & 0xFF;
                    switch (st)
                    {
                        case 1: return 0x39; // PlayerCharacter
                        case 2: return 0x17; // NPC
                        case 3: return 0x30; // Monster
                        case 4: return 0x17; // SceneObj（同 NPC 大小）
                        default: return -1;
                    }
                }

            // ── MRS：固定 0x80 字节 ──────────────────────
            case 4: return 0x80;

            // ── SRS：变长，遍历 FrameHeader + ResImage[] ─
            // 注意：data 必须足够长（SRS 可达几十KB），不可传截断切片
            case 5:
                {
                    if (data.Length < 6) return -1;
                    int fc = data[2] & 0xFF; // FrameCount
                    int ic = data[3] & 0xFF; // ImageCount
                    int ptr = 6 + fc * 5;     // 跳过 FrameHeader[fc][5]
                    for (int i = 0; i < ic; i++)
                    {
                        if (ptr + 6 > data.Length) return -1;
                        int iw = data[ptr + 2] & 0xFF;
                        int ih = data[ptr + 3] & 0xFF;
                        int inum = data[ptr + 4] & 0xFF;
                        int imod = data[ptr + 5] & 0xFF;
                        int row = (iw + 7) / 8;
                        ptr += 6 + inum * row * ih * imod;
                    }
                    return ptr; // ptr 即从 data[0] 起的总字节数
                }

            // ── GRS：固定 0x86 字节 ──────────────────────
            case 6: return 0x86;

            // ── ResImage（TIL/ACP/GDP/PIC）──────────────
            // len = Number × ceil(W/8) × Height × Mode
            case 7:
            case 8:
            case 9:
            case 11:
                {
                    if (data.Length < 6) return -1;
                    int w = data[2] & 0xFF;
                    int h = data[3] & 0xFF;
                    int num = data[4] & 0xFF;
                    int mode = data[5] & 0xFF;
                    if (mode == 0) return 6; // 无像素数据
                    int row = (w + 7) / 8;   // ceil(W/8)，与 ResImage.SetData 完全一致
                    return 6 + num * row * h * mode;
                }

            // ── MLR：变长（subType 由 data[0] 区分）─────
            // type=1 MagicChain：3 + MaxLevel×2
            // type=2 LevelupChain：4 + MaxLevel×20
            case 12:
                {
                    int st = data[0] & 0xFF;
                    int maxLevel = data[2] & 0xFF;
                    if (maxLevel <= 0) return -1;
                    if (st == 1) return 3 + maxLevel * 2;
                    if (st == 2) return 4 + maxLevel * 20;
                    return -1;
                }

            default: return -1;
        }
    }
}
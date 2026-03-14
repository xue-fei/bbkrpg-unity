using BBKRPGSimulator;
using I18N.CJK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public class ResExtractor
{
    static Encoding gb2312Encoding = null;

    private static string Name;

    /// <summary>
    /// 库文件内容缓存
    /// </summary>
    private static byte[] buf;

    /// <summary>
    /// 索引区A/B 总条目数（固定 2725 = (0x2000 - 0x10) / 3）
    /// </summary>
    private const int INDEX_COUNT = (0x2000 - 0x10) / 3;

    /// <summary>
    /// 保存资源数据相对文件首字节的偏移量
    /// key = (resType << 16) | (type << 8) | index
    /// </summary>
    private static Dictionary<int, int> _dataOffset = new Dictionary<int, int>(2048);

    [MenuItem("工具/解压资源")]
    public static void ExRes()
    {
        gb2312Encoding = new CP936();

        string datPath = Application.streamingAssetsPath + "/Game/伏魔记经典版.lib";
        buf = File.ReadAllBytes(datPath);

        Name = Utilities.GetGameName(buf);
        Debug.Log("游戏名: " + Name);

        GetAllResOffset();
        ExtractAll();
    }

    // ── 索引表解析 ────────────────────────────────────────
    // 索引区A: 0x0010 起，每条3字节 (resType, type, index)
    // 索引区B: 0x2000 起，每条3字节 (block, low, high)
    // A[n] 与 B[n] 严格一一对应，resType=0xFF 为空条目跳过
    private static void GetAllResOffset()
    {
        _dataOffset.Clear();

        int posA = 0x0010;
        int posB = 0x2000;

        for (int n = 0; n < INDEX_COUNT; n++, posA += 3, posB += 3)
        {
            int resType = buf[posA];
            int type = buf[posA + 1];
            int index = buf[posA + 2] & 0xFF;

            if (resType == 0xFF) // 空条目
                continue;

            int block = buf[posB];
            int low = buf[posB + 1];
            int high = buf[posB + 2];
            int offset = block * 0x4000 | (high << 8 | low);

            int key = GetKey(resType, type, index);
            if (!_dataOffset.ContainsKey(key))
            {
                _dataOffset.Add(key, offset);
            }
        }

        Debug.Log($"索引表解析完成，有效条目: {_dataOffset.Count}");
    }

    // ── 提取所有资源 ──────────────────────────────────────
    private static void ExtractAll()
    {
        int gutSaved = 0, gutSkipped = 0;
        int mapSaved = 0, mapSkipped = 0;
        int srsSaved = 0, srsSkipped = 0;

        int tilSaved = 0, tilSkipped = 0,
            acpSaved = 0, acpSkipped = 0,
            gdpSaved = 0, gdpSkipped = 0;
        int mrsSaved = 0, mrsSkipped = 0;
        int grsSaved = 0, grsSkipped = 0;
        int mlrSaved = 0, mlrSkipped = 0;

        foreach (var kv in _dataOffset)
        {
            int resType = (kv.Key >> 16) & 0xFF;
            int type = (kv.Key >> 8) & 0xFF;
            int offset = kv.Value;

            if (resType == 1) // RES_GUT 剧情脚本
            {
                if (ExtractGut(offset)) gutSaved++;
                else gutSkipped++;
            }
            else if (resType == 2) // RES_MAP 地图
            {
                if (ExtractMap(offset)) mapSaved++;
                else mapSkipped++;
            }
            else if (resType == 5)
            {
                if (ExtractSrs(offset)) srsSaved++;
                else srsSkipped++;
            }
            else if (resType == 7)
            {
                if (ExtractResImage(offset, "til")) tilSaved++;
                else tilSkipped++;
            }
            else if (resType == 8)
            {
                if (ExtractResImage(offset, "acp")) acpSaved++;
                else acpSkipped++;
            }
            else if (resType == 9)
            {
                if (ExtractResImage(offset, "gdp")) gdpSaved++;
                else gdpSkipped++;
            }
            else if (resType == 4)
            {
                if (ExtractMrs(offset)) mrsSaved++;
                else mrsSkipped++;
            }
            else if (resType == 6)
            {
                if (ExtractGrs(offset)) grsSaved++;
                else grsSkipped++;
            }
            else if (resType == 12) // RES_MLR 链资源（type=1魔法链, type=2升级链）
            {
                if (ExtractMlr(offset, type)) mlrSaved++;
                else mlrSkipped++;
            }
        }

        Debug.Log($"gut: 保存 {gutSaved} 个，跳过 {gutSkipped} 个");
        Debug.Log($"map: 保存 {mapSaved} 个，跳过 {mapSkipped} 个");
        Debug.Log($"til: 保存 {tilSaved} 个，跳过 {tilSkipped} 个");
        Debug.Log($"acp: 保存 {acpSaved} 个，跳过 {acpSkipped} 个");
        Debug.Log($"gdp: 保存 {gdpSaved} 个，跳过 {gdpSkipped} 个");
        Debug.Log($"mrs: 保存 {mrsSaved} 个，跳过 {mrsSkipped} 个");
        Debug.Log($"grs: 保存 {grsSaved} 个，跳过 {grsSkipped} 个");
        Debug.Log($"mlr: 保存 {mlrSaved} 个，跳过 {mlrSkipped} 个");
    }

    // ── gut 块解析与保存 ──────────────────────────────────
    // gut 块格式：
    //   +0x00        Type           1字节
    //   +0x01        Index          1字节
    //   +0x02~+0x17  Description    23字节，GB2312，0xCC填充，\0结尾
    //   +0x18~+0x19  Length         uint16 小端（脚本数据段长度）
    //   +0x1A        NumSceneEvent  uint8
    //   +0x1B        SceneEvent[]   NumSceneEvent * 2 字节
    //   +0x1B+N*2    ScriptData     (Length - NumSceneEvent*2 - 3) 字节
    //   totalLen = 0x1B + NumSceneEvent*2 + ScriptData长度
    private static bool ExtractGut(int offset)
    {
        if (offset + 0x1B > buf.Length)
        {
            Debug.LogWarning($"[gut] offset=0x{offset:X} 块头超出文件范围，跳过");
            return false;
        }

        int type = buf[offset];
        int index = buf[offset + 1];

        int length = ((buf[offset + 0x19] & 0xFF) << 8)
                           | (buf[offset + 0x18] & 0xFF);
        int numSceneEvent = buf[offset + 0x1A] & 0xFF;
        int scriptLen = length - numSceneEvent * 2 - 3;

        if (scriptLen <= 0)
        {
            Debug.LogWarning($"[gut] offset=0x{offset:X} {type}-{index} scriptLen={scriptLen} 无效，跳过");
            return false;
        }

        int totalLen = 0x1B + numSceneEvent * 2 + scriptLen;
        if (offset + totalLen > buf.Length)
        {
            Debug.LogWarning($"[gut] offset=0x{offset:X} {type}-{index} 块长度 {totalLen} 超出文件范围，跳过");
            return false;
        }

        string description = GetString(buf, offset + 2);

        byte[] rawBlock = new byte[totalLen];
        Array.Copy(buf, offset, rawBlock, 0, totalLen);

        string dir = Application.dataPath + "/../ExRes/gut";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string savePath = dir + $"/{type}-{index}.gut";
        File.WriteAllBytes(savePath, rawBlock);
        Debug.Log($"[gut] 已保存: {type}-{index}.gut  desc={description}  size={totalLen}  offset=0x{offset:X}");
        return true;
    }

    // ── map 块解析与保存 ──────────────────────────────────
    // map 块格式（来自 ResMap.SetData）：
    //   +0x00        Type       1字节
    //   +0x01        Index      1字节
    //   +0x02        tilIndex   1字节（该地图使用的 tile 图块资源索引）
    //   +0x03~+0x0F  MapName    13字节，GB2312，0xCC填充，\0结尾
    //   +0x10        MapWidth   1字节
    //   +0x11        MapHeight  1字节
    //   +0x12        _data      MapWidth * MapHeight * 2 字节
    //                           每格2字节：低字节最高位=是否可行走，低7位=tile索引；高字节=事件号
    //   totalLen = 0x12 + MapWidth * MapHeight * 2
    private static bool ExtractMap(int offset)
    {
        if (offset + 0x12 > buf.Length)
        {
            Debug.LogWarning($"[map] offset=0x{offset:X} 块头超出文件范围，跳过");
            return false;
        }

        int type = buf[offset];
        int index = buf[offset + 1];
        int tilIndex = buf[offset + 2];
        int mapWidth = buf[offset + 0x10];
        int mapHeight = buf[offset + 0x11];

        if (mapWidth <= 0 || mapHeight <= 0)
        {
            Debug.LogWarning($"[map] offset=0x{offset:X} {type}-{index} 宽高无效 w={mapWidth} h={mapHeight}，跳过");
            return false;
        }

        int totalLen = 0x12 + mapWidth * mapHeight * 2;
        if (offset + totalLen > buf.Length)
        {
            Debug.LogWarning($"[map] offset=0x{offset:X} {type}-{index} 块长度 {totalLen} 超出文件范围，跳过");
            return false;
        }

        string mapName = GetString(buf, offset + 3);

        byte[] rawBlock = new byte[totalLen];
        Array.Copy(buf, offset, rawBlock, 0, totalLen);

        string dir = Application.dataPath + "/../ExRes/map";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string savePath = dir + $"/{type}-{index}-{mapName}.map";
        File.WriteAllBytes(savePath, rawBlock);
        Debug.Log($"[map] 已保存: {type}-{index}.map  name={mapName}  w={mapWidth} h={mapHeight}  size={totalLen}  offset=0x{offset:X}");
        return true;
    }

    // ── srs 块解析与保存 ──────────────────────────────────────────
    // srs 块格式（来自 ResSrs.SetData）：
    //   +0x00        Type        1字节
    //   +0x01        Index       1字节
    //   +0x02        FrameCount  1字节
    //   +0x03        ImageCount  1字节
    //   +0x04        StartFrame  1字节
    //   +0x05        EndFrame    1字节
    //   +0x06        FrameHeader[FrameCount][5]   每帧5字节
    //                  [x(1), y(1), show(1), nshow(1), imageIdx(1)]
    //   +0x06+FrameCount*5   ResImage[ImageCount]  连续变长（见上）
    //
    // totalLen = 遍历累加所有 ResImage.GetBytesCount() 后的 ptr - offset
    //
    // 注意：部分块数据跨越 0x4000 block 边界，边界后有 0xFF padding，
    //       但 totalLen 只取实际数据长度，padding 不计入。
    private static bool ExtractSrs(int offset)
    {
        if (offset + 6 > buf.Length)
        {
            Debug.LogWarning($"[srs] offset=0x{offset:X} 块头超出文件范围，跳过");
            return false;
        }

        int type = buf[offset];
        int index = buf[offset + 1] & 0xFF;
        int frameCount = buf[offset + 2] & 0xFF;
        int imageCount = buf[offset + 3] & 0xFF;

        // 跳过帧头表（每帧固定 5 字节）
        int ptr = offset + 6 + frameCount * 5;

        // 遍历每个 ResImage 累加大小
        for (int i = 0; i < imageCount; i++)
        {
            if (ptr + 6 > buf.Length)
            {
                Debug.LogWarning($"[srs] offset=0x{offset:X} {type}-{index} img[{i}] 超出文件范围，跳过");
                return false;
            }
            ptr += ResImageGetBytesCount(ptr);
        }

        int totalLen = ptr - offset;

        byte[] rawBlock = new byte[totalLen];
        Array.Copy(buf, offset, rawBlock, 0, totalLen);

        string dir = Application.dataPath + "/../ExRes/srs";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string savePath = dir + $"/{type}-{index}.srs";
        File.WriteAllBytes(savePath, rawBlock);
        Debug.Log($"[srs] 已保存: {type}-{index}.srs  fc={frameCount} ic={imageCount}  size={totalLen}  offset=0x{offset:X}");
        return true;
    }

    // ── ResImage 大小计算 ─────────────────────────────────────────
    // ResImage 格式：
    //   +0x00  Type   1字节
    //   +0x01  Index  1字节
    //   +0x02  Width  1字节（像素）
    //   +0x03  Height 1字节（像素）
    //   +0x04  ?      1字节（固定=1）
    //   +0x05  Mode   1字节（0=无数据, 1=1bpp无对齐, 2=2bpp偶数对齐）
    //   +0x06  PixelData
    //
    // 公式：
    //   raw  = ceil(W * mode / 8)
    //   row  = ceil(raw / mode) * mode   // 对齐到 mode 字节
    //   size = 6 + row * H
    //
    // 验证样本（全部通过）：
    //   W= 8 H= 9 mode=1 → size= 15
    //   W=16 H=16 mode=2 → size= 70
    //   W=30 H=24 mode=2 → size=198
    //   W=24 H=28 mode=2 → size=174
    //   W=43 H=41 mode=2 → size=498  (raw=11 → row=12)
    //   W=121 H=25 mode=1 → size=406
    //   W=159 H= 2 mode=1 → size= 46
    private static int ResImageGetBytesCount(int offset)
    {
        int w = buf[offset + 2] & 0xFF;
        int h = buf[offset + 3] & 0xFF;
        int mode = buf[offset + 5] & 0xFF;
        if (mode == 0) return 6;                         // 无像素数据
        int raw = (w * mode + 7) / 8;                   // 每行原始字节数
        int row = (raw + mode - 1) / mode * mode;        // 对齐到 mode 字节
        return 6 + row * h;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ResExtractor.cs — 新增 RES_TIL / RES_ACP / RES_GDP 提取支持
    //  同一个方法 ExtractResImage 处理全部三种 resType
    // ═══════════════════════════════════════════════════════════════════════  
    // ResImage 块格式（来自 ResImage.SetData）：
    //   +0x00  Type        1字节
    //   +0x01  Index       1字节
    //   +0x02  Width       1字节（像素宽）
    //   +0x03  Height      1字节（像素高）
    //   +0x04  Number      1字节（切片数量）
    //   +0x05  Mode        1字节（1=不透明1bpp, 2=透明2bpp）
    //   +0x06  PixelData   Number * ceil(Width/8) * Height * Mode 字节
    //
    // GetBytesCount（与 ResImage.GetBytesCount() 完全等价）：
    //   row  = (Width + 7) / 8       // ceil(W/8)，每切片每行字节数
    //   len  = Number * row * Height * Mode
    //   size = 6 + len
    //
    // 注意：部分块末尾有 0xFF padding 填充到下一个 0x4000 block 边界，
    //       padding 不属于资源数据，提取时只保存 size 字节。
    //
    // 覆盖资源类型：
    //   resType=7  RES_TIL  tile图片   8条
    //   resType=8  RES_ACP  角色图片  124条
    //   resType=9  RES_GDP  道具图片  214条

    private static bool ExtractResImage(int offset, string ext)
    {
        if (offset + 6 > buf.Length)
        {
            Debug.LogWarning($"[{ext}] offset=0x{offset:X} 块头超出文件范围，跳过");
            return false;
        }

        int type = buf[offset];
        int index = buf[offset + 1] & 0xFF;
        int width = buf[offset + 2] & 0xFF;
        int height = buf[offset + 3] & 0xFF;
        int number = buf[offset + 4] & 0xFF;
        int mode = buf[offset + 5] & 0xFF;

        // 与 ResImage.SetData 中的 len 计算完全一致：
        //   int len = Number * (Width/8 + (Width%8!=0?1:0)) * Height * buf[offset+5];
        int row = (width + 7) / 8;           // ceil(W/8)
        int dataLen = number * row * height * mode;
        int totalLen = 6 + dataLen;

        if (totalLen <= 6 && number > 0)
        {
            Debug.LogWarning($"[{ext}] offset=0x{offset:X} {type}-{index} dataLen={dataLen} 无效，跳过");
            return false;
        }

        if (offset + totalLen > buf.Length)
        {
            Debug.LogWarning($"[{ext}] offset=0x{offset:X} {type}-{index} 块长度 {totalLen} 超出文件范围，跳过");
            return false;
        }

        byte[] rawBlock = new byte[totalLen];
        Array.Copy(buf, offset, rawBlock, 0, totalLen);

        string dir = Application.dataPath + $"/../ExRes/{ext}";
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string savePath = dir + $"/{type}-{index}.{ext}";
        File.WriteAllBytes(savePath, rawBlock);
        Debug.Log($"[{ext}] 已保存: {type}-{index}.{ext}  W={width} H={height} num={number} mode={mode}  size={totalLen}  offset=0x{offset:X}");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ResExtractor.cs — 新增 RES_MRS 魔法资源提取支持
    // ═══════════════════════════════════════════════════════════════════════

    // MRS 块格式（来自 BaseMagic + 各子类 SetOtherData 逆向）：
    //
    //   +0x00        Type         1字节  （魔法大类：1攻击 2增强 3恢复 4辅助 5特殊）
    //   +0x01        Index        1字节
    //   +0x02        Power        1字节  （基础效能/威力）
    //   +0x03        Flags        1字节  （bit7=1 表示群体，其余位保留）
    //   +0x04        CostMp       1字节  （施展消耗真气）
    //   +0x05        InitLevel    1字节  （习得等级要求）
    //   +0x06~+0x10  Name         11字节 （GB2312，\0结尾，0xFF填充）
    //   +0x11        0xFF         （固定填充）
    //   +0x12~+0x19  SubclassData 8字节  （各子类 SetOtherData 读取，见下表）
    //   +0x1A~+0x7E  Description  变长   （GB2312，\0结尾，0xFF填充至+0x7F）
    //   +0x7F        0xFF
    //   ─────────────────────────────────────────────────────────────────
    //   固定大小 = 0x80 = 128 字节（经全部107条二进制验证）
    //
    // SubclassData (+0x12~+0x19) 各类型字段含义：
    //
    //   Type=1 MagicAttack（攻击型）：
    //     +0x12~+0x13  AffectHp    int16 LE（>0损伤敌人HP，<0吸取敌人HP）
    //     +0x14~+0x15  AffectMp    int16 LE（>0损伤敌人MP，<0吸取敌人MP）
    //     +0x16        AffectDf    uint8  （0~100，敌人防御减弱%）
    //     +0x17        AffectAt    uint8  （0~100，敌人攻击减弱%）
    //     +0x18        AffectBuff  uint8  （高4位=持续回合，低4位=毒乱封眠）
    //     +0x19        AffectSpeed uint8  （0~100，敌人速度减慢%）
    //
    //   Type=2 MagicEnhance（增强型）：
    //     +0x16        Defend       uint8  （0~100，己方防御增强%）
    //     +0x17        Attack       uint8  （0~100，己方攻击增强%）
    //     +0x18        EnhanceRound uint8  （高4位=持续回合）
    //     +0x19        Speed        uint8  （0~100，己方速度增快%）
    //
    //   Type=3 MagicRestore（恢复型）：
    //     +0x12~+0x13  RestoreHp   uint16 LE（0~8000，恢复HP值）
    //     +0x18        DefendDeBuff uint8  （低4位=可解除的debuff标志）
    //
    //   Type=4 MagicAuxiliary（辅助型）：
    //     +0x12~+0x13  HpPercent   uint16 LE（0~100，起死回生HP恢复%）
    //
    //   Type=5 MagicSpecial（特殊型，妙手空空）：
    //     无额外字段（+0x12~+0x19 保留）
    //
    private static bool ExtractMrs(int offset)
    {
        // MRS 块固定 128 字节
        const int TOTAL_LEN = 0x80;

        if (offset + TOTAL_LEN > buf.Length)
        {
            Debug.LogWarning($"[mrs] offset=0x{offset:X} 块长度超出文件范围，跳过");
            return false;
        }

        int type = buf[offset];
        int index = buf[offset + 1] & 0xFF;

        byte[] rawBlock = new byte[TOTAL_LEN];
        Array.Copy(buf, offset, rawBlock, 0, TOTAL_LEN);

        string dir = Application.dataPath + "/../ExRes/mrs";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        // 读取名称供日志输出
        string name = GetString(buf, offset + 6);

        string savePath = dir + $"/{type}-{index}-{name}.mrs";
        File.WriteAllBytes(savePath, rawBlock);
        Debug.Log($"[mrs] 已保存: {type}-{index}.mrs  name={name}  offset=0x{offset:X}");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ResExtractor.cs — 新增 RES_GRS 道具资源提取支持
    // ═══════════════════════════════════════════════════════════════════════

    // GRS 块格式（来自 BaseGoods.SetData 逆向，经全部216条二进制验证）：
    //
    //   +0x00        Type         1字节  （道具大类 1~14，见下表）
    //   +0x01        Index        1字节
    //   +0x02        BlockSize    1字节  （固定 0x86=134，即本块总长）
    //   +0x03        EnableLevel  1字节  （bit0=主角1可用, bit1=主角2…）
    //   +0x04        SumRound     1字节  （持续回合，0=永久）
    //   +0x05        ImageIndex   1字节  （对应 ResImage GDP 的图片索引）
    //   +0x06~+0x11  Name         12字节 （GB2312，\0结尾，0xFF填充）
    //   +0x12~+0x13  BuyPrice     uint16 LE
    //   +0x14~+0x15  SellPrice    uint16 LE
    //   +0x16~+0x1D  SubclassData 8字节  （各子类字段，见下表）
    //   +0x1E~+0x83  Description  102字节（GB2312，\0结尾，0xFF填充）
    //   +0x84~+0x85  EventId      uint16 LE（非0时装备触发事件）
    //   ─────────────────────────────────────────────────────────────────
    //   固定大小 = 0x86 = 134 字节（经全部 216 条验证，包含 block 边界 padding 情况）
    //
    // Type 分类（来自 DatLib.InternalGetGoods）：
    //   1  GoodsEquipment    护头
    //   2  GoodsEquipment    护身
    //   3  GoodsEquipment    护脚
    //   4  GoodsEquipment    护肩
    //   5  GoodsEquipment    护腕
    //   6  GoodsDecorations  装饰品
    //   7  GoodsWeapon       武器
    //   8  GoodsHiddenWeapon 暗器
    //   9  GoodsMedicine     药品
    //   10 GoodsLifeMedicine 生命药
    //   11 GoodsAttributesMedicine 属性药
    //   12 GoodsStimulant    兴奋剂
    //   13 GoodsTudun        土遁
    //   14 GoodsDrama        剧情道具
    //
    // SubclassData (+0x16~+0x1D) 各类型字段（二进制观察，无子类源码）：
    //   type=1~5  装备类：+0x18=防御加成, +0x1B=特殊属性加成
    //   type=6    装饰类：+0x1A=攻击%, +0x1C=HP加成, +0x1D=持续回合
    //   type=7    武器类：+0x19=攻击加成
    //   type=8~9  暗器/药：+0x16=效果值低字节, +0x1A=数量/次数, +0x1B=目标
    //   type=10   生命药：+0x17=HP恢复量, +0x19=HP%, +0x1B=MP恢复
    //   type=11   属性药：各字节对应不同属性加成
    //   type=12   兴奋剂：单字节属性临时加成
    //   type=13   土遁：无额外字段
    //   type=14   剧情：+0x1B=剧情参数
    //
    private static bool ExtractGrs(int offset)
    {
        // GRS 块固定 134 字节（由 +0x02 字段声明，全部条目均为 0x86）
        const int TOTAL_LEN = 0x86;

        if (offset + TOTAL_LEN > buf.Length)
        {
            Debug.LogWarning($"[grs] offset=0x{offset:X} 块长度超出文件范围，跳过");
            return false;
        }

        int type = buf[offset];
        int index = buf[offset + 1] & 0xFF;

        // 可选：校验块大小声明字段
        int declared = buf[offset + 2] & 0xFF;
        if (declared != TOTAL_LEN)
        {
            Debug.LogWarning($"[grs] offset=0x{offset:X} {type}-{index} 块大小字段={declared} 非预期值{TOTAL_LEN}，仍按{TOTAL_LEN}字节保存");
        }

        byte[] rawBlock = new byte[TOTAL_LEN];
        Array.Copy(buf, offset, rawBlock, 0, TOTAL_LEN);

        string dir = Application.dataPath + "/../ExRes/grs";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string name = GetString(buf, offset + 6);
        string savePath = dir + $"/{type}-{index}-{name}.grs";
        File.WriteAllBytes(savePath, rawBlock);
        Debug.Log($"[grs] 已保存: {type}-{index}.grs  name={name}  offset=0x{offset:X}");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ResExtractor.cs — RES_MLR (resType=12) 链资源提取
    //  type=1  ResMagicChain   魔法链
    //  type=2  ResLevelupChain 升级链
    // ═══════════════════════════════════════════════════════════════════════
    //
    // ── 魔法链块格式（二进制验证，如 怪妖坛子-15.mlr）：
    //   +0x00  Type        1字节  （固定=1）
    //   +0x01  Index       1字节
    //   +0x02  MaxLevel    1字节
    //   +0x03  ChainData   MaxLevel × 2 字节（每级2字节，魔法索引等）
    //   totalLen = 3 + MaxLevel × 2
    //
    // ── 升级链块格式（来自 ResLevelupChain.SetData）：
    //   +0x00  Type        1字节  （固定=2）
    //   +0x01  Index       1字节
    //   +0x02  MaxLevel    1字节
    //   +0x03  (填充)      1字节
    //   +0x04  LevelData   MaxLevel × 20 字节
    //   totalLen = 4 + MaxLevel × 20
    //
    //   每级20字节布局（偏移相对于该级起始）：
    //   +0x00~+0x01  MaxHP         uint16 LE
    //   +0x02~+0x03  HP            uint16 LE
    //   +0x04~+0x05  MaxMP         uint16 LE
    //   +0x06~+0x07  MP            uint16 LE
    //   +0x08~+0x09  Attack        uint16 LE
    //   +0x0A~+0x0B  Defend        uint16 LE
    //   +0x0C~+0x0D  (保留)        uint16 LE
    //   +0x0E~+0x0F  NextLevelExp  uint16 LE
    //   +0x10~+0x11  (保留)        uint16 LE
    //   +0x12        Speed         uint8
    //   +0x13        Lingli        uint8
    //   +0x14        Luck          uint8
    //   +0x15        LearnMagicNum uint8
    //
    private static bool ExtractMlr(int offset, int subType)
    {
        if (offset + 3 > buf.Length)
        {
            Debug.LogWarning($"[mlr] offset=0x{offset:X} 块头超出文件范围，跳过");
            return false;
        }

        int type = buf[offset] & 0xFF;
        int index = buf[offset + 1] & 0xFF;
        int maxLevel = buf[offset + 2] & 0xFF;

        if (maxLevel <= 0)
        {
            Debug.LogWarning($"[mlr] offset=0x{offset:X} {type}-{index} maxLevel={maxLevel} 无效，跳过");
            return false;
        }

        int totalLen;
        string subName;

        if (subType == 1) // ResMagicChain 魔法链：header=3字节，每级2字节
        {
            totalLen = 3 + maxLevel * 2;
            subName = "magic";
        }
        else if (subType == 2) // ResLevelupChain 升级链：header=4字节（含1字节填充），每级20字节
        {
            totalLen = 4 + maxLevel * 20;
            subName = "levelup";
        }
        else
        {
            Debug.LogWarning($"[mlr] offset=0x{offset:X} 未知子类型 subType={subType}，跳过");
            return false;
        }

        if (offset + totalLen > buf.Length)
        {
            Debug.LogWarning($"[mlr] offset=0x{offset:X} {type}-{index} 块长度 {totalLen} 超出文件范围，跳过");
            return false;
        }

        byte[] rawBlock = new byte[totalLen];
        Array.Copy(buf, offset, rawBlock, 0, totalLen);

        string dir = Application.dataPath + "/../ExRes/mlr";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string savePath = dir + $"/{type}-{index}.mlr";
        File.WriteAllBytes(savePath, rawBlock);
        Debug.Log($"[mlr] 已保存: {type}-{index}.mlr  sub={subName}  maxLevel={maxLevel}  size={totalLen}  offset=0x{offset:X}");
        return true;
    }

    // ── 工具方法 ──────────────────────────────────────────

    private static int GetKey(int resType, int type, int index)
    {
        return (resType << 16) | (type << 8) | index;
    }

    static string GetString(byte[] data, int offset)
    {
        int i = 0;
        while (offset + i < data.Length && data[offset + i] != 0)
        {
            byte b = data[offset + i];
            // GB2312 双字节字符：首字节范围 0xA1~0xFE，连同下一字节一起跳过
            if (b >= 0xA1 && b <= 0xFE)
                i += 2;
            else
                i += 1;
        }

        try
        {
            return gb2312Encoding.GetString(data, offset, i).TrimEnd('\0');
        }
        catch
        {
            return "?";
        }
    }
}
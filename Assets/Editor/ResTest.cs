using BBKRPGSimulator;
using I18N.CJK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public class ResTest
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

    [MenuItem("工具/测试解压资源")]
    public static void ExRes()
    {
        gb2312Encoding = new CP936();

        string datPath = Application.streamingAssetsPath + "/Game/伏魔记经典版.lib";
        buf = File.ReadAllBytes(datPath);

        ParseHeader();
        GetAllResOffset();
        ExtractAll();
    }

    // ── 文件头解析 ────────────────────────────────────────
    // 格式：
    //   0x00~0x02  魔数 "LIB"
    //   0x03~0x08  游戏名 GB2312 \0结尾
    //   0x09~0x0B  保留 (全0)
    //   0x0C~0x0D  条目数 uint16 小端
    //   0x0E~0x0F  固定 0xFFFF
    private static void ParseHeader()
    {
        string magic = System.Text.Encoding.ASCII.GetString(buf, 0, 3);
        Name = Utilities.GetGameName(buf);
        int entryCount = (buf[0x0D] << 8) | buf[0x0C];
        Debug.Log($"魔数={magic}  游戏名={Name}  条目数={entryCount}");
    }

    // ── 索引表解析 ────────────────────────────────────────
    // 索引区A: 0x0010 起，每条3字节 (resType, type, index)
    // 索引区B: 0x2000 起，每条3字节 (block, low, high)
    // A[n] 与 B[n] 严格一一对应
    // resType=255 为空条目，B区对应值全FF，跳过
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

            // resType=255 是空条目，跳过
            if (resType == 0xFF)
                continue;

            int block = buf[posB];
            int low = buf[posB + 1];
            int high = buf[posB + 2];
            int offset = block * 0x4000 | (high << 8 | low);

            int key = GetKey(resType, type, index);
            if (!_dataOffset.ContainsKey(key))
                _dataOffset.Add(key, offset);
        }

        Debug.Log($"索引表解析完成，有效条目数: {_dataOffset.Count}");
    }

    // ── 提取所有 RES_GUT 资源 ─────────────────────────────
    private static void ExtractAll()
    {
        string gutDir = Application.dataPath + "/../ExRes/gut";
        if (!Directory.Exists(gutDir))
        {
            Directory.CreateDirectory(gutDir);
        }
        int saved = 0;
        int skipped = 0;

        foreach (var kv in _dataOffset)
        {
            int resType = (kv.Key >> 16) & 0xFF;
            int offset = kv.Value;

            if (resType == 1) // RES_GUT
            {
                bool ok = ExtractGut(offset, gutDir);
                if (ok) saved++;
                else skipped++;
            }
        }

        Debug.Log($"提取完成：保存 {saved} 个 gut 文件，跳过 {skipped} 个");
    }

    // ── gut 块解析与保存 ──────────────────────────────────
    // gut 块格式：
    //   +0x00        Type           1字节  资源类型（=1）
    //   +0x01        Index          1字节  资源索引号
    //   +0x02~+0x17  Description    23字节 GB2312字符串，0xCC填充，\0结尾
    //   +0x18~+0x19  Length         uint16 小端，脚本数据段长度
    //   +0x1A        NumSceneEvent  uint8  场景事件数量
    //   +0x1B~       SceneEvent[]   NumSceneEvent*2 字节，每个uint16小端
    //   +0x1B+N*2    ScriptData     (Length - NumSceneEvent*2 - 3) 字节
    //
    //   totalLen = 0x1B + NumSceneEvent*2 + ScriptData长度
    private static bool ExtractGut(int offset, string gutDir)
    {
        // 最少需要 0x1B 字节读完固定头
        if (offset + 0x1B > buf.Length)
        {
            Debug.LogWarning($"offset=0x{offset:X}  块头超出文件范围，跳过");
            return false;
        }

        int type = buf[offset];
        int index = buf[offset + 1];

        string description = GetString(buf, offset + 2);

        int length = ((buf[offset + 0x19] & 0xFF) << 8)
                           | (buf[offset + 0x18] & 0xFF);
        int numSceneEvent = buf[offset + 0x1A] & 0xFF;

        int scriptLen = length - numSceneEvent * 2 - 3;
        if (type <= 0 || scriptLen <= 0)
        {
            Debug.LogWarning($"offset=0x{offset:X}  {type}-{index}  scriptLen={scriptLen} 无效，跳过");
            return false;
        }

        int totalLen = 0x1B + numSceneEvent * 2 + scriptLen;
        if (offset + totalLen > buf.Length)
        {
            Debug.LogWarning($"offset=0x{offset:X}  {type}-{index}  块长度 {totalLen} 超出文件范围，跳过");
            return false;
        }

        byte[] rawBlock = new byte[totalLen];
        Array.Copy(buf, offset, rawBlock, 0, totalLen);

        string savePath = gutDir + $"/{type}-{index}.gut";
        File.WriteAllBytes(savePath, rawBlock);

        Debug.Log($"已保存: {type}-{index}.gut  desc={description}  totalLen={totalLen}  offset=0x{offset:X}");
        return true;
    }

    // ── 工具方法 ──────────────────────────────────────────

    /// <summary>
    /// 获取资源的 KEY
    /// </summary>
    private static int GetKey(int resType, int type, int index)
    {
        return (resType << 16) | (type << 8) | index;
    }

    static string GetString(byte[] data, int offset)
    {
        int i = 0;
        while (offset + i < data.Length && data[offset + i] != 0 && data[offset + i] != 0xCC)
            i++;
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
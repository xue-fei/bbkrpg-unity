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

        string datPath = Application.streamingAssetsPath + "/Game/伏魔记.lib";
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
                _dataOffset.Add(key, offset);
        }

        Debug.Log($"索引表解析完成，有效条目: {_dataOffset.Count}");
    }

    // ── 提取所有资源 ──────────────────────────────────────
    private static void ExtractAll()
    {
        int gutSaved = 0, gutSkipped = 0;
        int mapSaved = 0, mapSkipped = 0;

        foreach (var kv in _dataOffset)
        {
            int resType = (kv.Key >> 16) & 0xFF;
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
        }

        Debug.Log($"gut: 保存 {gutSaved} 个，跳过 {gutSkipped} 个");
        Debug.Log($"map: 保存 {mapSaved} 个，跳过 {mapSkipped} 个");
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

        if (type <= 0 || scriptLen <= 0)
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
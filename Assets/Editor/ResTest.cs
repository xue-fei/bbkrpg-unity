using BBKRPGSimulator;
using I18N.CJK;
using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
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
    /// 保存资源数据相对文件首字节的偏移量
    /// </summary>
    private static Dictionary<int, int> _dataOffset = new Dictionary<int, int>(2048);

    [MenuItem("工具/测试解压资源")]
    public static void ExRes()
    {
        gb2312Encoding = new CP936();

        string datPath = Application.streamingAssetsPath + "/Game/伏魔记经典版.lib";
        buf = File.ReadAllBytes(datPath);
        GetAllResOffset();
    }

    /// <summary>
    /// 获取所有资源的偏移
    /// </summary>
    private static void GetAllResOffset()
    {
        Name = Utilities.GetGameName(buf);
        Debug.Log(Name);

        int i = 0x10, j = 0x2000;

        while (i < buf.Length - 3 && j < buf.Length - 3)
        {
            int resType = buf[i++];
            int key = GetKey(resType, buf[i++], buf[i++] & 0xFF);

            int block = buf[j++] & 0xFF;
            int low = buf[j++] & 0xFF;
            int high = buf[j++] & 0xFF;
            int offset = block * 0x4000 | (high << 8 | low);

            if (!_dataOffset.ContainsKey(key))
            {
                _dataOffset.Add(key, offset);
                ExFile(resType, offset);
            }
        }
    }


    static void ExFile(int resType, int offset)
    {
        //Debug.Log("offset: " + offset);
        if (resType <= 0 || resType > 12)
        {
            return;
        }
        if (offset >= buf.Length)
        {
            //Debug.LogWarning("offset 超出 buf 范围: " + offset);
            return;
        }
        Debug.LogWarning("resType:" + resType);
        int Type = buf[offset];
        Debug.LogWarning("Type:" + Type);
        if (resType == 1)
        {
            Gut(Type, offset);
        } 
    }

    static void Gut(int Type, int offset)
    {
        int Index = buf[offset + 1];
        //Debug.Log("Type:" + Type + " Index:" + Index);

        if (Index == 255)
        {
            return;
        }
        string Description = GetString(buf, offset + 2);
        Debug.Log("Description: " + Description);

        int Length = (((int)buf[offset + 0x19] & 0xFF) << 8)
                   | ((int)buf[offset + 0x18] & 0xFF);

        int NumSceneEvent = (int)buf[offset + 0x1a] & 0xFF;

        int[] SceneEvent = new int[NumSceneEvent];
        for (int i = 0; i < NumSceneEvent; i++)
        {
            SceneEvent[i] = ((int)buf[offset + (i << 1) + 0x1c] & 0xFF) << 8
                          | ((int)buf[offset + (i << 1) + 0x1b] & 0xFF);
        }

        int len = Length - NumSceneEvent * 2 - 3;
        Debug.Log("ScriptData len: " + len); 
        if (len <= 0)
        {
            Debug.LogWarning($"跳过 {Type}-{Index}：len={len} 无效");
            return;
        }
        byte[] ScriptData = new byte[len];
        Array.Copy(buf, offset + 0x1b + (NumSceneEvent * 2), ScriptData, 0, len);

        // 计算完整资源块长度（头部 + SceneEvent表 + 脚本数据）
        int totalLen = 0x1b + NumSceneEvent * 2 + len;

        // 越界保护
        if (offset + totalLen > buf.Length)
        {
            Debug.LogWarning($"跳过 {Type}-{Index}：块长度超出 buf 范围");
            return;
        }

        // 从原始 buf 复制完整块
        byte[] rawBlock = new byte[totalLen];
        Array.Copy(buf, offset, rawBlock, 0, totalLen);

        string gutDir = Application.streamingAssetsPath + "/gut";
        if (!Directory.Exists(gutDir))
        {
            Directory.CreateDirectory(gutDir);
        }

        string savePath = gutDir + $"/{Type}-{Index}.gut";
        File.WriteAllBytes(savePath, rawBlock);
        //File.WriteAllText(gutDir + $"/{Type}-{Index}.txt",
        //    gb2312Encoding.GetString(ScriptData).TrimEnd('\0')); 
        Debug.Log($"已保存: {savePath}  ({totalLen} bytes)");
    }
     
    /// <summary>
    /// 获取资源的 KEY
    /// </summary>
    /// <param name="resType">资源文件类型号 1-12</param>
    /// <param name="type">资源类型</param>
    /// <param name="index">资源索引号</param>
    private static int GetKey(int resType, int type, int index)
    {
        return (resType << 16) | (type << 8) | index;
    }

    static string GetString(byte[] data, int offset)
    {
        byte[] strbyte = GetStringBytes(data, offset);
        return gb2312Encoding.GetString(strbyte).TrimEnd('\0');
    }

    static byte[] GetStringBytes(byte[] data, int offset)
    {
        int i = 0;
        while (data[offset + i] != 0)
        {
            ++i;
        }

        byte[] result = new byte[++i];
        Array.Copy(data, offset, result, 0, i);
        return result;
    }
}
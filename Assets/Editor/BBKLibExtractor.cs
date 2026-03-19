using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// BBK RPG .gam 文件 → .lib 文件提取器
/// 
/// 经过二进制逆向分析确认的精确格式：
///
/// ┌─────────────────────────────────────────────────────────┐
/// │                   GAM 文件结构                           │
/// ├──────────┬──────┬──────────────────────────────────────┤
/// │  偏移    │ 大小 │ 说明                                  │
/// ├──────────┼──────┼──────────────────────────────────────┤
/// │ 0x000    │  4   │ Magic: "GAM\0"                        │
/// │ 0x004    │  2   │ WORD = 0x0010 (用途待定)              │
/// │ 0x006    │ ~16  │ 游戏标题 (GBK编码, null结尾)          │
/// │ 0x025    │  8   │ "BBK LTD." 公司名                     │
/// │ 0x037    │  6   │ "Ver1.1" 版本号                       │
/// │ 0x040    │ ...  │ 游戏 ROM 代码 (6502 CPU 指令)         │
/// │ 0x48000  │ EOF  │ LIB 资源数据                          │
/// └──────────┴──────┴──────────────────────────────────────┘
///
/// LIB 文件 = gam[0x48000 .. EOF] + 0x00（追加1个null字节）
///
/// ┌─────────────────────────────────────────────────────────┐
/// │                   LIB 文件结构                           │
/// ├──────────┬──────┬──────────────────────────────────────┤
/// │ 0x000    │  3   │ Magic: "LIB"                          │
/// │ 0x003    │ ~10  │ 游戏标题 (GBK编码, null结尾)          │
/// │ 0x00C+   │ ...  │ 游戏资源数据                          │
/// └──────────┴──────┴──────────────────────────────────────┘
/// </summary>
public static class BBKLibExtractor
{
    // ── 格式常量 ────────────────────────────────────────────────────────────

    /// <summary>GAM 文件魔数</summary>
    public static readonly byte[] GAM_MAGIC = { 0x47, 0x41, 0x4D, 0x00 }; // "GAM\0"

    /// <summary>LIB 数据在 GAM 文件中的固定起始偏移 (0x48000 = 294912 bytes)</summary>
    public const int LIB_DATA_OFFSET = 0x48000;

    /// <summary>GAM 文件最小合法大小（必须大于 LIB_DATA_OFFSET）</summary>
    public const int GAM_MIN_SIZE = LIB_DATA_OFFSET + 1;

    /// <summary>GAM 头部游戏标题的偏移</summary>
    public const int GAM_TITLE_OFFSET = 0x06;

    // ── 公开 API ────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 .gam 文件提取 .lib 文件
    /// </summary>
    /// <param name="gamPath">输入 .gam 文件路径</param>
    /// <param name="libPath">输出 .lib 文件路径（null则自动生成同名.lib）</param>
    /// <returns>提取结果信息</returns>
    public static ExtractResult Extract(string gamPath, string libPath = null)
    {
        if (string.IsNullOrEmpty(gamPath))
            return ExtractResult.Fail("gamPath 不能为空");

        if (!File.Exists(gamPath))
            return ExtractResult.Fail($"文件不存在: {gamPath}");

        if (string.IsNullOrEmpty(libPath))
            libPath = Path.ChangeExtension(gamPath, ".lib");

        try
        {
            byte[] gamData = File.ReadAllBytes(gamPath);

            // 验证魔数
            if (!StartsWith(gamData, GAM_MAGIC))
                return ExtractResult.Fail($"不是有效的 BBK GAM 文件（魔数不匹配，期望 GAM\\0，实际 {gamData[0]:X2}{gamData[1]:X2}{gamData[2]:X2}{gamData[3]:X2}）");

            // 验证文件大小
            if (gamData.Length < GAM_MIN_SIZE)
                return ExtractResult.Fail($"文件过小: {gamData.Length} bytes（最小需要 {GAM_MIN_SIZE} bytes）");

            // 读取游戏标题
            string title = ReadGBKString(gamData, GAM_TITLE_OFFSET);

            // 提取 LIB 数据：gam[0x48000..EOF] + 0x00
            int libDataLength = gamData.Length - LIB_DATA_OFFSET;
            byte[] libData = new byte[libDataLength + 1]; // +1 for trailing null byte
            Buffer.BlockCopy(gamData, LIB_DATA_OFFSET, libData, 0, libDataLength);
            libData[libDataLength] = 0x00; // 追加 null 字节

            // 写出 .lib 文件
            string outputDir = Path.GetDirectoryName(libPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            File.WriteAllBytes(libPath, libData);

            return new ExtractResult
            {
                Success = true,
                GameTitle = title,
                GamSize = gamData.Length,
                LibOffset = LIB_DATA_OFFSET,
                LibSize = libData.Length,
                OutputPath = libPath,
                Message = $"提取成功：{title}（{libData.Length:N0} bytes）"
            };
        }
        catch (Exception e)
        {
            return ExtractResult.Fail($"提取失败: {e.Message}");
        }
    }

    /// <summary>
    /// 检查文件是否为有效的 BBK GAM 文件（不实际提取）
    /// </summary>
    public static bool IsValidGamFile(string path, out string reason)
    {
        reason = "";
        if (!File.Exists(path)) { reason = "文件不存在"; return false; }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            if (fs.Length < GAM_MIN_SIZE)
            {
                reason = $"文件大小 {fs.Length} bytes 不足，最小需要 {GAM_MIN_SIZE} bytes";
                return false;
            }

            byte[] header = new byte[4];
            fs.Read(header, 0, 4);
            if (!StartsWith(header, GAM_MAGIC))
            {
                reason = "文件头魔数不是 \"GAM\\0\"";
                return false;
            }

            reason = "有效";
            return true;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
    }

    /// <summary>
    /// 读取 GAM 文件中的游戏标题（GBK编码）
    /// </summary>
    public static string ReadGameTitle(string gamPath)
    {
        if (!File.Exists(gamPath)) return "";
        try
        {
            byte[] data = File.ReadAllBytes(gamPath);
            return ReadGBKString(data, GAM_TITLE_OFFSET);
        }
        catch { return ""; }
    }

    // ── 内部工具方法 ─────────────────────────────────────────────────────────

    private static string ReadGBKString(byte[] data, int offset)
    {
        // 找 null 结尾
        int end = offset;
        while (end < data.Length && data[end] != 0x00)
            end++;

        if (end == offset) return "";

        try
        {
            // Unity 中使用 GBK/GB2312 编码（代码页 936）
            Encoding gbk = Encoding.GetEncoding(936);
            return gbk.GetString(data, offset, end - offset);
        }
        catch
        {
            // 回退：按 UTF-8 尝试
            return Encoding.UTF8.GetString(data, offset, end - offset);
        }
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
            if (data[i] != prefix[i]) return false;
        return true;
    }
}

// ── 结果结构体 ───────────────────────────────────────────────────────────────

/// <summary>提取操作的结果信息</summary>
public class ExtractResult
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public string GameTitle { get; set; }
    public long GamSize { get; set; }
    public int LibOffset { get; set; }
    public int LibSize { get; set; }
    public string OutputPath { get; set; }

    public static ExtractResult Fail(string msg) =>
        new ExtractResult { Success = false, Message = msg };

    public override string ToString() => Message;
}
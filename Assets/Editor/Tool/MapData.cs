using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace BBKMapEditor
{
    /// <summary>
    /// BBK RPG .map 文件数据模型（对齐 MapEditor.cs 格式）
    ///
    /// ── .map 文件格式 ────────────────────────────────────────────
    ///   +0x00  Type      1字节
    ///   +0x01  Index     1字节
    ///   +0x02  TilIndex  1字节  对应 til 文件组起始索引
    ///   +0x03~+0x0F  MapName  13字节，GB2312，\0结尾，余0xCC填充
    ///   +0x10  MapWidth  1字节
    ///   +0x11  MapHeight 1字节
    ///   +0x12  _data     MapWidth × MapHeight × 2字节
    ///            低字节：bit7=可行走，bit6~0=tile索引(0~126)
    ///            高字节：事件号(0=无事件)
    /// </summary>
    [Serializable]
    public class MapData
    {
        // ── 格式常量 ──────────────────────────────────────────────────────────
        public const int HEADER_SIZE = 0x12;
        public const int NAME_OFFSET = 0x03;
        public const int NAME_FIELD_LEN = 13;     // +0x03~+0x0F
        public const int WIDTH_OFFSET = 0x10;
        public const int HEIGHT_OFFSET = 0x11;
        public const int DATA_OFFSET = 0x12;
        public const byte NAME_FILL = 0xCC;

        // ── 元数据 ────────────────────────────────────────────────────────────
        public int ResType = 0;
        public int ResIndex = 0;
        public int TilIndex = 1;   // 对应 1-N.til 的 N
        public string MapName = "新地图";
        public int Width = 20;
        public int Height = 15;

        /// <summary>
        /// 原始 tile 数据，每格 2 字节：
        ///   [i*2+0] 低字节：bit7=可行走，bit6~0=tile索引
        ///   [i*2+1] 高字节：事件号
        /// </summary>
        public byte[] RawData;

        // ── 构造 ──────────────────────────────────────────────────────────────
        public MapData() { }

        public MapData(int width, int height, string name = "新地图",
                       int resType = 0, int resIndex = 0, int tilIndex = 1)
        {
            Width = width;
            Height = height;
            MapName = name;
            ResType = resType;
            ResIndex = resIndex;
            TilIndex = tilIndex;
            RawData = new byte[width * height * 2];
            // 默认：可行走(bit7=1) + tile索引0
            for (int i = 0; i < width * height; i++)
                RawData[i * 2] = 0x80; // walkable, tile=0
        }

        // ── Tile 访问（低字节） ───────────────────────────────────────────────

        /// <summary>获取 tile 索引（低字节 bit6~0）</summary>
        public int GetTileIndex(int x, int y)
        {
            if (!InBounds(x, y)) return 0;
            return RawData[(y * Width + x) * 2] & 0x7F;
        }

        /// <summary>设置 tile 索引，保留可行走位</summary>
        public void SetTileIndex(int x, int y, int tileIdx)
        {
            if (!InBounds(x, y)) return;
            int i = (y * Width + x) * 2;
            RawData[i] = (byte)((RawData[i] & 0x80) | (tileIdx & 0x7F));
        }

        /// <summary>获取可行走标志（低字节 bit7）</summary>
        public bool GetWalkable(int x, int y)
        {
            if (!InBounds(x, y)) return false;
            return (RawData[(y * Width + x) * 2] & 0x80) != 0;
        }

        /// <summary>设置可行走标志，保留 tile 索引</summary>
        public void SetWalkable(int x, int y, bool walkable)
        {
            if (!InBounds(x, y)) return;
            int i = (y * Width + x) * 2;
            RawData[i] = (byte)(walkable ? (RawData[i] | 0x80) : (RawData[i] & 0x7F));
        }

        // ── 事件号访问（高字节） ─────────────────────────────────────────────

        /// <summary>获取事件号（高字节，0=无事件）</summary>
        public int GetEventId(int x, int y)
        {
            if (!InBounds(x, y)) return 0;
            return RawData[(y * Width + x) * 2 + 1] & 0xFF;
        }

        /// <summary>设置事件号</summary>
        public void SetEventId(int x, int y, int eventId)
        {
            if (!InBounds(x, y)) return;
            RawData[(y * Width + x) * 2 + 1] = (byte)(eventId & 0xFF);
        }

        // ── 兼容旧接口（MapEditorWindow 调用）──────────────────────────────
        /// <summary>旧接口兼容：等价于 GetTileIndex</summary>
        public int GetBaseTile(int x, int y) => GetTileIndex(x, y);
        /// <summary>旧接口兼容：等价于 SetTileIndex</summary>
        public void SetBaseTile(int x, int y, int idx) => SetTileIndex(x, y, idx);

        // ── 边界 ──────────────────────────────────────────────────────────────
        public bool InBounds(int x, int y) =>
            x >= 0 && x < Width && y >= 0 && y < Height;

        // ── 文件 IO ───────────────────────────────────────────────────────────
        public static MapData Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"找不到文件: {path}");
            return Deserialize(File.ReadAllBytes(path), path);
        }

        public void Save(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, Serialize());
        }

        // ── 序列化 ────────────────────────────────────────────────────────────
        public byte[] Serialize()
        {
            int dataLen = Width * Height * 2;
            byte[] buf = new byte[HEADER_SIZE + dataLen];

            buf[0] = (byte)ResType;
            buf[1] = (byte)ResIndex;
            buf[2] = (byte)TilIndex;

            // MapName：GB2312，\0结尾，余 0xCC 填充，共 13 字节
            byte[] nameBytes;
            try { nameBytes = Encoding.GetEncoding("gb2312").GetBytes(MapName); }
            catch { nameBytes = Encoding.UTF8.GetBytes(MapName); }

            int copyLen = Math.Min(nameBytes.Length, NAME_FIELD_LEN - 1);
            Array.Copy(nameBytes, 0, buf, NAME_OFFSET, copyLen);
            buf[NAME_OFFSET + copyLen] = 0x00; // null 终止符
            for (int i = NAME_OFFSET + copyLen + 1; i < NAME_OFFSET + NAME_FIELD_LEN; i++)
                buf[i] = NAME_FILL; // 0xCC 填充

            buf[WIDTH_OFFSET] = (byte)Width;
            buf[HEIGHT_OFFSET] = (byte)Height;

            Array.Copy(RawData, 0, buf, DATA_OFFSET, dataLen);
            return buf;
        }

        private static MapData Deserialize(byte[] buf, string path = "")
        {
            if (buf.Length < HEADER_SIZE)
                throw new InvalidDataException($"文件过小（{buf.Length} 字节）");

            var map = new MapData
            {
                ResType = buf[0],
                ResIndex = buf[1],
                TilIndex = buf[2],
                Width = buf[WIDTH_OFFSET],
                Height = buf[HEIGHT_OFFSET]
            };

            if (map.Width == 0 || map.Height == 0)
                throw new InvalidDataException($"地图尺寸无效（W={map.Width} H={map.Height}）");

            // 读取 MapName（GB2312，\0结尾）
            int nameEnd = NAME_OFFSET;
            while (nameEnd < NAME_OFFSET + NAME_FIELD_LEN && buf[nameEnd] != 0) nameEnd++;
            try
            {
                map.MapName = Encoding.GetEncoding("gb2312")
                    .GetString(buf, NAME_OFFSET, nameEnd - NAME_OFFSET);
            }
            catch
            {
                map.MapName = Path.GetFileNameWithoutExtension(path);
            }
            if (string.IsNullOrEmpty(map.MapName))
                map.MapName = Path.GetFileNameWithoutExtension(path);

            int dataLen = map.Width * map.Height * 2;
            if (buf.Length < HEADER_SIZE + dataLen)
                throw new InvalidDataException(
                    $"数据长度不足（需{HEADER_SIZE + dataLen}，实{buf.Length}）");

            map.RawData = new byte[dataLen];
            Array.Copy(buf, DATA_OFFSET, map.RawData, 0, dataLen);
            return map;
        }

        // ── 工具方法 ──────────────────────────────────────────────────────────
        public MapData Clone()
        {
            var copy = new MapData(Width, Height, MapName, ResType, ResIndex, TilIndex);
            Array.Copy(RawData, copy.RawData, RawData.Length);
            return copy;
        }

        public void Resize(int newW, int newH)
        {
            var newData = new byte[newW * newH * 2];
            // 默认：可行走
            for (int i = 0; i < newW * newH; i++) newData[i * 2] = 0x80;

            int copyW = Math.Min(Width, newW);
            int copyH = Math.Min(Height, newH);
            for (int y = 0; y < copyH; y++)
                for (int x = 0; x < copyW; x++)
                {
                    newData[(y * newW + x) * 2] = RawData[(y * Width + x) * 2];
                    newData[(y * newW + x) * 2 + 1] = RawData[(y * Width + x) * 2 + 1];
                }

            RawData = newData;
            Width = newW;
            Height = newH;
        }

        public void Fill(int tileIdx, bool walkable = true)
        {
            byte lo = (byte)((walkable ? 0x80 : 0x00) | (tileIdx & 0x7F));
            for (int i = 0; i < Width * Height; i++)
            {
                RawData[i * 2] = lo;
                RawData[i * 2 + 1] = 0;
            }
        }

        public void FillRect(int x, int y, int w, int h, int tileIdx, bool walkable = true)
        {
            byte lo = (byte)((walkable ? 0x80 : 0x00) | (tileIdx & 0x7F));
            for (int row = y; row < y + h; row++)
                for (int col = x; col < x + w; col++)
                {
                    if (!InBounds(col, row)) continue;
                    RawData[(row * Width + col) * 2] = lo;
                    RawData[(row * Width + col) * 2 + 1] = 0;
                }
        }

        // ── Undo 快照兼容（保存/恢复整个 RawData）───────────────────────────
        public byte[] SnapshotRaw()
        {
            var snap = new byte[RawData.Length];
            Array.Copy(RawData, snap, RawData.Length);
            return snap;
        }

        public void RestoreRaw(byte[] snap)
        {
            if (snap.Length == RawData.Length)
                Array.Copy(snap, RawData, RawData.Length);
        }
    }
}
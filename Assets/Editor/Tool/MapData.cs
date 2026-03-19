using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace BBKMapEditor
{
    /// <summary>
    /// BBK RPG .map 文件数据模型
    ///
    /// 文件格式（逆向分析确认）：
    /// ┌──────────┬──────┬───────────────────────────────────────┐
    /// │ 偏移     │ 大小 │ 说明                                  │
    /// ├──────────┼──────┼───────────────────────────────────────┤
    /// │ 0x00     │  3   │ 版本标志 [1, 1, 1]                    │
    /// │ 0x03     │  ?   │ 地图名称（GBK编码，null结尾）          │
    /// │ ?        │  ?   │ 填充至 0x0E                           │
    /// │ 0x0E     │  2   │ Magic "MP"                            │
    /// │ 0x10     │  1   │ 地图宽度（Tiles）                     │
    /// │ 0x11     │  1   │ 地图高度（Tiles）                     │
    /// │ 0x12     │ W*H*2│ Tile数据（每格一个 WORD，小端序）      │
    /// └──────────┴──────┴───────────────────────────────────────┘
    ///
    /// Tile WORD 格式：
    ///   高字节 (bits 15-8)：事件/对象 ID（0 = 无事件）
    ///   低字节 (bits 7-0) ：基础 Tile 索引（对应 Tiles.bmp 中的图块）
    /// </summary>
    [Serializable]
    public class MapData
    {
        // ── 格式常量 ─────────────────────────────────────────────────────────
        public const int HEADER_SIZE = 0x12;       // 固定头部大小
        public const int TITLE_OFFSET = 0x03;       // 标题起始偏移
        public const int TITLE_MAX_LEN = 10;         // 标题最大字节数（含null）
        public const int MAGIC_OFFSET = 0x0E;       // "MP" 偏移
        public const int WIDTH_OFFSET = 0x10;
        public const int HEIGHT_OFFSET = 0x11;
        public const int TILE_DATA_OFFSET = 0x12;
        public static readonly byte[] VERSION_FLAGS = { 1, 1, 1 };
        public static readonly byte[] MAP_MAGIC = { 0x4D, 0x50 }; // "MP"

        // ── 地图属性 ──────────────────────────────────────────────────────────
        public string MapName = "新地图";
        public int Width = 20;
        public int Height = 15;

        /// <summary>Tile 数据：ushort[y * Width + x]</summary>
        public ushort[] Tiles;

        // ── 构造 ──────────────────────────────────────────────────────────────
        public MapData() { }

        public MapData(int width, int height, string name = "新地图")
        {
            Width = width;
            Height = height;
            MapName = name;
            Tiles = new ushort[width * height];
            // 默认填充 tile 129（地板）
            for (int i = 0; i < Tiles.Length; i++)
                Tiles[i] = 129;
        }

        // ── Tile 访问 ─────────────────────────────────────────────────────────
        public ushort GetTile(int x, int y)
        {
            if (!InBounds(x, y)) return 0;
            return Tiles[y * Width + x];
        }

        public void SetTile(int x, int y, ushort value)
        {
            if (!InBounds(x, y)) return;
            Tiles[y * Width + x] = value;
        }

        /// <summary>获取基础 tile 索引（低字节）</summary>
        public int GetBaseTile(int x, int y) => GetTile(x, y) & 0xFF;

        /// <summary>获取事件 ID（高字节）</summary>
        public int GetEventId(int x, int y) => (GetTile(x, y) >> 8) & 0xFF;

        /// <summary>设置基础 tile，保留事件 ID</summary>
        public void SetBaseTile(int x, int y, int tileIndex)
        {
            int eventId = GetEventId(x, y);
            SetTile(x, y, (ushort)((eventId << 8) | (tileIndex & 0xFF)));
        }

        /// <summary>设置事件 ID，保留基础 tile</summary>
        public void SetEventId(int x, int y, int eventId)
        {
            int baseTile = GetBaseTile(x, y);
            SetTile(x, y, (ushort)((eventId << 8) | baseTile));
        }

        public bool InBounds(int x, int y) =>
            x >= 0 && x < Width && y >= 0 && y < Height;

        // ── 文件 IO ───────────────────────────────────────────────────────────

        /// <summary>从 .map 文件加载</summary>
        public static MapData Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"找不到文件: {path}");

            byte[] raw = File.ReadAllBytes(path);
            return Deserialize(raw, path);
        }

        /// <summary>保存为 .map 文件</summary>
        public void Save(string path)
        {
            byte[] raw = Serialize();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, raw);
        }

        // ── 序列化 ────────────────────────────────────────────────────────────

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // [0x00] 版本标志
            bw.Write(VERSION_FLAGS);

            // [0x03] 地图名称（GBK, null结尾, 固定到0x0D共10字节）
            byte[] titleBytes = Encoding.GetEncoding(936).GetBytes(MapName);
            int titleLen = Math.Min(titleBytes.Length, TITLE_MAX_LEN - 1);
            bw.Write(titleBytes, 0, titleLen);
            // 填充至 0x0E（偏移 0x03 + 10 字节 + 1 null = 0x0E）
            int padLen = (MAGIC_OFFSET - TITLE_OFFSET) - titleLen;
            for (int i = 0; i < padLen; i++) bw.Write((byte)0);

            // [0x0E] Magic "MP"
            bw.Write(MAP_MAGIC);

            // [0x10] 宽高
            bw.Write((byte)Width);
            bw.Write((byte)Height);

            // [0x12] Tile 数据
            for (int i = 0; i < Tiles.Length; i++)
                bw.Write(Tiles[i]); // WORD LE

            return ms.ToArray();
        }

        private static MapData Deserialize(byte[] raw, string path = "")
        {
            if (raw.Length < HEADER_SIZE)
                throw new InvalidDataException($"文件太小 ({raw.Length} bytes)");

            // 验证 Magic
            if (raw[MAGIC_OFFSET] != 'M' || raw[MAGIC_OFFSET + 1] != 'P')
                throw new InvalidDataException($"无效的 Magic（期望 \"MP\"，实际 0x{raw[MAGIC_OFFSET]:X2}{raw[MAGIC_OFFSET + 1]:X2}）");

            var map = new MapData();

            // 读取标题
            int titleEnd = TITLE_OFFSET;
            while (titleEnd < MAGIC_OFFSET && raw[titleEnd] != 0) titleEnd++;
            map.MapName = Encoding.GetEncoding(936)
                .GetString(raw, TITLE_OFFSET, titleEnd - TITLE_OFFSET);
            if (string.IsNullOrEmpty(map.MapName))
                map.MapName = Path.GetFileNameWithoutExtension(path);

            // 读取尺寸
            map.Width = raw[WIDTH_OFFSET];
            map.Height = raw[HEIGHT_OFFSET];

            if (map.Width == 0 || map.Height == 0)
                throw new InvalidDataException("地图宽度或高度为0");

            int expectedSize = HEADER_SIZE + map.Width * map.Height * 2;
            if (raw.Length < expectedSize)
                throw new InvalidDataException(
                    $"文件大小不足: 期望 {expectedSize}，实际 {raw.Length}");

            // 读取 Tile 数据
            map.Tiles = new ushort[map.Width * map.Height];
            int pos = TILE_DATA_OFFSET;
            for (int i = 0; i < map.Tiles.Length; i++)
            {
                map.Tiles[i] = (ushort)(raw[pos] | (raw[pos + 1] << 8));
                pos += 2;
            }

            return map;
        }

        // ── 工具方法 ──────────────────────────────────────────────────────────

        public MapData Clone()
        {
            var copy = new MapData(Width, Height, MapName);
            Array.Copy(Tiles, copy.Tiles, Tiles.Length);
            return copy;
        }

        public void Resize(int newW, int newH, ushort fillTile = 129)
        {
            var newTiles = new ushort[newW * newH];
            for (int i = 0; i < newTiles.Length; i++) newTiles[i] = fillTile;

            int copyW = Math.Min(Width, newW);
            int copyH = Math.Min(Height, newH);
            for (int y = 0; y < copyH; y++)
                for (int x = 0; x < copyW; x++)
                    newTiles[y * newW + x] = GetTile(x, y);

            Tiles = newTiles;
            Width = newW;
            Height = newH;
        }

        public void Fill(ushort tileValue)
        {
            for (int i = 0; i < Tiles.Length; i++)
                Tiles[i] = tileValue;
        }

        public void FillRect(int x, int y, int w, int h, ushort tileValue)
        {
            for (int row = y; row < y + h; row++)
                for (int col = x; col < x + w; col++)
                    SetTile(col, row, tileValue);
        }
    }
}
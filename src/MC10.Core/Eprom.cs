using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MC10.Core
{
    public enum EpromMode
    {
        /// <summary>Mask ROM / EPROM: writes from the CPU are ignored (and counted).</summary>
        ReadOnly,
        /// <summary>
        /// EEPROM-style flash: a byte can only be programmed by clearing bits (new = old AND value).
        /// Restoring bits requires <see cref="Eprom.EraseAll"/> or <see cref="Eprom.EraseByte"/>.
        /// </summary>
        ByteProgrammable,
    }

    /// <summary>
    /// A ROM chip mapped into the address space. Holds the image, the write policy and
    /// simple diagnostics (checksum, CRC-32, write-attempt counter).
    /// </summary>
    public sealed class Eprom
    {
        public int Base { get; }
        public int Size { get; }
        public byte[] Data { get; }
        public EpromMode Mode { get; set; }
        public long WriteAttempts { get; private set; }
        public long ProgramOps { get; private set; }
        public long IgnoredWrites { get; private set; }

        public Eprom(int baseAddr, byte[] image, EpromMode mode = EpromMode.ReadOnly)
        {
            if (baseAddr < 0 || baseAddr + image.Length > 0x10000)
                throw new ArgumentException("ROM does not fit in the 64 KB address space");
            Base = baseAddr;
            Size = image.Length;
            Data = (byte[])image.Clone();
            Mode = mode;
        }

        /// <summary>Handle a CPU write at <paramref name="addr"/>. Returns the byte now stored there.</summary>
        public byte Program(int addr, byte value)
        {
            WriteAttempts++;
            int i = addr - Base;
            if (Mode == EpromMode.ReadOnly)
            {
                IgnoredWrites++;
                return Data[i];
            }
            ProgramOps++;
            Data[i] &= value;                       // flash can only clear bits
            return Data[i];
        }

        public void EraseByte(int addr) => Data[addr - Base] = 0xFF;

        public void EraseAll() => Array.Fill(Data, (byte)0xFF);

        /// <summary>Sum of all bytes modulo 65536.</summary>
        public int Checksum16()
        {
            int sum = 0;
            foreach (byte b in Data) sum = (sum + b) & 0xFFFF;
            return sum;
        }

        public uint Crc32()
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in Data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }
    }

    /// <summary>Loads ROM / program images: raw binary, Intel HEX and Motorola S-record.</summary>
    public static class ImageLoader
    {
        /// <summary>A contiguous block of bytes at an absolute address.</summary>
        public readonly struct Segment
        {
            public readonly int Address;
            public readonly byte[] Data;
            public Segment(int address, byte[] data) { Address = address; Data = data; }
        }

        public static List<Segment> Load(string path, int rawBase = 0)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            byte[] bytes = File.ReadAllBytes(path);
            if (ext == ".hex" || ext == ".ihx") return ParseIntelHex(Encoding.ASCII.GetString(bytes));
            if (ext == ".s19" || ext == ".srec" || ext == ".s28" || ext == ".s37") return ParseSRecord(Encoding.ASCII.GetString(bytes));
            return new List<Segment> { new Segment(rawBase, bytes) };
        }

        public static List<Segment> ParseIntelHex(string text)
        {
            var bytes = new SortedDictionary<int, byte>();
            int offset = 0;                                       // extended segment/linear base
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] != ':') throw new FormatException("Intel HEX record must start with ':'");
                byte[] rec = FromHex(line.Substring(1));
                int count = rec[0];
                int addr = (rec[1] << 8) | rec[2];
                int type = rec[3];
                switch (type)
                {
                    case 0x00:                                    // data
                        for (int i = 0; i < count; i++) bytes[offset + addr + i] = rec[4 + i];
                        break;
                    case 0x01:                                    // end of file
                        return Coalesce(bytes);
                    case 0x02:                                    // extended segment address
                        offset = ((rec[4] << 8) | rec[5]) << 4;
                        break;
                    case 0x04:                                    // extended linear address
                        offset = ((rec[4] << 8) | rec[5]) << 16;
                        break;
                }
            }
            return Coalesce(bytes);
        }

        public static List<Segment> ParseSRecord(string text)
        {
            var bytes = new SortedDictionary<int, byte>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length < 4 || line[0] != 'S') continue;
                int addrLen = line[1] switch { '1' => 2, '2' => 3, '3' => 4, _ => 0 };
                if (addrLen == 0) continue;                       // S0/S5/S7-S9 ignored
                byte[] rec = FromHex(line.Substring(2));
                int count = rec[0];
                int addr = 0;
                for (int i = 0; i < addrLen; i++) addr = (addr << 8) | rec[1 + i];
                int dataLen = count - addrLen - 1;               // last byte is checksum
                for (int i = 0; i < dataLen; i++) bytes[addr + i] = rec[1 + addrLen + i];
            }
            return Coalesce(bytes);
        }

        static List<Segment> Coalesce(SortedDictionary<int, byte> bytes)
        {
            var result = new List<Segment>();
            int start = -1, next = -1;
            var cur = new List<byte>();
            foreach (var kv in bytes)
            {
                if (start >= 0 && kv.Key == next)
                {
                    cur.Add(kv.Value);
                    next++;
                    continue;
                }
                if (start >= 0) result.Add(new Segment(start, cur.ToArray()));
                start = kv.Key;
                next = kv.Key + 1;
                cur = new List<byte> { kv.Value };
            }
            if (start >= 0) result.Add(new Segment(start, cur.ToArray()));
            return result;
        }

        static byte[] FromHex(string hex)
        {
            if (hex.Length % 2 != 0) throw new FormatException("odd-length hex record");
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }

        /// <summary>
        /// Place loaded segments into a flat image covering [baseAddr, baseAddr+size).
        /// Bytes not covered by any segment are filled with <paramref name="fill"/>.
        /// </summary>
        public static byte[] Flatten(List<Segment> segs, int baseAddr, int size, byte fill = 0xFF)
        {
            var img = new byte[size];
            Array.Fill(img, fill);
            foreach (var s in segs)
            {
                for (int i = 0; i < s.Data.Length; i++)
                {
                    int a = s.Address + i - baseAddr;
                    if (a >= 0 && a < size) img[a] = s.Data[i];
                }
            }
            return img;
        }
    }
}

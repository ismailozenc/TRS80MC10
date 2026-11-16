using System;

namespace MC10.Core
{
    /// <summary>A memory-mapped peripheral. Addresses are absolute.</summary>
    public interface IDevice
    {
        byte Read(ushort addr);
        void Write(ushort addr, byte value);
    }

    /// <summary>Handles the CPU's internal registers at $0000-$001F.</summary>
    public interface IIoHandler
    {
        byte IoRead(ushort addr);
        void IoWrite(ushort addr, byte value);
    }

    /// <summary>
    /// 64 KB address space. Every address is one of: RAM, ROM (EPROM), a device,
    /// the CPU's internal I/O block, or unmapped (reads return the unmapped value).
    /// </summary>
    public sealed class Bus
    {
        const byte KRam = 0, KRom = 1, KDevice = 2, KIo = 3, KUnmapped = 4;

        public readonly byte[] Mem = new byte[0x10000];
        readonly byte[] kind = new byte[0x10000];
        readonly IDevice[] devices = new IDevice[0x10000];
        Eprom rom;
        public IIoHandler Io;
        public byte UnmappedValue = 0xFF;

        /// <summary>Optional watch hook: (address, isWrite, value). Called on every access to a watched address.</summary>
        public Action<ushort, bool, byte> WatchHook;
        public bool[] Watched = new bool[0x10000];

        public Bus()
        {
            Array.Fill(kind, KUnmapped);
        }

        public void AttachIo(IIoHandler io)
        {
            Io = io;
            for (int a = 0; a < 0x20; a++) kind[a] = KIo;
        }

        public void MapRam(int baseAddr, int size)
        {
            for (int a = baseAddr; a < baseAddr + size; a++)
                if (kind[a] != KIo) kind[a] = KRam;
        }

        public void MapRom(Eprom chip)
        {
            rom = chip;
            for (int a = chip.Base; a < chip.Base + chip.Size; a++)
                kind[a] = KRom;
            Array.Copy(chip.Data, 0, Mem, chip.Base, chip.Size);
        }

        public void MapDevice(ushort addr, IDevice dev)
        {
            kind[addr] = KDevice;
            devices[addr] = dev;
        }

        public Eprom Rom => rom;

        public byte Read(ushort addr)
        {
            byte v;
            switch (kind[addr])
            {
                case KRam:
                case KRom: v = Mem[addr]; break;
                case KIo: v = Io.IoRead(addr); break;
                case KDevice: v = devices[addr].Read(addr); break;
                default: v = UnmappedValue; break;
            }
            if (Watched[addr]) WatchHook?.Invoke(addr, false, v);
            return v;
        }

        public void Write(ushort addr, byte value)
        {
            switch (kind[addr])
            {
                case KRam: Mem[addr] = value; break;
                case KRom:                                      // EPROM policy decides the result
                    if (rom != null) Mem[addr] = rom.Program(addr, value);
                    break;
                case KIo: Io.IoWrite(addr, value); break;
                case KDevice: devices[addr].Write(addr, value); break;
                default: break;                                 // unmapped: ignored
            }
            if (Watched[addr]) WatchHook?.Invoke(addr, true, value);
        }

        /// <summary>Write bypassing device and ROM policy (debugger memory editing).</summary>
        public void Poke(ushort addr, byte value)
        {
            if (kind[addr] == KRam || kind[addr] == KRom) Mem[addr] = value;
            else Write(addr, value);
        }

        public bool IsRam(ushort addr) => kind[addr] == KRam;
        public bool IsRom(ushort addr) => kind[addr] == KRom;
        public bool IsMapped(ushort addr) => kind[addr] != KUnmapped;
    }
}

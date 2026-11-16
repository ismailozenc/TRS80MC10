using System;
using System.Collections.Generic;

namespace MC10.Core
{
    /// <summary>
    /// Keyboard model: a FIFO of ASCII codes. Reading the data address pops one code;
    /// reading the status address returns bit 0 = data ready. This is an abstraction
    /// (the real MC-10 keyboard is scanned through a matrix); see docs/ASSUMPTIONS.md.
    /// </summary>
    public sealed class KeyboardDevice : IDevice
    {
        readonly Queue<byte> fifo = new Queue<byte>();
        readonly ushort dataAddr, statusAddr;

        public KeyboardDevice(ushort dataAddr, ushort statusAddr)
        {
            this.dataAddr = dataAddr;
            this.statusAddr = statusAddr;
        }

        public int Pending => fifo.Count;

        public void Press(byte code)
        {
            if (fifo.Count < 64) fifo.Enqueue(code);   // drop keystrokes if the buffer is full
        }

        public void Clear() => fifo.Clear();

        public byte Read(ushort addr)
        {
            if (addr == statusAddr) return (byte)(fifo.Count > 0 ? 0x01 : 0x00);
            if (addr == dataAddr) return fifo.Count > 0 ? fifo.Dequeue() : (byte)0;
            return 0xFF;
        }

        public void Write(ushort addr, byte value) { }
    }

    /// <summary>
    /// Sound register model. Each write stores the pitch byte and records an event, so
    /// tools can see what the firmware played. Pitch range 1-255 per the MC-10 manual's SOUND statement.
    /// </summary>
    public sealed class SoundDevice : IDevice
    {
        public readonly struct Event
        {
            public readonly long Cycle;
            public readonly byte Pitch;
            public Event(long cycle, byte pitch) { Cycle = cycle; Pitch = pitch; }
        }

        readonly Func<long> cycleSource;
        public byte LastPitch { get; private set; }
        public readonly List<Event> Events = new List<Event>();
        public int MaxEvents { get; set; } = 10000;

        public SoundDevice(Func<long> cycleSource) { this.cycleSource = cycleSource; }

        public byte Read(ushort addr) => LastPitch;

        public void Write(ushort addr, byte value)
        {
            LastPitch = value;
            if (Events.Count >= MaxEvents) Events.RemoveAt(0);
            Events.Add(new Event(cycleSource(), value));
        }
    }

    /// <summary>
    /// Video control register: bits 0-3 = background colour index, bits 4-7 = text colour index.
    /// Colour indices 0-8 follow the MC-10 manual's CLS colour codes:
    /// 0 black, 1 green, 2 yellow, 3 blue, 4 red, 5 buff, 6 cyan, 7 magenta, 8 orange.
    /// The register address is a placeholder; see docs/ASSUMPTIONS.md.
    /// </summary>
    public sealed class VideoControlDevice : IDevice
    {
        public byte Value { get; private set; } = 0x10;     // green text on black
        public int Background => Value & 0x0F;
        public int Foreground => (Value >> 4) & 0x0F;

        public byte Read(ushort addr) => Value;
        public void Write(ushort addr, byte value) => Value = value;
    }
}

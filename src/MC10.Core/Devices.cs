using System;
using System.Collections.Generic;

namespace MC10.Core
{
    /// <summary>
    /// MC-10 keyboard matrix, derived from the firmware's scan routine at $F8A1-$F8F3:
    /// the CPU writes a column strobe to port 1 (bit n low selects column n, 8 columns) and reads the
    /// six row lines from $BFFF (bits 0-5, active low). Key index = row*8 + column:
    ///   0-26 @ A-Z, 30 ENTER, 31 SPACE, 32-39 0-7, 40-47 8 9 : ; , - . /
    /// Shift, Ctrl and Break arrive on port 2 bit 1 while column 7, 0 or 2 is strobed (Shift = column 7,
    /// Control = column 0, Break = column 2; confirmed by running the firmware: Ctrl+A backs up,
    /// Ctrl+W gives "^", Break stops a program). Host keystrokes are queued as timed "taps" so the
    /// firmware's edge-detecting scan always sees a press and a release.
    /// </summary>
    public sealed class MatrixKeyboard : IDevice
    {
        public const int HoldFrames = 4, GapFrames = 3;

        readonly Func<byte> strobe;                    // current port 1 output
        readonly byte[] cols = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        bool shift, ctrl, brk;
        readonly Queue<Tap> queue = new Queue<Tap>();
        readonly Queue<char> text = new Queue<char>();   // bulk text (pasted / sample programs), fed one key at a time
        Tap? active;
        int frames;
        bool holding;

        struct Tap { public int Index; public bool Shift, Ctrl, Break; }

        public MatrixKeyboard(Func<byte> port1Strobe) { strobe = port1Strobe; }

        public int Pending => queue.Count + text.Count + (active.HasValue ? 1 : 0);

        /// <summary>True while typed keys or bulk text are still waiting to be pressed.</summary>
        public bool Busy => queue.Count > 0 || text.Count > 0 || holding;

        /// <summary>Characters of bulk text not yet typed.</summary>
        public int TextPending => text.Count;

        public byte Read(ushort addr)
        {
            byte s = strobe();
            byte rows = 0xFF;
            for (int c = 0; c < 8; c++)
                if (((s >> c) & 1) == 0) rows &= cols[c];
            return rows;
        }

        public void Write(ushort addr, byte value) { }   // $BFFF writes: VDG/sound latch (not modelled)

        /// <summary>Row bits of one keyboard column (0 = key down), for the debugger's matrix view.</summary>
        public byte ColumnRows(int column) => cols[column & 7];
        public bool ShiftDown => shift;
        public bool CtrlDown => ctrl;
        public bool BreakDown => brk;
        /// <summary>Current port 1 strobe (bit n low selects column n).</summary>
        public byte Strobe => strobe();

        /// <summary>External levels on port 2: bit 1 low when a modifier on the strobed column is down.</summary>
        public byte ReadPort2()
        {
            byte s = strobe();
            bool low = (((s >> 7) & 1) == 0 && shift) || ((s & 1) == 0 && ctrl) || (((s >> 2) & 1) == 0 && brk);
            return low ? (byte)0xFD : (byte)0xFF;
        }

        /// <summary>Queue a key by matrix index (0-47) with optional modifiers.</summary>
        public void TapIndex(int index, bool withShift = false, bool withCtrl = false)
        {
            if (queue.Count < 32) queue.Enqueue(new Tap { Index = index, Shift = withShift, Ctrl = withCtrl });
        }

        public void TapBreak()
        {
            if (queue.Count < 32) queue.Enqueue(new Tap { Index = -1, Break = true });
        }

        /// <summary>True when the MC-10 has a key (or key chord) for this host character.</summary>
        public static bool HasKey(char ch)
        {
            if (ch == '\r' || ch == ' ' || ch == '\b' || ch == '^' || ch == '@') return true;
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) return true;
            return "!\"#$%&'()*+<=>?".IndexOf(ch) >= 0 || ":;,-./".IndexOf(ch) >= 0;
        }

        /// <summary>
        /// Queue a whole text (a BASIC listing, pasted text). Unlike <see cref="TapChar"/> there is no
        /// 32-key limit: characters are fed to the tap queue one at a time as the firmware consumes keys.
        /// Characters without a key are skipped; the number skipped is returned.
        /// </summary>
        public int TypeText(string value)
        {
            int skipped = 0;
            foreach (char ch in value.Replace("\r\n", "\r").Replace('\n', '\r'))
            {
                if (HasKey(ch)) text.Enqueue(ch); else skipped++;
            }
            return skipped;
        }

        /// <summary>Queue the key(s) that produce a host character. Returns false if there is none.</summary>
        public bool TapChar(char ch)
        {
            if (ch == '^') { TapIndex(23, false, true); return true; }          // CONTROL+W = exponent (manual)
            if (ch == '\r') { TapIndex(30); return true; }
            if (ch == ' ') { TapIndex(31); return true; }
            if (ch == '\b') { TapIndex(1, false, true); return true; }          // CONTROL+A = backspace (manual)
            if (ch >= 1 && ch <= 26) { TapIndex(ch, false, true); return true; } // Ctrl+letter
            // The MC-10 powers up in all-caps mode, where SHIFT+letter selects a block-graphics
            // character (confirmed by running the firmware scan: shift+A returns $89).
            // So both cases of a host letter send the plain key.
            if (ch >= 'A' && ch <= 'Z') { TapIndex(ch - 'A' + 1); return true; }
            if (ch >= 'a' && ch <= 'z') { TapIndex(ch - 'a' + 1); return true; }
            if (ch == '@') { TapIndex(0); return true; }
            if (ch >= '0' && ch <= '7') { TapIndex(32 + ch - '0'); return true; }
            if (ch == '8' || ch == '9') { TapIndex(40 + ch - '8'); return true; }
            int shifted = "!\"#$%&'()*+<=>?".IndexOf(ch);       // shifted 1-9 then punctuation
            if (shifted >= 0) { TapIndex(33 + shifted, true); return true; }
            int plain = ":;,-./".IndexOf(ch);
            if (plain >= 0) { TapIndex(42 + plain); return true; }
            return false;
        }

        /// <summary>Called once per display frame: advances the tap state machine.</summary>
        public void Advance()
        {
            if (frames > 0) frames--;
            if (frames > 0) return;
            if (holding)
            {
                Release();
                holding = false;
                frames = GapFrames;
                return;
            }
            if (queue.Count == 0 && text.Count > 0) TapChar(text.Dequeue());
            if (queue.Count == 0) { active = null; return; }
            Tap t = queue.Dequeue();
            active = t;
            Press(t);
            holding = true;
            frames = HoldFrames;
        }

        void Press(Tap t)
        {
            shift = t.Shift; ctrl = t.Ctrl; brk = t.Break;
            if (t.Index >= 0) cols[t.Index & 7] &= (byte)~(1 << (t.Index >> 3));
        }

        void Release()
        {
            for (int c = 0; c < 8; c++) cols[c] = 0xFF;
            shift = ctrl = brk = false;
        }

        public void Clear()
        {
            queue.Clear(); text.Clear(); active = null; holding = false; frames = 0; Release();
        }
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

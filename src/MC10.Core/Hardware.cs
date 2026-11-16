using System.Collections.Generic;

namespace MC10.Core
{
    /// <summary>Names for the 6803's internal registers and the MC-10 keyboard/VDG latch.</summary>
    public static class Hardware
    {
        static readonly Dictionary<ushort, string> names = new Dictionary<ushort, string>
        {
            [0x00] = "P1DDR", [0x01] = "P2DDR", [0x02] = "PORT1", [0x03] = "PORT2",
            [0x04] = "P3DDR", [0x05] = "P4DDR", [0x06] = "PORT3", [0x07] = "PORT4",
            [0x08] = "TCSR1", [0x09] = "FRC_H", [0x0A] = "FRC_L", [0x0B] = "OCR_H",
            [0x0C] = "OCR_L", [0x0D] = "ICR_H", [0x0E] = "ICR_L", [0x10] = "RMCR",
            [0x11] = "TRCSR", [0x12] = "RDR", [0x13] = "TDR", [0x14] = "RAMCR",
            [0xBFFF] = "KBD_VDG",
        };

        public static string NameFor(ushort addr) => names.TryGetValue(addr, out var n) ? n : null;
    }
}

using System.Linq;
using MC10.Core;
using Xunit;

namespace MC10.Tests
{
    public class AnalyzerTests
    {
        // 4 KB ROM at $F000, zero-filled (opcode $00 is illegal, so it stops code tracing).
        static byte[] NewRom() => new byte[0x1000];

        static void Put(byte[] rom, int addr, params byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++) rom[addr - 0xF000 + i] = bytes[i];
        }

        static byte[] SampleRom()
        {
            var rom = NewRom();
            Put(rom, 0xF000, 0x8D, 0x03);                    // BSR  F005
            Put(rom, 0xF002, 0x7E, 0xF0, 0x02);              // JMP  F002
            Put(rom, 0xF005, 0x86, 0x01);                    // LDAA #1
            Put(rom, 0xF007, 0x26, 0x02);                    // BNE  F00B
            Put(rom, 0xF009, 0x01, 0x01);                    // NOP NOP
            Put(rom, 0xF00B, 0xCE, 0xF0, 0x20);              // LDX  #$F020
            Put(rom, 0xF00E, 0x39);                          // RTS
            Put(rom, 0xF020, 0x48, 0x49, 0x21);              // data "HI!"
            Put(rom, 0xFFFE, 0xF0, 0x00);                    // reset vector -> F000
            return rom;
        }

        [Fact]
        public void NamesSubroutinesBranchTargetsAndData()
        {
            var an = CodeAnalyzer.Analyze(SampleRom(), 0xF000);
            Assert.Equal("reset", an.Labels[0xF000]);
            Assert.Equal("sub_F005", an.Labels[0xF005]);
            Assert.Contains((ushort)0xF005, an.Subroutines);
            Assert.Equal("loc_F002", an.Labels[0xF002]);
            Assert.Equal("loc_F00B", an.Labels[0xF00B]);
            Assert.Equal("data_F020", an.Labels[0xF020]);
        }

        [Fact]
        public void SeparatesCodeFromData()
        {
            var an = CodeAnalyzer.Analyze(SampleRom(), 0xF000);
            Assert.True(an.IsCodeStart(0xF00E));             // the RTS
            Assert.False(an.IsCodeStart(0xF020));            // table data
            Assert.False(an.IsCodeStart(0xF00F));            // zero byte after RTS
            Assert.Equal(8, an.InstructionCount);
        }

        [Fact]
        public void RecordsCallers()
        {
            var an = CodeAnalyzer.Analyze(SampleRom(), 0xF000);
            Assert.Equal(new ushort[] { 0xF000 }, an.Callers[0xF005].ToArray());
        }

        [Fact]
        public void FindsPointerTableAndEntries()
        {
            var rom = SampleRom();
            Put(rom, 0xF030, 0xF1, 0x00, 0xF1, 0x10, 0xF1, 0x20);   // three code pointers
            Put(rom, 0xF100, 0x01, 0x39);
            Put(rom, 0xF110, 0x01, 0x39);
            Put(rom, 0xF120, 0x01, 0x39);
            var an = CodeAnalyzer.Analyze(rom, 0xF000);
            Assert.Equal("tbl_F030", an.Labels[0xF030]);
            Assert.Equal("fn_F100", an.Labels[0xF100]);
            Assert.True(an.IsCodeStart(0xF120));
        }

        [Fact]
        public void ListingShowsLabelsAndNamedOperands()
        {
            var rom = SampleRom();
            var an = CodeAnalyzer.Analyze(rom, 0xF000);
            var lines = Listing.Build(an, a => an.Labels.TryGetValue(a, out var n) ? n : null,
                                      a => an.Labels.TryGetValue(a, out var n) ? n : null, "test");
            Assert.Contains(lines, l => l.Kind == LineKind.Label && l.Text.StartsWith("sub_F005:"));
            Assert.Contains(lines, l => l.Kind == LineKind.Code && l.Text.Contains("BSR") && l.Text.Contains("sub_F005"));
            Assert.Contains(lines, l => l.Kind == LineKind.Code && l.Text.Contains("LDX") && l.Text.Contains("#data_F020"));
            Assert.Contains(lines, l => l.Kind == LineKind.Data && l.Text.Contains("FDB") && l.Text.Contains("reset"));
        }

        [Fact]
        public void HardwareRegistersAreNamed()
        {
            Assert.Equal("PORT1", Hardware.NameFor(0x02));
            Assert.Equal("KBD_VDG", Hardware.NameFor(0xBFFF));
            Assert.Null(Hardware.NameFor(0x4000));
        }
    }
}

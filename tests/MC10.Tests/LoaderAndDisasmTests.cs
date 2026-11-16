using System.Collections.Generic;
using MC10.Core;
using Xunit;

namespace MC10.Tests
{
    public class LoaderAndDisasmTests
    {
        [Fact]
        public void IntelHexRecordIsParsed()
        {
            // Classic example: 3 data bytes (02 33 7A) at address $0030
            var segs = ImageLoader.ParseIntelHex(":0300300002337A1E\n:00000001FF\n");
            Assert.Single(segs);
            Assert.Equal(0x0030, segs[0].Address);
            Assert.Equal(new byte[] { 0x02, 0x33, 0x7A }, segs[0].Data);
        }

        [Fact]
        public void SRecordIsParsed()
        {
            // S1: count 05, address $1000, data AA BB, checksum 85
            var segs = ImageLoader.ParseSRecord("S1051000AABB85\n");
            Assert.Single(segs);
            Assert.Equal(0x1000, segs[0].Address);
            Assert.Equal(new byte[] { 0xAA, 0xBB }, segs[0].Data);
        }

        [Fact]
        public void EpromIgnoresWritesWhenReadOnly()
        {
            var rom = new Eprom(0xC000, new byte[] { 0x12, 0x34 }, EpromMode.ReadOnly);
            byte after = rom.Program(0xC000, 0x00);
            Assert.Equal(0x12, after);
            Assert.Equal(1, rom.IgnoredWrites);
        }

        [Fact]
        public void ByteProgrammableEpromOnlyClearsBits()
        {
            var rom = new Eprom(0xC000, new byte[] { 0xF0 }, EpromMode.ByteProgrammable);
            Assert.Equal(0xA0, rom.Program(0xC000, 0xAF));   // 1111 0000 AND 1010 1111
            rom.EraseByte(0xC000);
            Assert.Equal(0xFF, rom.Data[0]);
        }

        [Fact]
        public void Crc32MatchesKnownVector()
        {
            var rom = new Eprom(0, System.Text.Encoding.ASCII.GetBytes("123456789"));
            Assert.Equal(0xCBF43926u, rom.Crc32());
        }

        [Fact]
        public void OpcodeTableHasExpectedEntries()
        {
            Assert.Equal("LDAA", Opcodes.Table[0x86].Name);
            Assert.Equal(Mode.Imm, Opcodes.Table[0x86].Mode);
            Assert.Equal(2, Opcodes.Table[0x86].Cycles);
            Assert.Equal("MUL", Opcodes.Table[0x3D].Name);
            Assert.Equal("JSR", Opcodes.Table[0xBD].Name);
            Assert.Equal(Kind.Illegal, Opcodes.Table[0x02].Kind);
        }

        [Fact]
        public void DisassemblesImmediateAndRelative()
        {
            var mem = new Dictionary<ushort, byte> { [0x0100] = 0x86, [0x0101] = 0x10, [0x0102] = 0x20, [0x0103] = 0xFE };
            byte Read(ushort a) => mem.TryGetValue(a, out byte v) ? v : (byte)0;

            string t1 = Disassembler.Disassemble(Read, 0x0100, out int len1);
            Assert.Equal(2, len1);
            Assert.Contains("LDAA", t1);
            Assert.Contains("#$10", t1);

            // BRA -2 at $0102 branches to itself: target = $0104 + (-2) = $0102
            string t2 = Disassembler.Disassemble(Read, 0x0102, out int len2);
            Assert.Equal(2, len2);
            Assert.Contains("$0102", t2);
        }
    }
}

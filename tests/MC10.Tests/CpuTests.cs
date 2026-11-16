using System.Collections.Generic;
using MC10.Core;
using Xunit;

namespace MC10.Tests
{
    public class CpuTests
    {
        // Fresh machine with an empty (all $FF) ROM. Code is poked into RAM at $0100.
        static Machine NewMachine()
        {
            var m = new Machine();
            m.LoadRom(new List<ImageLoader.Segment>());
            return m;
        }

        static void Load(Machine m, ushort at, params byte[] code)
        {
            for (int i = 0; i < code.Length; i++) m.Bus.Poke((ushort)(at + i), code[i]);
            m.Cpu.PC = at;
            m.Cpu.SP = 0x0FFF;
        }

        static void Steps(Machine m, int n)
        {
            for (int i = 0; i < n; i++) m.Cpu.Step();
        }

        [Fact]
        public void AddWrapsToZeroAndSetsCarry()
        {
            var m = NewMachine();
            Load(m, 0x0100, 0x86, 0xFF, 0x8B, 0x01);      // LDAA #$FF ; ADDA #$01
            Steps(m, 2);
            var c = m.Cpu;
            Assert.Equal(0x00, c.A);
            Assert.True((c.CC & Cpu6803.FlagZ) != 0);
            Assert.True((c.CC & Cpu6803.FlagC) != 0);
            Assert.True((c.CC & Cpu6803.FlagN) == 0);
            Assert.True((c.CC & Cpu6803.FlagV) == 0);
        }

        [Fact]
        public void SignedOverflowSetsOverflowAndHalfCarry()
        {
            var m = NewMachine();
            Load(m, 0x0100, 0x86, 0x7F, 0x8B, 0x01);      // LDAA #$7F ; ADDA #$01
            Steps(m, 2);
            var c = m.Cpu;
            Assert.Equal(0x80, c.A);
            Assert.True((c.CC & Cpu6803.FlagV) != 0);
            Assert.True((c.CC & Cpu6803.FlagN) != 0);
            Assert.True((c.CC & Cpu6803.FlagH) != 0);
            Assert.True((c.CC & Cpu6803.FlagC) == 0);
        }

        [Fact]
        public void SubtractBorrowSetsCarry()
        {
            var m = NewMachine();
            Load(m, 0x0100, 0x86, 0x10, 0x80, 0x20);      // LDAA #$10 ; SUBA #$20
            Steps(m, 2);
            Assert.Equal(0xF0, m.Cpu.A);
            Assert.True((m.Cpu.CC & Cpu6803.FlagC) != 0);
            Assert.True((m.Cpu.CC & Cpu6803.FlagN) != 0);
        }

        [Fact]
        public void DecimalAdjustAfterBcdAdd()
        {
            var m = NewMachine();
            Load(m, 0x0100, 0x86, 0x15, 0x8B, 0x27, 0x19);  // 15 + 27 in BCD, then DAA
            Steps(m, 3);
            Assert.Equal(0x42, m.Cpu.A);
        }

        [Fact]
        public void BranchIfEqualIsTakenAfterClear()
        {
            var m = NewMachine();
            // 4F CLRA ; 27 02 BEQ +2 (-> $0105) ; 01 01 skipped ; C6 05 LDAB #5
            Load(m, 0x0100, 0x4F, 0x27, 0x02, 0x01, 0x01, 0xC6, 0x05);
            Steps(m, 3);
            Assert.Equal(0x05, m.Cpu.B);
        }

        [Fact]
        public void BsrAndRtsReturnToCaller()
        {
            var m = NewMachine();
            // $0100 BSR +2 -> $0104 ; $0102 NOP ; $0103 NOP ; $0104 LDAA #$77 ; $0106 RTS
            Load(m, 0x0100, 0x8D, 0x02, 0x01, 0x01, 0x86, 0x77, 0x39);
            ushort sp0 = m.Cpu.SP;
            Steps(m, 3);                                    // BSR, LDAA, RTS
            Assert.Equal(0x0102, m.Cpu.PC);
            Assert.Equal(0x77, m.Cpu.A);
            Assert.Equal(sp0, m.Cpu.SP);
        }

        [Fact]
        public void IrqPushesSevenBytesAndVectors()
        {
            var m = NewMachine();
            // Vector $FFF8 -> $0200 (poked into the ROM window)
            m.Bus.Poke(0xFFF8, 0x02);
            m.Bus.Poke(0xFFF9, 0x00);
            m.Bus.Poke(0x0200, 0x86);
            m.Bus.Poke(0x0201, 0x99);                       // LDAA #$99 at the handler
            Load(m, 0x0100, 0x0E, 0x01, 0x01);              // CLI ; NOP ; NOP
            ushort sp0 = m.Cpu.SP;
            m.Cpu.IrqLine = true;
            Steps(m, 1);                                    // CLI
            Steps(m, 1);                                    // interrupt taken instead of NOP
            Assert.Equal(0x0200, m.Cpu.PC);
            Assert.Equal((ushort)(sp0 - 7), m.Cpu.SP);
            Assert.True((m.Cpu.CC & Cpu6803.FlagI) != 0);
            m.Cpu.IrqLine = false;
            Steps(m, 1);
            Assert.Equal(0x99, m.Cpu.A);
        }

        [Fact]
        public void RtiRestoresInterruptedState()
        {
            var m = NewMachine();
            m.Bus.Poke(0xFFF8, 0x02);
            m.Bus.Poke(0xFFF9, 0x00);
            m.Bus.Poke(0x0200, 0x3B);                       // RTI
            Load(m, 0x0100, 0x86, 0x42, 0x0E, 0x01);        // LDAA #$42 ; CLI ; NOP
            m.Cpu.IrqLine = true;
            Steps(m, 2);                                    // LDAA, CLI
            Steps(m, 1);                                    // service -> $0200
            m.Cpu.IrqLine = false;
            Steps(m, 1);                                    // RTI
            Assert.Equal(0x42, m.Cpu.A);
            Assert.Equal(0x0103, m.Cpu.PC);                 // resumes after the CLI
        }

        [Fact]
        public void Mul6803LeavesProductInD()
        {
            var m = NewMachine();
            Load(m, 0x0100, 0x86, 0x10, 0xC6, 0x10, 0x3D);  // LDAA #16 ; LDAB #16 ; MUL
            Steps(m, 3);
            Assert.Equal(256, m.Cpu.D);
        }

        [Fact]
        public void IllegalOpcodeIsCountedAndSkipped()
        {
            var m = NewMachine();
            Load(m, 0x0100, 0x02, 0x86, 0x05);             // 02 is undefined on the 6803
            Assert.Equal(StopReason.IllegalOpcode, m.Step(ignoreBreakpoint: true));
            Assert.Equal(1, m.Cpu.IllegalCount);
            Assert.Equal(0x0101, m.Cpu.PC);
        }

        [Fact]
        public void BreakpointStopsBeforeInstructionAndResumes()
        {
            var m = NewMachine();
            Load(m, 0x0100, 0x86, 0x01, 0x86, 0x02);      // LDAA #1 ; LDAA #2
            m.Breakpoints.Add(0x0102);
            Assert.Equal(StopReason.Breakpoint, m.RunCycles(100));
            Assert.Equal(0x0102, m.Cpu.PC);
            Assert.Equal(StopReason.Budget, m.RunCycles(2));  // resume past the breakpoint (LDAA is 2 cycles)
            Assert.Equal(0x02, m.Cpu.A);
        }

        [Fact]
        public void WriteWatchpointStopsExecution()
        {
            var m = NewMachine();
            Load(m, 0x0100, 0x86, 0x33, 0x97, 0x80);      // LDAA #$33 ; STAA $80
            m.Bus.Watched[0x0080] = true;
            Steps(m, 1);
            Assert.Equal(StopReason.Watchpoint, m.Step(ignoreBreakpoint: true));
            Assert.Equal(0x33, m.Bus.Mem[0x0080]);
        }
    }
}

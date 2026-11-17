using System.Linq;
using MC10.Core;
using Xunit;

namespace MC10.Tests
{
    public class DebuggerSupportTests
    {
        static Machine WithProgram(params byte[] code)
        {
            var m = new Machine();
            for (int i = 0; i < code.Length; i++) m.Bus.Poke((ushort)(0x4100 + i), code[i]);
            m.Cpu.PC = 0x4100;
            m.Cpu.SP = 0x4F00;
            return m;
        }

        [Fact]
        public void PeekDoesNotClearTimerFlags()
        {
            var m = new Machine();
            m.Cpu.Io[8] = 0x60;                       // OCF and TOF set
            m.Bus.Peek(0x000A);                       // looking at FRC low ...
            m.Bus.Peek(0x000C);                       // ... and OCR low must not clear them
            Assert.Equal(0x60, m.Cpu.Io[8]);
            m.Bus.Read(0x000A);                       // a real read clears TOF
            Assert.Equal(0x40, m.Cpu.Io[8]);
        }

        [Fact]
        public void ControlIsColumn0AndBreakIsColumn2()
        {
            byte strobe = 0xFE;                       // column 0 low
            var k = new MatrixKeyboard(() => strobe);
            k.TapChar('\b');                          // CONTROL+A
            k.Advance();
            Assert.Equal(0xFD, k.ReadPort2());
            strobe = 0xFB;                            // column 2: CONTROL is not seen there
            Assert.Equal(0xFF, k.ReadPort2());

            var b = new MatrixKeyboard(() => strobe);
            b.TapBreak();
            b.Advance();
            Assert.Equal(0xFD, b.ReadPort2());        // BREAK on column 2
        }

        [Fact]
        public void CaretIsControlW()
        {
            byte strobe = 0x7F;                       // column 7: W is index 23 = row 2, column 7
            var k = new MatrixKeyboard(() => strobe);
            Assert.True(k.TapChar('^'));
            k.Advance();
            Assert.Equal(0xFB, k.Read(0xBFFF));
            Assert.True(k.CtrlDown);
        }

        [Fact]
        public void TypeTextHasNoLengthLimit()
        {
            byte strobe = 0xFF;
            var k = new MatrixKeyboard(() => strobe);
            string text = new string('A', 200) + "\n";
            Assert.Equal(0, k.TypeText(text));
            int presses = 0, guard = 0;
            bool wasDown = false;
            strobe = 0x00;                            // all columns selected
            while (k.Busy && guard++ < 10000)
            {
                k.Advance();
                bool down = k.Read(0xBFFF) != 0xFF;
                if (down && !wasDown) presses++;
                wasDown = down;
            }
            Assert.Equal(201, presses);
        }

        [Fact]
        public void TypeTextCountsCharactersWithoutAKey()
        {
            byte strobe = 0xFF;
            var k = new MatrixKeyboard(() => strobe);
            Assert.Equal(2, k.TypeText("A[B]"));
        }

        [Theory]
        [InlineData("A==41", true)]
        [InlineData("A!=41", false)]
        [InlineData("X>=4200", true)]
        [InlineData("X<4200", false)]
        [InlineData("[4100]==86", true)]
        [InlineData("CC&10", true)]
        [InlineData("b = 7", true)]
        public void BreakConditions(string text, bool expected)
        {
            var m = WithProgram(0x86, 0x41);
            m.Cpu.A = 0x41; m.Cpu.B = 7; m.Cpu.X = 0x4200; m.Cpu.CC = 0x10;
            Assert.Equal(expected, BreakCondition.Evaluate(text, m));
        }

        [Fact]
        public void InvalidConditionIsReported()
        {
            var m = new Machine();
            Assert.Null(BreakCondition.Evaluate("A is big", m));
            Assert.False(BreakCondition.IsValid("Q==1", m));
        }

        [Fact]
        public void ConditionalBreakpointOnlyStopsWhenTrue()
        {
            // 4100 INCA ; 4101 BRA 4100
            var m = WithProgram(0x4C, 0x20, 0xFD);
            m.Breakpoints.Add(0x4100);
            m.BreakConditions[0x4100] = "A==05";
            Assert.Equal(StopReason.Breakpoint, m.RunCycles(100000));
            Assert.Equal(5, m.Cpu.A);
            Assert.Equal(1, m.BreakHits[0x4100]);
        }

        [Fact]
        public void DisabledBreakpointDoesNotStop()
        {
            var m = WithProgram(0x4C, 0x20, 0xFD);
            m.Breakpoints.Add(0x4101);
            m.SetBreakpointEnabled(0x4101, false);
            Assert.Equal(StopReason.Budget, m.RunCycles(200));
            Assert.True(m.HasBreakpoint(0x4101));
            m.SetBreakpointEnabled(0x4101, true);
            Assert.Equal(StopReason.Breakpoint, m.RunCycles(200));
        }

        [Fact]
        public void HistoryKeepsRegistersBeforeTheInstruction()
        {
            var m = WithProgram(0x86, 0x41, 0x01);            // LDAA #$41 ; NOP
            m.Cpu.A = 0x11;
            m.Step(true);
            HistoryEntry h = m.History.Last();
            Assert.Equal(0x4100, h.Pc);
            Assert.Equal(0x11, h.A);                          // value before LDAA ran
            Assert.StartsWith("LDAA", h.Text);
        }

        [Fact]
        public void ProfileCountsInstructions()
        {
            var m = WithProgram(0x4C, 0x20, 0xFD);
            m.RunCycles(600);
            Assert.True(m.ExecCount[0x4100] > 10);
            Assert.Contains(m.BuildProfile(), p => p.Start == 0x4100 && p.Count > 20);
            m.ClearProfile();
            Assert.Equal(0, m.ExecCount[0x4100]);
        }

        [Fact]
        public void RunToReportsReached()
        {
            var m = WithProgram(0x01, 0x01, 0x01, 0x01);
            Assert.Equal(StopReason.Reached, m.RunTo(0x4102, 1000));
            Assert.Equal(0x4102, m.Cpu.PC);
        }

        [Fact]
        public void NamesResolveAndRename()
        {
            var m = new Machine();
            m.SetSymbol(0x4321, "my var");
            Assert.True(m.TryResolve("my_var", out ushort a));
            Assert.Equal(0x4321, a);
            Assert.True(m.TryResolve("$E71F", out a));
            Assert.Equal(0xE71F, a);
            Assert.False(m.TryResolve("nothing_here", out a));
            m.SetSymbol(0x4321, "");
            Assert.False(m.TryResolve("my_var", out a));
        }

        [Fact]
        public void SampleFileNotesAreNotTyped()
        {
            var s = SampleProgram.Parse("x.bas", ";; title: T\n;; summary: S\n;; look: L1\n;; break: stm_PRINT, fn_SIN\n10 PRINT 1\n\n20 END\n");
            Assert.Equal("T", s.Title);
            Assert.Equal("S", s.Summary);
            Assert.Equal(new[] { "L1" }, s.Look);
            Assert.Equal(new[] { "stm_PRINT", "fn_SIN" }, s.Breaks);
            Assert.Equal("10 PRINT 1\r20 END\r", s.Code);
        }
    }
}

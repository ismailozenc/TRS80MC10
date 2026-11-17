using System.Linq;
using MC10.Core;
using Xunit;

namespace MC10.Tests
{
    public class InsightTests
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
        public void ExplainsLoadImmediate()
        {
            var m = WithProgram(0x86, 0x41);                       // LDAA #$41
            string s = InstructionGuide.Describe(m.Bus.Peek, 0x4100, 0);
            Assert.StartsWith("Load A with the value $41 (65, 'A').", s);
            Assert.Contains("Flags: N Z, V=0.", s);
            Assert.Contains("2 bytes, 2 cycles.", s);
        }

        [Fact]
        public void IndexedOperandUsesX()
        {
            var m = WithProgram(0xA7, 0x05);                       // STAA $05,X
            OperandInfo o = InstructionGuide.Operand(m.Bus.Peek, 0x4100, 0x4200);
            Assert.True(o.HasAddress);
            Assert.True(o.UsesX);
            Assert.Equal(0x4205, o.Address);
            Assert.Equal(1, o.Size);
            Assert.False(o.IsCodeTarget);
        }

        [Fact]
        public void BranchExplanationSaysWhetherItIsTaken()
        {
            var m = WithProgram(0x27, 0x02);                       // BEQ +2
            m.Cpu.CC = 0x04;                                       // Z set
            Assert.Contains("the branch will be taken, to $4104", InstructionGuide.DescribeLive(m, 0x4100));
            m.Cpu.CC = 0x00;
            Assert.Contains("the branch will not be taken", InstructionGuide.DescribeLive(m, 0x4100));
            Assert.True(InstructionGuide.BranchTaken(7, 0x04));
            Assert.False(InstructionGuide.BranchTaken(6, 0x04));
        }

        [Fact]
        public void AutosListRegistersAndMemoryOfPreviousAndNext()
        {
            var m = WithProgram(0x86, 0x41, 0xB7, 0x42, 0x00);     // LDAA #$41 ; STAA $4200
            m.Step(true);
            var items = InstructionGuide.Autos(m);
            Assert.Equal("previous", items[0].Name);
            Assert.Contains(items, i => i.Name == "next" && i.Value == "4102");
            Assert.Contains(items, i => i.Name == "A" && i.Value == "41");
            Assert.Contains(items, i => i.HasAddress && i.Address == 0x4200 && !i.IsInstruction);
        }

        [Fact]
        public void WatchRegistersMemoryAndPointers()
        {
            var m = WithProgram(0x01);
            m.Bus.Poke(0x4280, 0x40); m.Bus.Poke(0x4281, 0x20);
            m.Bus.Poke(0x4020, 0x48); m.Bus.Poke(0x4021, 0x49);
            m.Cpu.X = 0x4280; m.Cpu.A = 0x7F;
            m.SetSymbol(0x4280, "cursor_addr");

            Assert.Equal(0x7F, WatchExpression.Evaluate("A", m).Value);
            Assert.Equal(0x4280, WatchExpression.Evaluate("x", m).Value);
            Assert.Equal(0x40, WatchExpression.Evaluate("4280", m).Value);
            Assert.Equal(0x4020, WatchExpression.Evaluate("cursor_addr.w", m).Value);
            Assert.Equal(0x40, WatchExpression.Evaluate("[X]", m).Value);
            Assert.Equal(0x20, WatchExpression.Evaluate("[X+1]", m).Value);
            Assert.Equal(0x48, WatchExpression.Evaluate("[cursor_addr.w]", m).Value);
            Assert.Equal("\"HI\"", WatchExpression.Evaluate("4020.s", m).Text);
            WatchResult r = WatchExpression.Evaluate("cursor_addr.w", m);
            Assert.True(r.HasAddress);
            Assert.Equal(0x4280, r.Address);
            Assert.Equal(2, r.Size);
        }

        [Fact]
        public void WatchReportsBadExpressions()
        {
            var m = new Machine();
            Assert.False(WatchExpression.Evaluate("no_such_label", m).Ok);
            Assert.False(WatchExpression.Evaluate("", m).Ok);
            Assert.False(WatchExpression.Evaluate("[X+zz]", m).Ok);
        }
    }
}

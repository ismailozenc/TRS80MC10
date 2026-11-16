using System;
using System.Collections.Generic;

namespace MC10.Core
{
    /// <summary>MC6803 addressing modes.</summary>
    public enum Mode : byte { Inh, Rel, Imm, Imm16, Dir, Idx, Ext }

    /// <summary>Instruction families. Sub-fields select the concrete operation.</summary>
    public enum Kind : byte
    {
        Inh, Branch, Bsr, Alu8, St8, Alu16, St16, Jsr, UnaryMem, UnaryReg, Illegal
    }

    public enum AluOp : byte { Sub, Cmp, Sbc, And, Bit, Ld, Eor, Adc, Or, Add }
    public enum Op16 : byte { Subd, Addd, Cpx, Lds, Ldd, Ldx, Sts, Std, Stx }
    public enum Unop : byte { Neg, Com, Lsr, Ror, Asr, Asl, Rol, Dec, Inc, Tst, Clr, Jmp }

    public enum Inh : byte
    {
        Nop, Tap, Tpa, Inx, Dex, Clv, Sev, Clc, Sec, Cli, Sei, Sba, Cba, Tab, Tba,
        Aba, Daa, Tsx, Txs, Ins, Des, Pula, Pulb, Psha, Pshb, Pulx, Pshx, Rts, Abx,
        Rti, Mul, Wai, Swi, Xgdx, Lsrd, Asld
    }

    /// <summary>One decoded table entry.</summary>
    public readonly struct OpInfo
    {
        public readonly Kind Kind;
        public readonly byte Sub;      // AluOp / Op16 / Unop / Inh / branch condition
        public readonly bool RegB;     // accumulator B (ALU, unary reg)
        public readonly Mode Mode;
        public readonly byte Cycles;
        public readonly string Name;   // mnemonic as shown by the disassembler

        public OpInfo(Kind kind, byte sub, bool regB, Mode mode, byte cycles, string name)
        {
            Kind = kind; Sub = sub; RegB = regB; Mode = mode; Cycles = cycles; Name = name;
        }
    }

    /// <summary>
    /// The 256-entry MC6803 opcode table. Shared by the CPU, disassembler and debugger.
    /// Cycle counts follow the MC6800/6803 data-sheet family and are approximate.
    /// </summary>
    public static class Opcodes
    {
        public static readonly OpInfo[] Table = new OpInfo[256];

        static Opcodes()
        {
            // Inherent
            Inh1(0x01, Inh.Nop, 2, "NOP"); Inh1(0x04, Inh.Lsrd, 3, "LSRD");
            Inh1(0x05, Inh.Asld, 3, "ASLD"); Inh1(0x06, Inh.Tap, 2, "TAP");
            Inh1(0x07, Inh.Tpa, 2, "TPA"); Inh1(0x08, Inh.Inx, 3, "INX");
            Inh1(0x09, Inh.Dex, 3, "DEX"); Inh1(0x0A, Inh.Clv, 2, "CLV");
            Inh1(0x0B, Inh.Sev, 2, "SEV"); Inh1(0x0C, Inh.Clc, 2, "CLC");
            Inh1(0x0D, Inh.Sec, 2, "SEC"); Inh1(0x0E, Inh.Cli, 2, "CLI");
            Inh1(0x0F, Inh.Sei, 2, "SEI"); Inh1(0x10, Inh.Sba, 2, "SBA");
            Inh1(0x11, Inh.Cba, 2, "CBA"); Inh1(0x16, Inh.Tab, 2, "TAB");
            Inh1(0x17, Inh.Tba, 2, "TBA"); Inh1(0x19, Inh.Daa, 2, "DAA");
            Inh1(0x1B, Inh.Aba, 2, "ABA"); Inh1(0x30, Inh.Tsx, 3, "TSX");
            Inh1(0x31, Inh.Ins, 3, "INS"); Inh1(0x32, Inh.Pula, 4, "PULA");
            Inh1(0x33, Inh.Pulb, 4, "PULB"); Inh1(0x34, Inh.Des, 3, "DES");
            Inh1(0x35, Inh.Txs, 3, "TXS"); Inh1(0x36, Inh.Psha, 3, "PSHA");
            Inh1(0x37, Inh.Pshb, 3, "PSHB"); Inh1(0x38, Inh.Pulx, 5, "PULX");
            Inh1(0x39, Inh.Rts, 5, "RTS"); Inh1(0x3A, Inh.Abx, 3, "ABX");
            Inh1(0x3B, Inh.Rti, 10, "RTI"); Inh1(0x3C, Inh.Pshx, 4, "PSHX");
            Inh1(0x3D, Inh.Mul, 10, "MUL"); Inh1(0x3E, Inh.Wai, 9, "WAI");
            Inh1(0x3F, Inh.Swi, 12, "SWI"); Inh1(0x8F, Inh.Xgdx, 3, "XGDX");

            // Register unary (A form at base, B form at base+0x10)
            UnaryReg(0x40, Unop.Neg, "NEG"); UnaryReg(0x43, Unop.Com, "COM");
            UnaryReg(0x44, Unop.Lsr, "LSR"); UnaryReg(0x46, Unop.Ror, "ROR");
            UnaryReg(0x47, Unop.Asr, "ASR"); UnaryReg(0x48, Unop.Asl, "ASL");
            UnaryReg(0x49, Unop.Rol, "ROL"); UnaryReg(0x4A, Unop.Dec, "DEC");
            UnaryReg(0x4C, Unop.Inc, "INC"); UnaryReg(0x4D, Unop.Tst, "TST");
            UnaryReg(0x4F, Unop.Clr, "CLR");

            // Memory unary: indexed 0x60+off, extended 0x70+off
            UnaryMem(0x00, Unop.Neg, "NEG"); UnaryMem(0x03, Unop.Com, "COM");
            UnaryMem(0x04, Unop.Lsr, "LSR"); UnaryMem(0x06, Unop.Ror, "ROR");
            UnaryMem(0x07, Unop.Asr, "ASR"); UnaryMem(0x08, Unop.Asl, "ASL");
            UnaryMem(0x09, Unop.Rol, "ROL"); UnaryMem(0x0A, Unop.Dec, "DEC");
            UnaryMem(0x0C, Unop.Inc, "INC"); UnaryMem(0x0D, Unop.Tst, "TST");
            UnaryMem(0x0F, Unop.Clr, "CLR");
            Set(0x6E, Kind.UnaryMem, (byte)Unop.Jmp, false, Mode.Idx, 4, "JMP");
            Set(0x7E, Kind.UnaryMem, (byte)Unop.Jmp, false, Mode.Ext, 3, "JMP");

            // Branches (sub = condition index = opcode & 0x0F)
            string[] br = { "BRA", "BRN", "BHI", "BLS", "BCC", "BCS", "BNE", "BEQ",
                            "BVC", "BVS", "BPL", "BMI", "BGE", "BLT", "BGT", "BLE" };
            for (int i = 0; i < 16; i++)
                Set((byte)(0x20 + i), Kind.Branch, (byte)i, false, Mode.Rel, 4, br[i]);
            Set(0x8D, Kind.Bsr, 0, false, Mode.Rel, 8, "BSR");

            // 8-bit accumulator ops: IMM base, DIR +0x10, IDX +0x20, EXT +0x30
            Alu8Row(0x80, AluOp.Sub, false, "SUBA"); Alu8Row(0x81, AluOp.Cmp, false, "CMPA");
            Alu8Row(0x82, AluOp.Sbc, false, "SBCA"); Alu8Row(0x84, AluOp.And, false, "ANDA");
            Alu8Row(0x85, AluOp.Bit, false, "BITA"); Alu8Row(0x86, AluOp.Ld, false, "LDAA");
            Alu8Row(0x88, AluOp.Eor, false, "EORA"); Alu8Row(0x89, AluOp.Adc, false, "ADCA");
            Alu8Row(0x8A, AluOp.Or, false, "ORAA"); Alu8Row(0x8B, AluOp.Add, false, "ADDA");
            Alu8Row(0xC0, AluOp.Sub, true, "SUBB"); Alu8Row(0xC1, AluOp.Cmp, true, "CMPB");
            Alu8Row(0xC2, AluOp.Sbc, true, "SBCB"); Alu8Row(0xC4, AluOp.And, true, "ANDB");
            Alu8Row(0xC5, AluOp.Bit, true, "BITB"); Alu8Row(0xC6, AluOp.Ld, true, "LDAB");
            Alu8Row(0xC8, AluOp.Eor, true, "EORB"); Alu8Row(0xC9, AluOp.Adc, true, "ADCB");
            Alu8Row(0xCA, AluOp.Or, true, "ORAB"); Alu8Row(0xCB, AluOp.Add, true, "ADDB");
            Set(0x97, Kind.St8, (byte)AluOp.Ld, false, Mode.Dir, 4, "STAA");
            Set(0xA7, Kind.St8, (byte)AluOp.Ld, false, Mode.Idx, 6, "STAA");
            Set(0xB7, Kind.St8, (byte)AluOp.Ld, false, Mode.Ext, 5, "STAA");
            Set(0xD7, Kind.St8, (byte)AluOp.Ld, true, Mode.Dir, 4, "STAB");
            Set(0xE7, Kind.St8, (byte)AluOp.Ld, true, Mode.Idx, 6, "STAB");
            Set(0xF7, Kind.St8, (byte)AluOp.Ld, true, Mode.Ext, 5, "STAB");

            // 16-bit ops
            Alu16Row(0x83, 0x93, 0xA3, 0xB3, Op16.Subd, "SUBD", 4, 6, 7, 7);
            Alu16Row(0xC3, 0xD3, 0xE3, 0xF3, Op16.Addd, "ADDD", 4, 6, 7, 7);
            Alu16Row(0x8C, 0x9C, 0xAC, 0xBC, Op16.Cpx, "CPX", 3, 4, 6, 5);
            Alu16Row(0x8E, 0x9E, 0xAE, 0xBE, Op16.Lds, "LDS", 3, 4, 6, 5);
            Alu16Row(0xCC, 0xDC, 0xEC, 0xFC, Op16.Ldd, "LDD", 3, 4, 6, 5);
            Alu16Row(0xCE, 0xDE, 0xEE, 0xFE, Op16.Ldx, "LDX", 3, 4, 6, 5);
            Set(0x9F, Kind.St16, (byte)Op16.Sts, false, Mode.Dir, 5, "STS");
            Set(0xAF, Kind.St16, (byte)Op16.Sts, false, Mode.Idx, 7, "STS");
            Set(0xBF, Kind.St16, (byte)Op16.Sts, false, Mode.Ext, 6, "STS");
            Set(0xDD, Kind.St16, (byte)Op16.Std, false, Mode.Dir, 5, "STD");
            Set(0xED, Kind.St16, (byte)Op16.Std, false, Mode.Idx, 7, "STD");
            Set(0xFD, Kind.St16, (byte)Op16.Std, false, Mode.Ext, 6, "STD");
            Set(0xDF, Kind.St16, (byte)Op16.Stx, false, Mode.Dir, 5, "STX");
            Set(0xEF, Kind.St16, (byte)Op16.Stx, false, Mode.Idx, 7, "STX");
            Set(0xFF, Kind.St16, (byte)Op16.Stx, false, Mode.Ext, 6, "STX");
            Set(0x9D, Kind.Jsr, 0, false, Mode.Dir, 5, "JSR");
            Set(0xAD, Kind.Jsr, 0, false, Mode.Idx, 5, "JSR");
            Set(0xBD, Kind.Jsr, 0, false, Mode.Ext, 6, "JSR");

            // Everything else is undefined
            for (int op = 0; op < 256; op++)
                if (Table[op].Name == null)
                    Table[op] = new OpInfo(Kind.Illegal, 0, false, Mode.Inh, 2, "FCB");
        }

        static void Set(byte op, Kind kind, byte sub, bool regB, Mode mode, byte cyc, string name)
        {
            if (Table[op].Name != null) throw new InvalidOperationException($"duplicate opcode {op:X2}");
            Table[op] = new OpInfo(kind, sub, regB, mode, cyc, name);
        }

        static void Inh1(byte op, Inh sub, byte cyc, string name) => Set(op, Kind.Inh, (byte)sub, false, Mode.Inh, cyc, name);

        static void UnaryReg(byte baseOp, Unop op, string name)
        {
            Set(baseOp, Kind.UnaryReg, (byte)op, false, Mode.Inh, 2, name + "A");
            Set((byte)(baseOp + 0x10), Kind.UnaryReg, (byte)op, true, Mode.Inh, 2, name + "B");
        }

        static void UnaryMem(byte off, Unop op, string name)
        {
            Set((byte)(0x60 + off), Kind.UnaryMem, (byte)op, false, Mode.Idx, 7, name);
            Set((byte)(0x70 + off), Kind.UnaryMem, (byte)op, false, Mode.Ext, 6, name);
        }

        static void Alu8Row(byte imm, AluOp op, bool regB, string name)
        {
            Set(imm, Kind.Alu8, (byte)op, regB, Mode.Imm, 2, name);
            Set((byte)(imm + 0x10), Kind.Alu8, (byte)op, regB, Mode.Dir, 3, name);
            Set((byte)(imm + 0x20), Kind.Alu8, (byte)op, regB, Mode.Idx, 5, name);
            Set((byte)(imm + 0x30), Kind.Alu8, (byte)op, regB, Mode.Ext, 4, name);
        }

        static void Alu16Row(byte imm, byte dir, byte idx, byte ext, Op16 op, string name,
                             byte ci, byte cd, byte cx, byte ce)
        {
            Set(imm, Kind.Alu16, (byte)op, false, Mode.Imm16, ci, name);
            Set(dir, Kind.Alu16, (byte)op, false, Mode.Dir, cd, name);
            Set(idx, Kind.Alu16, (byte)op, false, Mode.Idx, cx, name);
            Set(ext, Kind.Alu16, (byte)op, false, Mode.Ext, ce, name);
        }
    }
}

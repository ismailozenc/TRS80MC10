using System;
using System.Collections.Generic;

namespace MC10.Core
{
    /// <summary>
    /// Motorola MC6803 CPU: full MC6800 instruction set plus the 6801/6803 additions
    /// (MUL, ABX, ASLD/LSRD, PSHX/PULX, XGDX, WAI), the internal timer and I/O block at
    /// $0000-$001F, and the interrupt model (NMI, IRQ1, ICI, OCI, TOI, SCI, SWI, RESET).
    /// Condition codes: C=01 V=02 Z=04 N=08 I=10 H=20.
    /// </summary>
    public sealed class Cpu6803
    {
        public const byte FlagC = 0x01, FlagV = 0x02, FlagZ = 0x04, FlagN = 0x08, FlagI = 0x10, FlagH = 0x20;
        const byte CcrMask = 0x3F;

        public const ushort VecSci = 0xFFF0, VecToi = 0xFFF2, VecOci = 0xFFF4, VecIci = 0xFFF6,
                            VecIrq = 0xFFF8, VecSwi = 0xFFFA, VecNmi = 0xFFFC, VecReset = 0xFFFE;

        // Internal register offsets ($00-$1F)
        const int P1Ddr = 0, P2Ddr = 1, P1Dr = 2, P2Dr = 3, P3Ddr = 4, P4Ddr = 5, P3Dr = 6, P4Dr = 7,
                  Tcsr1 = 0x08, CntH = 0x09, CntL = 0x0A, OcrH = 0x0B, OcrL = 0x0C, IcrH = 0x0D, IcrL = 0x0E,
                  Rmcr = 0x10, Trcsr = 0x11, Rdr = 0x12, Tdr = 0x13, Ramcr = 0x14;
        const byte TcsrIcf = 0x80, TcsrOcf = 0x40, TcsrTof = 0x20;
        const byte TcsrEici = 0x10, TcsrEoci = 0x08, TcsrEtoi = 0x04;

        public byte A, B;
        public ushort X, SP, PC;
        public byte CC;
        public bool Halted;
        public long Cycles;
        public long Instructions;
        public long IllegalCount;
        public ushort LastIllegalPc;
        public bool IrqLine;          // external IRQ1 level
        public bool NmiPending;

        public readonly byte[] Io = new byte[32];
        public readonly byte[] PortIn = { 0xFF, 0xFF, 0xFF, 0xFF };
        public ushort Ocr = 0xFFFF, Icr, Frc;
        int frcDiv;

        readonly Bus bus;

        public Cpu6803(Bus bus)
        {
            this.bus = bus;
            bus.Io = this;
            Reset();
        }

        public ushort D
        {
            get { return (ushort)((A << 8) | B); }
            set { A = (byte)(value >> 8); B = (byte)value; }
        }

        public void Reset()
        {
            A = B = 0;
            X = 0;
            SP = 0;
            CC = FlagI;
            Halted = false;
            Cycles = 0;
            Instructions = 0;
            IllegalCount = 0;
            Frc = 0;
            frcDiv = 0;
            NmiPending = false;
            Array.Clear(Io, 0, Io.Length);
            PC = ReadWord(VecReset);
        }

        // ------------------------------------------------------------ bus access
        public byte Read(ushort a) => bus.Read(a);
        public void Write(ushort a, byte v) => bus.Write(a, v);

        public ushort ReadWord(ushort a)
        {
            return (ushort)((bus.Read(a) << 8) | bus.Read((ushort)(a + 1)));
        }

        public void WriteWord(ushort a, ushort v)
        {
            bus.Write(a, (byte)(v >> 8));
            bus.Write((ushort)(a + 1), (byte)v);
        }

        byte Fetch8()
        {
            byte v = bus.Read(PC);
            PC++;
            return v;
        }

        ushort Fetch16()
        {
            byte hi = Fetch8();
            return (ushort)((hi << 8) | Fetch8());
        }

        void Push8(byte v)
        {
            bus.Write(SP, v);
            SP--;
        }

        byte Pull8()
        {
            SP++;
            return bus.Read(SP);
        }

        void Push16(ushort v)          // low byte first so RTS pulls high first
        {
            Push8((byte)v);
            Push8((byte)(v >> 8));
        }

        ushort Pull16()
        {
            byte hi = Pull8();
            return (ushort)((hi << 8) | Pull8());
        }

        void StackAll()
        {
            Push8((byte)PC);
            Push8((byte)(PC >> 8));
            Push8((byte)X);
            Push8((byte)(X >> 8));
            Push8(A);
            Push8(B);
            Push8((byte)(CC | 0xC0));
        }

        void UnstackAll()
        {
            CC = (byte)(Pull8() & CcrMask);
            B = Pull8();
            A = Pull8();
            byte xh = Pull8();
            X = (ushort)((xh << 8) | Pull8());
            PC = Pull16();
        }

        // ------------------------------------------------------------ internal I/O
        public byte IoRead(ushort addr)
        {
            int r = addr & 0x1F;
            switch (r)
            {
                case P1Dr:
                case P2Dr:
                {
                    int ddr = Io[r - 2];
                    return (byte)((Io[r] & ddr) | (PortIn[r - 2] & ~ddr & 0xFF));
                }
                case Tcsr1: return Io[Tcsr1];
                case CntH: return (byte)(Frc >> 8);
                case CntL: Io[Tcsr1] &= unchecked((byte)~TcsrTof); return (byte)Frc;
                case OcrH: return (byte)(Ocr >> 8);
                case OcrL: Io[Tcsr1] &= unchecked((byte)~TcsrOcf); return (byte)Ocr;
                case IcrH: return (byte)(Icr >> 8);
                case IcrL: Io[Tcsr1] &= unchecked((byte)~TcsrIcf); return (byte)Icr;
                case Trcsr: return (byte)(0x20 | Io[Trcsr]);   // TDRE: transmitter empty
                default: return Io[r];
            }
        }

        public void IoWrite(ushort addr, byte val)
        {
            int r = addr & 0x1F;
            switch (r)
            {
                case Tcsr1:
                    Io[Tcsr1] = (byte)((Io[Tcsr1] & 0xE0) | (val & 0x1F));
                    break;
                case CntH:
                case CntL:
                case IcrH:
                case IcrL:
                case Rdr:
                    break;                                   // read-only here
                case OcrH: Ocr = (ushort)((val << 8) | (Ocr & 0xFF)); break;
                case OcrL: Ocr = (ushort)((Ocr & 0xFF00) | val); break;
                default: Io[r] = val; break;
            }
        }

        void Tick(int cycles)
        {
            frcDiv += cycles;
            while (frcDiv >= 4)                       // counter runs at E/4
            {
                frcDiv -= 4;
                Frc++;
                if (Frc == 0) Io[Tcsr1] |= TcsrTof;
                if (Frc == Ocr) Io[Tcsr1] |= TcsrOcf;
            }
        }

        // ------------------------------------------------------------ interrupts
        int PendingVector()
        {
            if ((CC & FlagI) != 0) return -1;
            if (IrqLine) return VecIrq;
            byte t = Io[Tcsr1];
            if ((t & TcsrIcf) != 0 && (t & TcsrEici) != 0) return VecIci;
            if ((t & TcsrOcf) != 0 && (t & TcsrEoci) != 0) return VecOci;
            if ((t & TcsrTof) != 0 && (t & TcsrEtoi) != 0) return VecToi;
            return -1;
        }

        int Service(ushort vector, bool stacked)
        {
            if (!stacked) StackAll();
            CC |= FlagI;
            Halted = false;
            PC = ReadWord(vector);
            return 12;
        }

        public void RequestNmi() => NmiPending = true;

        // ------------------------------------------------------------ execution
        /// <summary>Execute one instruction (or service one interrupt). Returns cycles used.</summary>
        public int Step()
        {
            int cyc;
            if (NmiPending)
            {
                NmiPending = false;
                cyc = Service(VecNmi, Halted);
            }
            else
            {
                int vec = PendingVector();
                if (vec >= 0)
                {
                    cyc = Service((ushort)vec, Halted);
                }
                else if (Halted)
                {
                    cyc = 4;                              // idle in WAI
                }
                else
                {
                    byte op = Fetch8();
                    OpInfo info = Opcodes.Table[op];
                    if (info.Kind == Kind.Illegal)
                    {
                        IllegalCount++;
                        LastIllegalPc = (ushort)(PC - 1);
                    }
                    Instructions++;
                    cyc = info.Cycles;
                    Execute(op, info);
                }
            }
            Cycles += cyc;
            Tick(cyc);
            return cyc;
        }

        void Execute(byte op, OpInfo info)
        {
            switch (info.Kind)
            {
                case Kind.Inh: ExecInh((Inh)info.Sub); break;
                case Kind.Branch: ExecBranch(info.Sub); break;
                case Kind.Bsr:
                {
                    sbyte off = (sbyte)Fetch8();
                    Push16(PC);
                    PC = (ushort)(PC + off);
                    break;
                }
                case Kind.Alu8: ExecAlu8((AluOp)info.Sub, info.RegB, info.Mode); break;
                case Kind.St8:
                {
                    ushort ea = Ea(info.Mode);
                    byte v = info.RegB ? B : A;
                    bus.Write(ea, v);
                    SetFlags(FlagN | FlagZ | FlagV, Nz8(v));
                    break;
                }
                case Kind.Alu16: ExecAlu16((Op16)info.Sub, info.Mode); break;
                case Kind.St16:
                {
                    ushort ea = Ea(info.Mode);
                    ushort v = (Op16)info.Sub == Op16.Std ? D : (Op16)info.Sub == Op16.Stx ? X : SP;
                    WriteWord(ea, v);
                    SetFlags(FlagN | FlagZ | FlagV, Nz16(v));
                    break;
                }
                case Kind.Jsr:
                {
                    ushort ea = Ea(info.Mode);
                    Push16(PC);
                    PC = ea;
                    break;
                }
                case Kind.UnaryMem: ExecUnaryMem((Unop)info.Sub, info.Mode); break;
                case Kind.UnaryReg: ExecUnaryReg((Unop)info.Sub, info.RegB); break;
                case Kind.Illegal: break;   // treated as a 2-cycle NOP
            }
        }

        // ------------------------------------------------------------ addressing
        ushort Ea(Mode mode)
        {
            switch (mode)
            {
                case Mode.Dir: return Fetch8();
                case Mode.Idx: return (ushort)(X + Fetch8());
                default: return Fetch16();
            }
        }

        byte Load8(Mode mode)
        {
            if (mode == Mode.Imm) return Fetch8();
            return bus.Read(Ea(mode));
        }

        ushort Load16(Mode mode)
        {
            if (mode == Mode.Imm16) return Fetch16();
            return ReadWord(Ea(mode));
        }

        // ------------------------------------------------------------ flag helpers
        static byte Nz8(int r)
        {
            byte f = 0;
            if ((r & 0x80) != 0) f |= FlagN;
            if ((r & 0xFF) == 0) f |= FlagZ;
            return f;
        }

        static byte Nz16(int r)
        {
            byte f = 0;
            if ((r & 0x8000) != 0) f |= FlagN;
            if ((r & 0xFFFF) == 0) f |= FlagZ;
            return f;
        }

        void SetFlags(byte mask, byte bits)
        {
            CC = (byte)((CC & ~mask) | (bits & mask));
        }

        /// <summary>a + m + cin; returns C|V|N|Z|H bits and the 8-bit result.</summary>
        static byte Add8(int a, int m, int cin, out byte r)
        {
            int sum = a + m + cin;
            byte f = 0;
            if (((a & 0xF) + (m & 0xF) + cin) > 0xF) f |= FlagH;
            if (sum > 0xFF) f |= FlagC;
            if (((a ^ sum) & (m ^ sum) & 0x80) != 0) f |= FlagV;
            r = (byte)sum;
            return (byte)(f | Nz8(r));
        }

        /// <summary>a - m - cin; returns C|V|N|Z bits and the 8-bit result.</summary>
        static byte Sub8(int a, int m, int cin, out byte r)
        {
            int diff = a - m - cin;
            byte f = 0;
            if (diff < 0) f |= FlagC;
            if (((a ^ m) & (a ^ diff) & 0x80) != 0) f |= FlagV;
            r = (byte)diff;
            return (byte)(f | Nz8(r));
        }

        static byte Add16(int a, int m, out ushort r)
        {
            int sum = a + m;
            byte f = 0;
            if (sum > 0xFFFF) f |= FlagC;
            if (((a ^ sum) & (m ^ sum) & 0x8000) != 0) f |= FlagV;
            r = (ushort)sum;
            return (byte)(f | Nz16(r));
        }

        static byte Sub16(int a, int m, out ushort r)
        {
            int diff = a - m;
            byte f = 0;
            if (diff < 0) f |= FlagC;
            if (((a ^ m) & (a ^ diff) & 0x8000) != 0) f |= FlagV;
            r = (ushort)diff;
            return (byte)(f | Nz16(r));
        }

        // ------------------------------------------------------------ 8-bit ALU
        void ExecAlu8(AluOp op, bool regB, Mode mode)
        {
            byte m = Load8(mode);
            int a = regB ? B : A;
            int cin = (CC & FlagC) != 0 ? 1 : 0;
            byte r = 0, bits;
            bool store = true;
            byte mask;
            switch (op)
            {
                case AluOp.Ld: r = m; bits = Nz8(m); mask = FlagN | FlagZ | FlagV; break;
                case AluOp.Add: bits = Add8(a, m, 0, out r); mask = FlagH | FlagC | FlagV | FlagN | FlagZ; break;
                case AluOp.Adc: bits = Add8(a, m, cin, out r); mask = FlagH | FlagC | FlagV | FlagN | FlagZ; break;
                case AluOp.Sub: bits = Sub8(a, m, 0, out r); mask = FlagC | FlagV | FlagN | FlagZ; break;
                case AluOp.Sbc: bits = Sub8(a, m, cin, out r); mask = FlagC | FlagV | FlagN | FlagZ; break;
                case AluOp.Cmp: bits = Sub8(a, m, 0, out r); mask = FlagC | FlagV | FlagN | FlagZ; store = false; break;
                case AluOp.And: r = (byte)(a & m); bits = Nz8(r); mask = FlagN | FlagZ | FlagV; break;
                case AluOp.Bit: r = (byte)(a & m); bits = Nz8(r); mask = FlagN | FlagZ | FlagV; store = false; break;
                case AluOp.Eor: r = (byte)(a ^ m); bits = Nz8(r); mask = FlagN | FlagZ | FlagV; break;
                case AluOp.Or: r = (byte)(a | m); bits = Nz8(r); mask = FlagN | FlagZ | FlagV; break;
                default: throw new InvalidOperationException(op.ToString());
            }
            if (store)
            {
                if (regB) B = r; else A = r;
            }
            SetFlags(mask, bits);
        }

        void ExecAlu16(Op16 op, Mode mode)
        {
            ushort m = Load16(mode);
            byte bits;
            switch (op)
            {
                case Op16.Subd: { bits = Sub16(D, m, out ushort r); D = r; SetFlags(FlagC | FlagV | FlagN | FlagZ, bits); break; }
                case Op16.Addd: { bits = Add16(D, m, out ushort r); D = r; SetFlags(FlagC | FlagV | FlagN | FlagZ, bits); break; }
                case Op16.Cpx: { bits = Sub16(X, m, out _); SetFlags(FlagC | FlagV | FlagN | FlagZ, bits); break; }
                case Op16.Ldd: D = m; SetFlags(FlagN | FlagZ | FlagV, Nz16(m)); break;
                case Op16.Ldx: X = m; SetFlags(FlagN | FlagZ | FlagV, Nz16(m)); break;
                case Op16.Lds: SP = m; SetFlags(FlagN | FlagZ | FlagV, Nz16(m)); break;
                default: throw new InvalidOperationException(op.ToString());
            }
        }

        // ------------------------------------------------------------ unary / shifts
        /// <summary>Returns the flag bits (C,V,N,Z), the result, and whether to store it.</summary>
        byte Unary(Unop op, byte v, out byte result, out bool store)
        {
            int cin = (CC & FlagC) != 0 ? 1 : 0;
            store = true;
            switch (op)
            {
                case Unop.Neg:
                    result = (byte)(-v);
                    return (byte)((v != 0 ? FlagC : 0) | (v == 0x80 ? FlagV : 0) | Nz8(result));
                case Unop.Com:
                    result = (byte)~v;
                    return (byte)(FlagC | Nz8(result));
                case Unop.Clr:
                    result = 0;
                    return FlagZ;
                case Unop.Tst:
                    result = v; store = false;
                    return Nz8(v);
                case Unop.Inc:
                    result = (byte)(v + 1);
                    return (byte)((v == 0x7F ? FlagV : 0) | Nz8(result) | (cin != 0 ? FlagC : 0));
                case Unop.Dec:
                    result = (byte)(v - 1);
                    return (byte)((v == 0x80 ? FlagV : 0) | Nz8(result) | (cin != 0 ? FlagC : 0));
                case Unop.Lsr:
                {
                    int cout = v & 1;
                    result = (byte)(v >> 1);
                    return (byte)((cout != 0 ? FlagC | FlagV : 0) | Nz8(result));
                }
                case Unop.Asr:
                {
                    int cout = v & 1;
                    result = (byte)((v >> 1) | (v & 0x80));
                    return ShiftFlags(result, cout);
                }
                case Unop.Asl:
                {
                    int cout = (v >> 7) & 1;
                    result = (byte)(v << 1);
                    return ShiftFlags(result, cout);
                }
                case Unop.Rol:
                {
                    int cout = (v >> 7) & 1;
                    result = (byte)((v << 1) | cin);
                    return ShiftFlags(result, cout);
                }
                case Unop.Ror:
                {
                    int cout = v & 1;
                    result = (byte)((v >> 1) | (cin << 7));
                    return ShiftFlags(result, cout);
                }
                default:
                    throw new InvalidOperationException(op.ToString());
            }
        }

        static byte ShiftFlags(byte r, int cout)
        {
            int n = (r >> 7) & 1;
            byte f = Nz8(r);
            if (cout != 0) f |= FlagC;
            if (n != cout) f |= FlagV;                 // V = N xor C
            return f;
        }

        const byte UnaryMask = FlagC | FlagV | FlagN | FlagZ;

        void ExecUnaryMem(Unop op, Mode mode)
        {
            ushort ea = Ea(mode);
            if (op == Unop.Jmp)
            {
                PC = ea;
                return;
            }
            byte bits = Unary(op, bus.Read(ea), out byte r, out bool store);
            if (store) bus.Write(ea, r);
            SetFlags(UnaryMask, bits);
        }

        void ExecUnaryReg(Unop op, bool regB)
        {
            byte v = regB ? B : A;
            byte bits = Unary(op, v, out byte r, out bool store);
            if (store)
            {
                if (regB) B = r; else A = r;
            }
            SetFlags(UnaryMask, bits);
        }

        // ------------------------------------------------------------ branches
        void ExecBranch(byte cond)
        {
            sbyte off = (sbyte)Fetch8();
            bool n = (CC & FlagN) != 0, v = (CC & FlagV) != 0, z = (CC & FlagZ) != 0, c = (CC & FlagC) != 0;
            bool take;
            switch (cond)
            {
                case 0: take = true; break;                  // BRA
                case 1: take = false; break;                 // BRN
                case 2: take = !(c || z); break;             // BHI
                case 3: take = c || z; break;                // BLS
                case 4: take = !c; break;                    // BCC
                case 5: take = c; break;                     // BCS
                case 6: take = !z; break;                    // BNE
                case 7: take = z; break;                     // BEQ
                case 8: take = !v; break;                    // BVC
                case 9: take = v; break;                     // BVS
                case 10: take = !n; break;                   // BPL
                case 11: take = n; break;                    // BMI
                case 12: take = n == v; break;               // BGE
                case 13: take = n != v; break;               // BLT
                case 14: take = !(z || (n != v)); break;     // BGT
                default: take = z || (n != v); break;        // BLE
            }
            if (take) PC = (ushort)(PC + off);
        }

        // ------------------------------------------------------------ inherent
        void ExecInh(Inh op)
        {
            switch (op)
            {
                case Inh.Nop: break;
                case Inh.Tap: CC = (byte)(A & CcrMask); break;
                case Inh.Tpa: A = (byte)(CC | 0xC0); break;
                case Inh.Inx: X++; SetFlags(FlagZ, X == 0 ? FlagZ : (byte)0); break;
                case Inh.Dex: X--; SetFlags(FlagZ, X == 0 ? FlagZ : (byte)0); break;
                case Inh.Clv: SetFlags(FlagV, 0); break;
                case Inh.Sev: SetFlags(FlagV, FlagV); break;
                case Inh.Clc: SetFlags(FlagC, 0); break;
                case Inh.Sec: SetFlags(FlagC, FlagC); break;
                case Inh.Cli: SetFlags(FlagI, 0); break;
                case Inh.Sei: SetFlags(FlagI, FlagI); break;
                case Inh.Sba:
                {
                    byte bits = Sub8(A, B, 0, out byte r);
                    A = r;
                    SetFlags(FlagC | FlagV | FlagN | FlagZ, bits);
                    break;
                }
                case Inh.Cba: SetFlags(FlagC | FlagV | FlagN | FlagZ, Sub8(A, B, 0, out _)); break;
                case Inh.Tab: B = A; SetFlags(FlagN | FlagZ | FlagV, Nz8(B)); break;
                case Inh.Tba: A = B; SetFlags(FlagN | FlagZ | FlagV, Nz8(A)); break;
                case Inh.Aba:
                {
                    byte bits = Add8(A, B, 0, out byte r);
                    A = r;
                    SetFlags(FlagH | FlagC | FlagV | FlagN | FlagZ, bits);
                    break;
                }
                case Inh.Daa: Daa(); break;
                case Inh.Tsx: X = (ushort)(SP + 1); break;
                case Inh.Txs: SP = (ushort)(X - 1); break;
                case Inh.Ins: SP++; break;
                case Inh.Des: SP--; break;
                case Inh.Pula: A = Pull8(); break;
                case Inh.Pulb: B = Pull8(); break;
                case Inh.Psha: Push8(A); break;
                case Inh.Pshb: Push8(B); break;
                case Inh.Pulx: { byte hi = Pull8(); X = (ushort)((hi << 8) | Pull8()); break; }
                case Inh.Pshx: Push16(X); break;
                case Inh.Rts: PC = Pull16(); break;
                case Inh.Abx: X = (ushort)(X + B); break;
                case Inh.Rti: UnstackAll(); break;
                case Inh.Mul:
                {
                    int r = A * B;
                    D = (ushort)r;
                    byte f = 0;
                    if (((r >> 7) & 1) != 0) f |= FlagC;   // C = bit 7 of the product
                    if ((r & 0xFFFF) == 0) f |= FlagZ;
                    SetFlags(FlagC | FlagZ, f);
                    break;
                }
                case Inh.Wai: StackAll(); Halted = true; break;
                case Inh.Swi:
                    StackAll();
                    CC |= FlagI;
                    PC = ReadWord(VecSwi);
                    break;
                case Inh.Xgdx:
                {
                    ushort d = D;
                    D = X;
                    X = d;
                    break;
                }
                case Inh.Lsrd:
                {
                    int cout = D & 1;
                    ushort r = (ushort)(D >> 1);
                    D = r;
                    SetFlags(FlagC | FlagV | FlagN | FlagZ,
                        (byte)((cout != 0 ? FlagC | FlagV : 0) | Nz16(r)));
                    break;
                }
                case Inh.Asld:
                {
                    int cout = (D >> 15) & 1;
                    ushort r = (ushort)(D << 1);
                    D = r;
                    int n = (r >> 15) & 1;
                    byte f = Nz16(r);
                    if (cout != 0) f |= FlagC;
                    if (n != cout) f |= FlagV;
                    SetFlags(FlagC | FlagV | FlagN | FlagZ, f);
                    break;
                }
                default:
                    throw new InvalidOperationException(op.ToString());
            }
        }

        void Daa()
        {
            int a = A;
            int corr = 0;
            bool cout = (CC & FlagC) != 0;
            if ((CC & FlagH) != 0 || (a & 0x0F) > 9) corr |= 0x06;
            if (cout || a > 0x99)
            {
                corr |= 0x60;
                cout = true;
            }
            A = (byte)(a + corr);
            SetFlags(FlagC | FlagN | FlagZ, (byte)((cout ? FlagC : 0) | Nz8(A)));
        }

        // ------------------------------------------------------------ run helpers
        /// <summary>Run until at least `budget` cycles have elapsed. Returns cycles run.</summary>
        public long Run(long budget)
        {
            long start = Cycles;
            while (Cycles - start < budget) Step();
            return Cycles - start;
        }
    }
}

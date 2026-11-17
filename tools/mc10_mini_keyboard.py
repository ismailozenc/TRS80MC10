# Minimal 6803 executor: enough to run the ROM keyboard routine ($F883) against a key matrix.
import sys
d=open(__import__('sys').argv[1],'rb').read();B0=0xE000
mem=bytearray(65536); mem[B0:B0+8192]=d
# RAM hooks default RTS
for a in (0x42A9,0x42AC,0x4285): mem[a]=0x39
mem[0x421D]=0x00; mem[0x421E]=0x10   # delay count (small)
mem[0x421C]=0xFF                      # caps lock on (default all-caps)
class M: pass
m=M(); m.a=m.b=0; m.x=0; m.sp=0x4F00; m.pc=0; m.c=m.z=m.n=m.v=0
cols=[0xFF]*8; shift=False
def strobe(): return mem[2]
def rd(a):
    a&=0xFFFF
    if a==0xBFFF:
        r=0xFF
        for c in range(8):
            if not (strobe()>>c)&1: r&=cols[c]
        return r
    if a==3:
        s=strobe()
        low=((not (s>>7)&1) and shift)
        return 0xFD if low else 0xFF
    return mem[a]
def wr(a,v):
    a&=0xFFFF
    if a>=0xE000: return
    mem[a]=v&0xFF
mem[0]=0xFF
def nz(v,bits=8):
    m.n=1 if v&(1<<(bits-1)) else 0; m.z=1 if v&((1<<bits)-1)==0 else 0
def f8(): v=rd(m.pc); m.pc=(m.pc+1)&0xFFFF; return v
def f16(): h=f8(); return (h<<8)|f8()
def push(v): wr(m.sp,v); m.sp=(m.sp-1)&0xFFFF
def pull(): m.sp=(m.sp+1)&0xFFFF; return rd(m.sp)
def push16(v): push(v&0xFF); push(v>>8)
def pull16(): h=pull(); return (h<<8)|pull()
def ea(mode):
    if mode=='d': return f8()
    if mode=='x': return (m.x+f8())&0xFFFF
    return f16()
def sub(a,b,c=0):
    r=a-b-c; m.c=1 if r<0 else 0; r&=0xFF; nz(r); return r
def step():
    op=f8()
    reg=lambda o:'a' if o<0xC0 else 'b'
    if op in (0x86,0xC6): v=f8(); setattr(m,reg(op),v); nz(v); m.v=0; return
    if 0x96<=op<=0xF6 and (op&0x0F)==6 and op not in (0x86,0xC6):
        mode={0x90:'d',0xA0:'x',0xB0:'e',0xD0:'d',0xE0:'x',0xF0:'e'}[op&0xF0]
        v=rd(ea(mode)); setattr(m,reg(op),v); nz(v); m.v=0; return
    if op in (0x97,0xA7,0xB7,0xD7,0xE7,0xF7):
        mode={0x90:'d',0xA0:'x',0xB0:'e',0xD0:'d',0xE0:'x',0xF0:'e'}[op&0xF0]
        v=getattr(m,reg(op)); wr(ea(mode),v); nz(v); m.v=0; return
    if op==0x49 or op==0x59:
        r='a' if op==0x49 else 'b'; v=getattr(m,r); nv=((v<<1)|m.c)&0xFF; m.c=(v>>7)&1; setattr(m,r,nv); nz(nv); return
    if op==0x7C: a=f16(); v=(rd(a)+1)&0xFF; wr(a,v); nz(v); return
    if op==0x08: m.x=(m.x+1)&0xFFFF; m.z=1 if m.x==0 else 0; return
    if op==0x09: m.x=(m.x-1)&0xFFFF; m.z=1 if m.x==0 else 0; return
    if op in (0x88,0x98,0xA8,0xB8):
        mode={0x88:'i',0x98:'d',0xA8:'x',0xB8:'e'}[op]; v=f8() if mode=='i' else rd(ea(mode)); m.a^=v; nz(m.a); m.v=0; return
    if op in (0x84,0x94,0xA4,0xB4):
        mode={0x84:'i',0x94:'d',0xA4:'x',0xB4:'e'}[op]; v=f8() if mode=='i' else rd(ea(mode)); m.a&=v; nz(m.a); m.v=0; return
    if op in (0x8A,0xCA):
        v=f8(); r=reg(op); setattr(m,r,getattr(m,r)|v); nz(getattr(m,r)); m.v=0; return
    if op==0xF8: v=rd(ea("e")); m.b^=v; nz(m.b); return
    if op==0xF6: v=rd(ea("e")); m.b=v; nz(v); return
    if op==0xC4: v=f8(); m.b&=v; nz(m.b); return
    if op==0x1B: r=m.a+m.b; m.a=r&0xFF; nz(m.a); return
    if op in (0xF1,0xC1,0xD1): v=f8() if op==0xC1 else rd(ea('e' if op==0xF1 else 'd')); sub(m.b,v); return
    if op in (0xA1,0x81,0x91,0xB1): mode={0x81:'i',0x91:'d',0xA1:'x',0xB1:'e'}[op]; v=f8() if mode=='i' else rd(ea(mode)); sub(m.a,v); return
    if op==0x16: m.b=m.a; nz(m.b); m.v=0; return
    if op==0x17: m.a=m.b; nz(m.a); m.v=0; return
    if op==0x4F: m.a=0; m.z=1;m.n=0;m.v=0;m.c=0; return
    if op==0x5F: m.b=0; m.z=1;m.n=0;m.v=0;m.c=0; return
    if op==0x5A: m.b=(m.b-1)&0xFF; nz(m.b); return
    if op==0x4D: nz(m.a); m.v=0; m.c=0; return
    if op==0x5D: nz(m.b); m.v=0; m.c=0; return
    if op==0x0D: m.c=1; return
    if op==0x0C: m.c=0; return
    if op==0x36: push(m.a); return
    if op==0x37: push(m.b); return
    if op==0x32: m.a=pull(); return
    if op==0x33: m.b=pull(); return
    if op==0x3C: push16(m.x); return
    if op==0x38: m.x=pull16(); return
    if op==0x39: m.pc=pull16(); return
    if op==0x3A: m.x=(m.x+m.b)&0xFFFF; return
    if op==0x44: m.c=m.a&1; m.a>>=1; nz(m.a); return
    if op==0x43: m.a=(~m.a)&0xFF; nz(m.a); m.c=1; return
    if op==0x73: a=f16(); v=(~rd(a))&0xFF; wr(a,v); nz(v); m.c=1; return
    if op==0xCB: v=f8(); m.b=(m.b+v)&0xFF; nz(m.b); return
    if op==0x8B: v=f8(); m.a=(m.a+v)&0xFF; nz(m.a); return
    if op==0xFB: v=rd(ea('e')); m.b=(m.b+v)&0xFF; nz(m.b); return
    if op==0x8C: v=f16(); r=(m.x-v)&0xFFFF; m.z=1 if r==0 else 0; return
    if op==0xCE: m.x=f16(); nz(m.x,16); return
    if op==0xFE: m.x=rd(ea('e'))<<8|rd(ea.__call__('e') if False else 0); return
    if 0x20<=op<=0x2F:
        off=f8(); off=off-256 if off>127 else off
        cond={0x20:True,0x24:not m.c,0x25:bool(m.c),0x26:not m.z,0x27:bool(m.z),0x2B:bool(m.n),0x2A:not m.n,0x23:bool(m.c or m.z),0x22:not(m.c or m.z)}.get(op,None)
        if cond: m.pc=(m.pc+off)&0xFFFF
        return
    if op==0x8D: off=f8(); off=off-256 if off>127 else off; push16(m.pc); m.pc=(m.pc+off)&0xFFFF; return
    if op==0xBD: a=f16(); push16(m.pc); m.pc=a; return
    if op==0x7E: m.pc=f16(); return
    raise Exception("unhandled opcode %02X at %04X"%(op,m.pc-1))
# LDX extended needs proper impl:
_old=step
def step2():
    pc=m.pc; op=rd(pc)
    if op==0xFE:
        m.pc+=1; a=f16(); m.x=(rd(a)<<8)|rd(a+1); nz(m.x,16); return
    return _old()
def call(addr,maxsteps=100000):
    m.sp=0x4F00; push16(0xFFFF); m.pc=addr; n=0
    while m.pc!=0x0000FFFF+1 and m.pc!=0xFFFF+0 and n<maxsteps:
        if m.pc==0xFFFF: break
        step2(); n+=1
    return n
mem[0x421D]=0x00; mem[0x421E]=0x10
def press(idx,sh=False):
    global shift
    for c in range(8): cols[c]=0xFF
    shift=sh
    if idx>=0: cols[idx&7]&=~(1<<(idx>>3))&0xFF
def scan(label):
    r=call(0xF883)
    print(label,"A=%02X Z=%d steps=%d"%(m.a,m.z,r))
    return m.a if not m.z else None
for name,idx,sh in [("A",1,False),("a(shift)",1,True),("P",16,False),("SPACE",31,False),("ENTER",30,False),("0",32,False),("@",0,False),("/",47,False)]:
    press(-1)
    call(0xF883)               # release
    press(idx,sh)
    got=scan(name)
    press(-1); call(0xF883)

import sys
d=open(__import__('sys').argv[1],'rb').read();B=0xE000
inh={0x01:'NOP',0x04:'LSRD',0x05:'ASLD',0x06:'TAP',0x07:'TPA',0x08:'INX',0x09:'DEX',0x0A:'CLV',0x0B:'SEV',0x0C:'CLC',0x0D:'SEC',0x0E:'CLI',0x0F:'SEI',0x10:'SBA',0x11:'CBA',0x16:'TAB',0x17:'TBA',0x19:'DAA',0x1B:'ABA',0x30:'TSX',0x31:'INS',0x32:'PULA',0x33:'PULB',0x34:'DES',0x35:'TXS',0x36:'PSHA',0x37:'PSHB',0x38:'PULX',0x39:'RTS',0x3A:'ABX',0x3B:'RTI',0x3C:'PSHX',0x3D:'MUL',0x3E:'WAI',0x3F:'SWI',0x8F:'XGDX'}
un=[('NEG',0),('COM',3),('LSR',4),('ROR',6),('ASR',7),('ASL',8),('ROL',9),('DEC',0xA),('INC',0xC),('TST',0xD),('CLR',0xF)]
for n,o in un:
    inh[0x40+o]=n+'A';inh[0x50+o]=n+'B'
br="BRA BRN BHI BLS BCC BCS BNE BEQ BVC BVS BPL BMI BGE BLT BGT BLE".split()
alu={}
for n,b in [('SUB',0x80),('CMP',0x81),('SBC',0x82),('AND',0x84),('BIT',0x85),('LDA',0x86),('EOR',0x88),('ADC',0x89),('ORA',0x8A),('ADD',0x8B)]:
    alu[b]=n+'A';alu[b+0x40]=n+'B'
a16={0x83:'SUBD',0xC3:'ADDD',0x8C:'CPX',0x8E:'LDS',0xCC:'LDD',0xCE:'LDX'}
def dis(a):
    o=d[a-B];n=1
    if o in inh:return inh[o],1
    if 0x20<=o<=0x2F:
        r=d[a-B+1];r=r-256 if r>127 else r
        return "%s %04X"%(br[o-0x20],a+2+r),2
    if o==0x8D:
        r=d[a-B+1];r=r-256 if r>127 else r
        return "BSR %04X"%(a+2+r),2
    hi=o&0xF0;lo=o&0x0F
    def op(mode):
        if mode=='i':return "#$%02X"%d[a-B+1],2
        if mode=='I':return "#$%04X"%((d[a-B+1]<<8)|d[a-B+2]),3
        if mode=='d':return "$%02X"%d[a-B+1],2
        if mode=='x':return "$%02X,X"%d[a-B+1],2
        return "$%04X"%((d[a-B+1]<<8)|d[a-B+2]),3
    if o in alu or o in [x+0x10 for x in alu] or o in [x+0x20 for x in alu] or o in [x+0x30 for x in alu]:
        base=o&~0x30 if (o&~0x30) in alu else None
        if base is not None:
            m={0x00:'i',0x10:'d',0x20:'x',0x30:'e'}[o&0x30]
            s,l=op(m);return alu[base]+' '+s,l
    for b,nm in a16.items():
        if o==b: s,l=op('I' if True else 'i');return nm+' '+s,l
        if o==b+0x10: s,l=op('d');return nm+' '+s,l
        if o==b+0x20: s,l=op('x');return nm+' '+s,l
        if o==b+0x30: s,l=op('e');return nm+' '+s,l
    st={0x97:('STAA','d'),0xA7:('STAA','x'),0xB7:('STAA','e'),0xD7:('STAB','d'),0xE7:('STAB','x'),0xF7:('STAB','e'),0xDD:('STD','d'),0xED:('STD','x'),0xFD:('STD','e'),0xDF:('STX','d'),0xEF:('STX','x'),0xFF:('STX','e'),0x9F:('STS','d'),0xAF:('STS','x'),0xBF:('STS','e'),0x9D:('JSR','d'),0xAD:('JSR','x'),0xBD:('JSR','e'),0x6E:('JMP','x'),0x7E:('JMP','e')}
    if o in st: s,l=op(st[o][1]);return st[o][0]+' '+s,l
    for nm,off in un:
        if o==0x60+off: s,l=op('x');return nm+' '+s,l
        if o==0x70+off: s,l=op('e');return nm+' '+s,l
    return "FCB $%02X"%o,1
a=int(sys.argv[2],16);end=int(sys.argv[3],16)
while a<end:
    t,l=dis(a);print("%04X  %-9s %s"%(a,d[a-B:a-B+l].hex(' '),t));a+=l

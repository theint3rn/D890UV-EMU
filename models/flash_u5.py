# STM32U5 FLASH controller (non-secure view) @0x40022000, 2 banks x 128 pages x 8 KB.
# Replaces Renode's STM32WBA controller, which ignores NSCR.BKER and so erased bank-1 code when the
# firmware erased its bank-2 table cache (0x081E4000). Programming is done by the CPU writing the
# flash memory directly (MappedMemory is writable); this model only handles lock, erase and status.
#   0x00 ACR, 0x08 NSKEYR, 0x10 OPTKEYR, 0x20 NSSR, 0x28 NSCR, 0x40 OPTR
if request.IsInit:
    regs = {0x00: 0x0, 0x28: 0x80000000, 0x40: 0x1FEFF8AA}   # NSCR locked, OPTR: RDP level 0
    keyseq = []
    BASE = 0x08000000
elif request.IsWrite:
    o, v = request.Offset, request.Value
    if o == 0x08:                                   # NSKEYR unlock sequence
        keyseq = (keyseq + [v])[-2:]
        if keyseq == [0x45670123, 0xCDEF89AB]:
            regs[0x28] = regs.get(0x28, 0) & ~0x80000000
    elif o == 0x20:                                 # NSSR: write-1-to-clear flags
        regs[0x20] = regs.get(0x20, 0) & ~v
    elif o == 0x28:                                 # NSCR
        if regs.get(0x28, 0) & 0x80000000 and not (v & 0x80000000):
            pass                                    # locked: ignore (LOCK can only be set)
        else:
            regs[0x28] = v
            if v & (1 << 16):                       # STRT
                if v & (1 << 1):                    # PER: page erase
                    page = (v >> 3) & 0x7F
                    bank = (v >> 11) & 1
                    addr = BASE + bank * 0x100000 + page * 0x2000
                    from System import Array, Byte
                    self.GetMachine().SystemBus.WriteBytes(Array[Byte]([0xFF] * 0x2000), addr)
                regs[0x28] = v & ~(1 << 16)
                regs[0x20] = regs.get(0x20, 0) | 1  # EOP
    else:
        regs[o] = v
else:
    o = request.Offset
    if o == 0x20:
        request.Value = regs.get(0x20, 0) & ~((1 << 16) | (1 << 17))   # never BSY/WDW
    else:
        request.Value = regs.get(o, 0)

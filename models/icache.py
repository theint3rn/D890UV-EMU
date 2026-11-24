# STM32U5 ICACHE @0x40030400: invalidate completes immediately (BSYENDF set, never busy).
if request.IsInit:
    regs = {}
elif request.IsWrite:
    if request.Offset != 0x0C:
        regs[request.Offset] = request.Value & ~2  # CACHEINV self-clears
else:
    request.Value = 0x2 if request.Offset == 0x04 else regs.get(request.Offset, 0)

# STM32U5 PWR @0x46020800: voltage scaling / boost / supplies always ready.
if request.IsInit:
    regs = {}
elif request.IsWrite:
    regs[request.Offset] = request.Value
else:
    v = regs.get(request.Offset, 0)
    if request.Offset == 0x0C:      # VOSR: VOSRDY, BOOSTRDY if BOOSTEN
        v |= (1 << 15) | ((1 << 14) if v & (1 << 18) else 0)
    elif request.Offset == 0x3C:    # SVMSR: ACTVOSRDY, ACTVOS mirrors VOS, supplies ready, REGS
        vos = (regs.get(0x0C, 0) >> 16) & 3
        v = (1 << 15) | (vos << 16) | (0xF << 24) | (1 << 1)
    elif request.Offset == 0x38:    # SR
        v = 0
    request.Value = v

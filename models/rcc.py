# STM32U5 RCC @0x46020C00: clocks become ready as soon as they are enabled.
# Test knob (cfw pre-flash test F9): write 1 to offset 0x3FC (sysbus WriteDoubleWord 0x46020FFC 1) = HSE never ready.
if request.IsInit:
    regs = {0x00: 0x00000035, 0x1C: 0x0, 0xF0: 0x0, 0xF4: 0x0C000000}
    def ready_cr(v):
        for on, rdy in ((0, 2), (4, 5), (8, 10), (12, 13), (14, 15), (16, 17), (24, 25), (26, 27), (28, 29)):
            v = (v | (1 << rdy)) if v & (1 << on) else (v & ~(1 << rdy))
        if regs.get(0x3FC, 0):
            v &= ~(1 << 17)
        return v | (1 << 2)  # MSIS always running (reset clock)
    def sws(v):
        return (v & ~0xC) | ((v & 0x3) << 2)
    def bdcr(v):
        v = (v | 2) if v & 1 else v
        v = (v | (1 << 27)) if v & (1 << 26) else v
        v = (v | (1 << 11)) if v & (1 << 7) else v
        return v
    rules = {0x00: ready_cr, 0x1C: sws, 0xF0: bdcr}
elif request.IsWrite:
    regs[request.Offset] = request.Value
else:
    v = regs.get(request.Offset, 0)
    f = rules.get(request.Offset)
    request.Value = f(v) if f else v

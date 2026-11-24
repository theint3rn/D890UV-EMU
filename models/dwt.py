# DWT @0xE0001000: CYCCNT advances on every read so busy-wait delays end.
if request.IsInit:
    regs = {}
    cyc = 0
elif request.IsWrite:
    if request.Offset == 0x04:
        cyc = request.Value
    else:
        regs[request.Offset] = request.Value
else:
    if request.Offset == 0x04:
        cyc = (cyc + 4096) & 0xFFFFFFFF
        request.Value = cyc
    else:
        request.Value = regs.get(request.Offset, 0)

# Plain register storage (reads return last written value). Used for EXTI, GPDMA, SYSCFG, CRS, etc.
if request.IsInit:
    regs = {}
elif request.IsWrite:
    regs[request.Offset] = request.Value
else:
    request.Value = regs.get(request.Offset, 0)

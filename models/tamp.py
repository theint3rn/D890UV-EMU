# STM32U5 TAMP @0x46007C00 (backup registers at +0x100). BKP0R = 0x5050 means "RTC already set"
# (firmware FUN_08062150); without it the radio shows its set-date/time screen on every boot.
if request.IsInit:
    regs = {0x100: 0x5050}
elif request.IsWrite:
    regs[request.Offset] = request.Value
else:
    request.Value = regs.get(request.Offset, 0)

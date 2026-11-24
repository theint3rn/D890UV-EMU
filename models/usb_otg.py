# STM32U5 USB OTG_FS @0x42040000 (Synopsys core), minimal: soft resets finish at once,
# AHB idle, no host attached. Enough for USB device init to complete; no enumeration yet.
if request.IsInit:
    regs = {0x40: 0x4F54330A}   # GSNPSID
elif request.IsWrite:
    o = request.Offset
    if o == 0x14:                       # GINTSTS: write-1-to-clear
        regs[o] = regs.get(o, 0) & ~request.Value
    else:
        regs[o] = request.Value
else:
    o = request.Offset
    v = regs.get(o, 0)
    if o == 0x10:                       # GRSTCTL: CSRST/RXFFLSH/TXFFLSH self-clear, AHBIDL set
        v = (v & ~0x31) | 0x80000000
    request.Value = v

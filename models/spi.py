# STM32U5 SPI (H7-style IP) minimal: always ready (TXP/RXP/EOT), RXDR returns 0xFF.
if request.IsInit:
    regs = {}
elif request.IsWrite:
    regs[request.Offset] = request.Value
else:
    o = request.Offset
    if o == 0x14:            # SR: RXP TXP EOT TXTF
        request.Value = 0x1 | 0x2 | 0x8 | 0x10
    elif o == 0x30:          # RXDR
        request.Value = 0xFF
    else:
        request.Value = regs.get(o, 0)

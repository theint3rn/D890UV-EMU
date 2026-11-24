# STM32U5 ADC1/ADC4 (one script, both instances): regulator, calibration and conversions finish at once.
# DR returns ADC_VALUE (mid-scale default). Change at runtime: <adc> SetValue? -> see emu/README.md
if request.IsInit:
    regs = {}
    value = 0x7FF
elif request.IsWrite:
    o, v = request.Offset, request.Value
    if o == 0x00:            # ISR: write-1-to-clear
        regs[0x00] = regs.get(0x00, 0) & ~v
    elif o == 0x08:          # CR
        isr = regs.get(0x00, 0) | (1 << 12)             # LDORDY
        if v & 1:
            isr |= 1                                     # ADRDY
        if v & (1 << 2):
            isr |= (1 << 1) | (1 << 2) | (1 << 3)        # EOSMP EOC EOS
        if v & (1 << 31):
            isr |= (1 << 11)                             # EOCAL (ADC4)
        regs[0x00] = isr
        regs[0x08] = v & ~((1 << 31) | (1 << 2) | (1 << 1) | (1 << 4))  # ADCAL ADSTART ADDIS ADSTP self-clear
    else:
        regs[o] = v
else:
    o = request.Offset
    if o == 0x40:
        request.Value = value
        regs[0x00] = regs.get(0x00, 0) & ~(1 << 2)
    else:
        request.Value = regs.get(o, 0)

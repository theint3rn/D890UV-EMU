# SPI2 @0x40003800 + SCT3288 DMR baseband (behavioural model), D890UV.
# Bus (FUN_0810a674 / FUN_0810a6dc): CS# PB12, MCU reads while PD11 (data ready) is high.
# Frame both ways: 84 A9 61 LenH LenL B0 <Len bytes> [pad if Len odd]   (FUN_0813af78)
# Default reply to any MCU frame: 84 A9 61 00 02 B0 <first payload byte> 00  (command echo + status OK),
# as in the SCT_Port scripts (e.g. CALL_END 00 21 -> 00 21 00). Per-command replies: RULES below.
# Every MCU frame and reply is appended to emu/out/baseband.log (path set in INIT via LOG).
if request.IsInit:
    regs = {}
    rx = []            # bytes queued baseband -> MCU
    inbuf = []         # bytes MCU -> baseband (frame assembly)
    # RULES: first payload byte (command) -> full reply payload (bytes after LenL), or None for no reply
    RULES = {}
    # Unsolicited messages sent once SPI2 is enabled: (B0, id, data...). 0x10 = status/ready (waited
    # for by FUN_080f83a4(0x10, ..., 1, 50000)).
    STARTUP = [[0x00, 0x10, 0x00], [0x00, 0x17, 0x00]]
    started = False
    # Status messages repeated whenever the reply queue runs dry (the MCU polls for them in loops).
    PERIODIC = [[0x00, 0x17, 0x00]]
    import os
    LOGPATH = os.environ.get("D890_BB_LOG", "/tmp/d890_baseband.log")
    def bb_log(s):
        try:
            f = open(LOGPATH, "a"); f.write(s + "\n"); f.close()
        except Exception:
            pass
    def drdy(level):
        try:
            port = self.GetMachine()["sysbus.gpioPortD"]
            port.OnGPIO(11, level)     # byte/data ready
            port.OnGPIO(7, level)      # frame pending (FUN_0810a6dc second mode)
        except Exception:
            pass
    def frame(payload):
        n = len(payload) - 1
        out = [0x84, 0xA9, 0x61, (n >> 8) & 0xFF, n & 0xFF] + list(payload)
        if n & 1:
            out.append(0)
        return out
    def handle(fr):
        body = fr[5:]
        cmd = body[1] if len(body) > 1 else 0
        bb_log("MCU> " + " ".join("%02X" % b for b in fr))
        reply = RULES.get(cmd, [body[0], cmd, 0x00])
        if reply is None:
            return
        out = frame(reply)
        bb_log("BB < " + " ".join("%02X" % b for b in out))
        rx.extend(out)
        drdy(True)
    drdy(False)
    bb_log("init " + LOGPATH)
elif request.IsWrite:
    o, v = request.Offset, request.Value
    if o == 0x20:                                   # TXDR
        bb_log("tx %02X" % (v & 0xFF))
        if rx:
            pass                                    # MCU is reading: TX byte is a dummy
        else:
            b = v & 0xFF
            if not inbuf and b != 0x84:
                pass
            else:
                inbuf.append(b)
                if len(inbuf) >= 2 and inbuf[1] != 0xA9: inbuf = []
                elif len(inbuf) >= 3 and inbuf[2] != 0x61: inbuf = []
                elif len(inbuf) >= 6:
                    n = (inbuf[3] << 8) | inbuf[4]
                    total = 6 + n + (n & 1)
                    if len(inbuf) >= total:
                        handle(inbuf[:total])
                        inbuf = []
        if not rx and started and inbuf == [] and PERIODIC:
            for m in PERIODIC:
                rx.extend(frame(m))
        regs[0x30] = rx.pop(0) if rx else 0xFF
        if rx: bb_log("rx-> %02X" % regs[0x30])
        if not rx and not (started and PERIODIC):
            drdy(False)
    elif o == 0x18:                                 # IFCR
        pass
    elif o == 0x00 and (v & 1) and not started:     # CR1.SPE: link up -> baseband announces itself
        started = True
        regs[o] = v
        for m in STARTUP:
            out = frame(m)
            bb_log("BB < " + " ".join("%02X" % b for b in out) + "  (startup)")
            rx.extend(out)
        drdy(True)
    else:
        regs[o] = v
else:
    o = request.Offset
    if o == 0x14:                                   # SR: RXP TXP EOT TXTF, RX FIFO level 1
        request.Value = 0x1 | 0x2 | 0x8 | 0x10 | (1 << 13)
    elif o == 0x30:
        request.Value = regs.get(0x30, 0xFF)
    else:
        request.Value = regs.get(o, 0)

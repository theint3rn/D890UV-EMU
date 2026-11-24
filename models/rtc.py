# STM32U5 RTC @0x46007800: init mode and shadow sync are immediate; the calendar runs on virtual time.
# TR (0x00) / DR (0x04) written in init mode set the calendar; reads return set time + elapsed virtual time.
if request.IsInit:
    regs = {0x0C: 0x7}
    def vt():
        try:
            return self.GetMachine().LocalTimeSource.ElapsedVirtualTime.TotalSeconds
        except Exception:
            return 0.0
    def bcd(v):
        return ((v // 10) << 4) | (v % 10)
    def unbcd(v):
        return (v >> 4) * 10 + (v & 0xF)
    def mdays(y, m):
        if m == 2:
            return 29 if y % 4 == 0 else 28
        return 30 if m in (4, 6, 9, 11) else 31
    def to_secs(tr, dr):
        y = 2000 + unbcd(dr >> 16 & 0xFF); m = unbcd(dr >> 8 & 0x1F); d = unbcd(dr & 0x3F)
        days = sum(366 if yy % 4 == 0 else 365 for yy in range(2000, y))
        days += sum(mdays(y, mm) for mm in range(1, max(m, 1))) + max(d, 1) - 1
        hh = unbcd(tr >> 16 & 0x3F); mi = unbcd(tr >> 8 & 0x7F); ss = unbcd(tr & 0x7F)
        return days * 86400 + hh * 3600 + mi * 60 + ss
    def from_secs(t):
        days, rem = divmod(int(t), 86400)
        wd = (days + 5) % 7 + 1              # 2000-01-01 = Saturday (6), Monday = 1
        y = 2000
        while days >= (366 if y % 4 == 0 else 365):
            days -= 366 if y % 4 == 0 else 365; y += 1
        m = 1
        while days >= mdays(y, m):
            days -= mdays(y, m); m += 1
        tr = (bcd(rem // 3600) << 16) | (bcd(rem // 60 % 60) << 8) | bcd(rem % 60)
        dr = (bcd(y - 2000) << 16) | (wd << 13) | (bcd(m) << 8) | bcd(days + 1)
        return tr, dr
    base = to_secs(0x00120000, 0x00262101)   # 12:00:00, 2026-01-01 until the firmware sets it
    base_vt = 0.0
    pend_tr, pend_dr = None, None
elif request.IsWrite:
    o, v = request.Offset, request.Value
    if o in (0x00, 0x04):
        tr, dr = from_secs(base + vt() - base_vt)
        if o == 0x00: tr = v
        else: dr = v
        base = to_secs(tr, dr); base_vt = vt()
    regs[o] = v
else:
    o = request.Offset
    if o == 0x0C:                       # ICSR: INITF follows INIT, RSF/INITS/WUTWF set
        v = regs.get(0x0C, 0)
        v = (v | (1 << 6)) if v & (1 << 7) else (v & ~(1 << 6))
        request.Value = v | (1 << 5) | (1 << 4) | (1 << 2)
    elif o in (0x00, 0x04):
        tr, dr = from_secs(base + vt() - base_vt)
        request.Value = tr if o == 0x00 else dr
    elif o == 0x08:                     # SSR counts down from PREDIV_S (255) each second
        t = base + vt() - base_vt
        request.Value = int((1.0 - (t - int(t))) * 255) & 0xFF
    else:
        request.Value = regs.get(o, 0)

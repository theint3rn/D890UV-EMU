# Renode monitor command: dump_state <file>. CPU registers, fault status, RTOS markers, lift trap.
# Every step is guarded and errors are written into the file (monitor output is not visible headless).
def mc_dump_state(path):
    out = []
    def step(label, fn):
        try:
            fn()
        except Exception as e:
            out.append("ERR %s: %s" % (label, e))
    m = self.Machine
    bus = m["sysbus"]
    cpu = m["sysbus.cpu"]
    rd = lambda a: bus.ReadDoubleWord(a)
    def reg(n):
        try:
            return int(cpu.GetRegister(n).RawValue)
        except Exception:
            return -1
    st = {}
    def core():
        st["pc"] = int(cpu.PC.RawValue)
        out.append("PC=0x%08x SP=0x%08x LR=0x%08x" % (st["pc"], int(cpu.SP.RawValue), reg(14)))
        out.append("R0-R3=0x%08x 0x%08x 0x%08x 0x%08x" % (reg(0), reg(1), reg(2), reg(3)))
    step("core", core)
    step("count", lambda: out.append("instructions=%d halted=%s" % (int(cpu.ExecutedInstructions), cpu.IsHalted)))
    def faults():
        icsr, cfsr, hfsr = rd(0xE000ED04), rd(0xE000ED28), rd(0xE000ED2C)
        out.append("ICSR=0x%08x (active exc %d) CFSR=0x%08x HFSR=0x%08x MMFAR=0x%08x BFAR=0x%08x" %
                   (icsr, icsr & 0x1FF, cfsr, hfsr, rd(0xE000ED34), rd(0xE000ED38)))
        if hfsr & 0x40000000 or cfsr:
            out.append("FAULT: HardFault/escalated fault recorded")
    step("faults", faults)
    step("rtos", lambda: out.append("OSRunning=%d OSPrioCur=%d OSPrioHighRdy=%d OSTCBCurPtr=0x%08x" %
               (bus.ReadByte(0x2000A479), bus.ReadByte(0x2000A3F2), bus.ReadByte(0x2000A3F3), rd(0x2000A47C))))
    def trap():
        a = int(bus.GetSymbolAddress("g_lift_trap_address"))
        out.append("lift_trap=0x%08x" % rd(a))
    try:
        trap()
    except Exception:
        pass
    if "pc" in st:
        try:
            out.append("pc_symbol=%s" % bus.FindSymbolAt(st["pc"]))
        except Exception:
            pass
    f = open(path, "w")
    f.write("\n".join(out) + "\n")
    f.close()


# pc_sample <file>: append "virtual_ms PC LR OSRunning" (one line per call).
def mc_pc_sample(path):
    m = self.Machine
    cpu = m["sysbus.cpu"]
    bus = m["sysbus"]
    try:
        ms = m.ElapsedVirtualTime.TimeElapsed.TotalMilliseconds
    except Exception:
        ms = -1
    try:
        lr = int(cpu.GetRegister(14).RawValue)
    except Exception:
        lr = 0
    f = open(path, "a")
    f.write("%d 0x%08x 0x%08x %d\n" % (ms, int(cpu.PC.RawValue), lr, bus.ReadByte(0x2000A479)))
    f.close()


# dump_mem <addr> <len> <file>: write raw bytes.
def mc_dump_mem(addr, n, path):
    bus = self.Machine["sysbus"]
    data = bus.ReadBytes(int(addr), int(n))
    f = open(path, "wb")
    f.write(bytearray(data))
    f.close()


# nand_peek <addr> <n> <file>: model contents (not via the MCU).
def mc_nand_peek(addr, n, path):
    nand = self.Machine["sysbus.nand"]
    f = open(path, "a")
    f.write("%08x %s\n" % (int(addr), nand.Peek(int(addr), int(n))))
    f.close()


# dump_words <addr> <count> <file>: 32-bit reads (peripherals), appended as hex text.
def mc_dump_words(addr, n, path):
    bus = self.Machine["sysbus"]
    f = open(path, "a")
    f.write(" ".join("%08x" % bus.ReadDoubleWord(int(addr) + 4 * i) for i in range(int(n))) + "\n")
    f.close()

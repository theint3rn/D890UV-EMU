#!/usr/bin/env python3
"""Run D890UV firmware in Renode (headless) and collect results.

Examples:
  emu/run.py --orig --seconds 3                 # original V1.05 image
  emu/run.py --elf build/d890uv.elf --seconds 3 # lifted build
  emu/run.py --elf build/d890uv.elf --gdb 3333  # start halted, wait for GDB on :3333
  emu/run.py --orig --trace-periph              # log every peripheral access (slow)

Writes emu/out/<name>/: renode.log, run.resc, state.txt (PC/LR/SP, fault registers,
g_lift_trap_address for lifted builds), uart*.txt, gpio.csv (when --trace-gpio).
Exit code: 0 ran to the end, 2 CPU halted/faulted, 3 Renode error.
"""
import argparse, os, shutil, subprocess, sys, time, re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
RENODE = os.environ.get("RENODE", os.path.expanduser("~/.local/opt/renode_1.17.0-portable/renode"))
ORIG = os.path.join(os.path.dirname(ROOT), "D890UV V1.05 DMR official release 260521",
                    "D890UV_V1.05_DMR FW", "D890UV_V1.05_20260521.CDD")
UARTS = ["usart1", "usart2", "usart3", "uart4", "uart5", "lpuart1"]


def main():
    ap = argparse.ArgumentParser()
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--orig", action="store_true", help="original V1.05 .CDD")
    g.add_argument("--image", help="raw image (.CDD/.bin) loaded at 0x0800C000")
    g.add_argument("--elf", help="ELF (lifted build)")
    ap.add_argument("--seconds", type=float, default=2.0, help="virtual time to run")
    ap.add_argument("--gdb", type=int, help="start GDB server on this port and wait (interactive)")
    ap.add_argument("--trace-periph", action="store_true", help="log all peripheral accesses")
    ap.add_argument("--trace-gpio", action="store_true", help="record GPIO output changes to gpio.csv")
    ap.add_argument("--name", help="output subfolder name")
    ap.add_argument("--timeout", type=int, default=900, help="host seconds before giving up")
    ap.add_argument("--extra", action="append", default=[], help="extra Renode monitor line(s)")
    ap.add_argument("--no-rfcal", action="store_true", help="cfw run without the synthetic RF calibration block")
    ap.add_argument("--bootstub", help="binary at 0x08000000 instead of images/bootstub.bin (cfw pre-flash test: "
                    "a 'dirty' bootloader exit, cfw/tools/preflash_test.py)")
    ap.add_argument("--uart-socket", action="append", default=[], metavar="UART:PORT",
                    help="expose a UART as a raw TCP server on localhost (e.g. usart1:3470 = programming cable); "
                         "the usart*.txt capture keeps working")
    ap.add_argument("--then", action="append", default=[],
                    help='after the main run: "SECONDS|monitor command" (repeatable), e.g. "0.3|lcd Press 5 150"')
    ap.add_argument("--nand", help="NAND pages file (default images/extflash.pages if present, else images/synth.pages "
                                   "from make_nand.py)")
    ap.add_argument("--save-nand", action="store_true", help="save NAND contents at the end (nand.pages)")
    ap.add_argument("--power-off", action="store_true", help="leave the power switch (PA0) off")
    ap.add_argument("--serve", type=int, default=0,
                    help="boot for --seconds, then keep Renode running with its monitor on this telnet port "
                         "(drive it with emu/ctl.py)")
    ap.add_argument("--exact", action="store_true",
                    help="no speedups (default: busy-wait delay FUN_0811ece0 skipped with SkipTime, idle task sleeps in WFI)")
    ap.add_argument("--samples", type=int, default=0, help="sample PC this many times over the run (pc_samples.txt)")
    a = ap.parse_args()

    name = a.name or ("orig" if a.orig else os.path.splitext(os.path.basename(a.elf or a.image))[0])
    out = os.path.join(HERE, "out", name)
    os.makedirs(out, exist_ok=True)
    img_dir = os.path.join(out, "img")
    os.makedirs(img_dir, exist_ok=True)

    # Images: cfw ELFs need only our own files; stock runs (--orig, --image, lifted ELF) need files made from the
    # user's own AnyTone package (not published). See README "Images".
    cfw = bool(a.elf) and is_cfw_elf(a.elf)
    activation = os.path.join(HERE, "images", "activation_0800A000.bin")
    if not cfw:
        missing = [f for f in ([ORIG] if a.orig else []) + [activation] if not os.path.exists(f)]
        if missing:
            sys.exit("stock run needs files made from your own AnyTone D890UV package (not part of this repo):\n  "
                     + "\n  ".join(missing) + "\nsee emu/README.md \"Images\" (build_extflash.py, activation key).")
    a.nand = a.nand or default_nand(stock=not cfw)

    # Renode's tokenizer dislikes spaces in paths: stage inputs into the output folder.
    if a.elf:
        load = "sysbus LoadELF @%s" % stage(a.elf, img_dir)
    else:
        load = "sysbus LoadBinary @%s 0x0800C000" % stage(ORIG if a.orig else a.image, img_dir)

    global NAND_PAGES
    NAND_PAGES = a.nand
    abs_repl = make_platform(out)
    lines = ["include @%s" % os.path.join(HERE, "models", "SpiNandBitbang.cs"),
             "include @%s" % os.path.join(HERE, "models", "BoardInputs.cs"),
             "include @%s" % os.path.join(HERE, "models", "LcdKeypad.cs"),
             "include @%s" % os.path.join(HERE, "models", "RfTransceiver.cs"),
             "include @%s" % os.path.join(HERE, "models", "Baseband.cs"),
             "include @%s" % os.path.join(HERE, "models", "Analog.cs"),
             "include @%s" % os.path.join(HERE, "models", "GpsNmea.cs"),
             "include @%s" % os.path.join(HERE, "models", "Rtc.cs"),
             "include @%s" % os.path.join(HERE, "models", "Bluetooth.cs"),
             "include @%s" % os.path.join(HERE, "models", "Knob.cs"),
             "include @%s" % os.path.join(HERE, "models", "I2cBus.cs"),
             "include @%s" % os.path.join(HERE, "models", "SymbolSource.cs"),
             "include @%s" % os.path.join(HERE, "models", "Dac.cs"),
             "include @%s" % os.path.join(HERE, "models", "Pwm.cs"),
             "include @%s" % os.path.join(HERE, "models", "PcmTap.cs"),
             'mach create "d890uv"',
             "machine LoadPlatformDescription @%s" % abs_repl,
             "logFile @%s" % os.path.join(out, "renode.log"),
             "logLevel 1",
             load]
    for u in UARTS:
        lines.append("%s CreateFileBackend @%s true" % (u, os.path.join(out, "%s.txt" % u)))
    if a.trace_periph:
        lines.append("sysbus LogAllPeripheralsAccess true")
    if a.trace_gpio:
        lines.append("include @%s" % os.path.join(HERE, "gpio_trace.py"))
        lines.append('gpio_trace_start "%s"' % os.path.join(out, "gpio.csv"))
    # Power switch (PA0) and key idle levels come from the `board` model (stm32u575.repl).
    if a.power_off:
        lines.append('board Set "A0" false')
    # RF read hook (see models/RfTransceiver.cs): emulate FUN_080e2da4 (16-bit SDA read).
    rf_read = 0x080E2DA4
    if a.elf:
        try:
            nm = subprocess.check_output(["arm-none-eabi-nm", a.elf], text=True)
            rf_read = next(int(l.split()[0], 16) for l in nm.splitlines() if l.endswith(" FUN_080e2da4"))
        except Exception:
            rf_read = None
    if rf_read:
        lines.append('cpu AddHook 0x%08X "from Antmicro.Renode.Peripherals.CPU import RegisterValue; '
                     'from Antmicro.Renode.Core import EmulationManager; '
                     'm = list(EmulationManager.Instance.CurrentEmulation.Machines)[0]; '
                     'self.SetRegister(0, RegisterValue.Create(m[\'sysbus.rf0\'].ReadActive(), 32)); '
                     'self.PC = self.GetRegister(14)"' % (rf_read & ~1))
    # Speedups (same behaviour, less host work). The firmware's microsecond delay FUN_0811ece0(us) polls
    # SysTick VAL millions of times (each poll is a slow MMIO exit in Renode): a hook advances virtual time
    # by `us` instead. The OS idle task (FUN_080c18d4) spins; its call to the idle hook FUN_080c0318 is
    # redirected to a cave in free flash (0x081E3F00: wfi; b.w FUN_080c0318) so the CPU sleeps until the
    # next interrupt. Original image only for the idle patch; the delay hook uses the ELF symbol.
    if not a.exact:
        delay = 0x0811ECE0
        if a.elf:
            try:
                nm = subprocess.check_output(["arm-none-eabi-nm", a.elf], text=True)
                delay = next(int(l.split()[0], 16) for l in nm.splitlines() if l.endswith(" FUN_0811ece0"))
            except Exception:
                delay = None
        if delay:
            lines.append('cpu AddHook 0x%08X "from Antmicro.Renode.Time import TimeInterval; '
                         'self.SkipTime(TimeInterval.FromMicroseconds(self.GetRegister(0).RawValue)); '
                         'self.PC = self.GetRegister(14)"' % (delay & ~1))
        if a.orig:
            lines.append("sysbus LoadBinary @%s 0x081E3F00" % os.path.join(HERE, "images", "idle_cave.bin"))
            lines.append("sysbus LoadBinary @%s 0x080C18FA" % os.path.join(HERE, "images", "idle_call.bin"))
    # Decoded-audio ring of the custom firmware (cfw hal/voice.c): copy it to out/<name>/rx_pcm.raw (8 kHz int16)
    if a.elf:
        try:
            syms = {l.split()[2]: int(l.split()[0], 16) for l in
                    subprocess.check_output(["arm-none-eabi-nm", a.elf], text=True).splitlines() if len(l.split()) == 3}
            if "g_emu_pcm" in syms and "g_emu_pcm_wr" in syms:
                lines.append("pcm Attach 0x%08X 0x%08X 8192 @%s" % (syms["g_emu_pcm"], syms["g_emu_pcm_wr"],
                                                                    os.path.join(out, "rx_pcm.raw")))
        except Exception:
            pass
    for spec in a.uart_socket:                      # e.g. usart1:3470 -> CPS / tools talk TCP to the emulated UART
        uart, port = spec.split(":")
        lines.append('emulation CreateServerSocketTerminal %d "%s_sock" false' % (int(port), uart))
        lines.append("connector Connect sysbus.%s %s_sock" % (uart, uart))
    if a.elf and not a.no_rfcal and b"safety_tx_block" in open(a.elf, "rb").read():
        # cfw ELFs with the pre-flash TX gate (symbol safety_tx_block; not the lifted stock image): synthetic RF calibration block on top of the NAND image (make_rfcal.py; the factory-formatted
        # image has none, and the cfw refuses TX without one). The stock image runs without it, as before.
        rfcal = os.path.join(HERE, "images", "rfcal_synthetic.pages")
        if not os.path.exists(rfcal):
            subprocess.check_call([sys.executable, os.path.join(HERE, "make_rfcal.py")], cwd=HERE)
        lines.append("nand LoadPages @%s" % rfcal)
    lines += a.extra
    # Boot like hardware: reset vector at 0x0 (flash alias) -> boot stub -> app at 0x0800C000.
    lines.append("sysbus LoadBinary @%s 0x08000000" % (a.bootstub or os.path.join(HERE, "images", "bootstub.bin")))
    # Activation key the app checks at 0x0800A000 (FUN_080215c0): value derived from the chip UID
    # served by models (devinfo). Extracted from an emulated run; see emu/README.md.
    if not cfw:
        lines.append("sysbus LoadBinary @%s 0x0800A000" % activation)
    lines.append("cpu VectorTableOffset 0x0")
    if a.gdb:
        lines.append("machine StartGdbServer %d true" % a.gdb)
    elif a.serve:
        lines.append("include @%s" % os.path.join(HERE, "dump_state.py"))
        lines.append('emulation RunFor "%s"' % fmt_time(a.seconds))
        lines.append('lcd Screenshot @%s' % os.path.join(out, "lcd.ppm"))
    else:
        lines.append("include @%s" % os.path.join(HERE, "dump_state.py"))
        if a.samples:
            for _ in range(a.samples):
                lines.append('emulation RunFor "%s"' % fmt_time(a.seconds / a.samples))
                lines.append('pc_sample "%s"' % os.path.join(out, "pc_samples.txt"))
        else:
            lines.append('emulation RunFor "%s"' % fmt_time(a.seconds))
        shot = 0
        then_cmds = []
        then_file = os.path.join(out, "then_cmds.txt")
        then_out = os.path.join(out, "then_out.txt")
        if os.path.exists(then_out):
            os.remove(then_out)
        if a.then:
            lines.append("include @%s" % os.path.join(HERE, "then_cmd.py"))
        for step in a.then:
            secs, cmd = step.split("|", 1)
            if float(secs) > 0:
                lines.append('emulation RunFor "%s"' % fmt_time(float(secs)))
            if cmd.strip() == "shot":
                shot += 1
                cmd = "lcd Screenshot @%s" % os.path.join(out, "lcd_%02d.ppm" % shot)
            # run through then_cmd.py so the command's output lands in then_out.txt (printed at the end)
            then_cmds.append(cmd)
            lines.append('then_cmd "%s" %d "%s"' % (then_file, len(then_cmds) - 1, then_out))
        open(then_file, "w").write("\n".join(then_cmds) + "\n")
        lines.append('dump_state "%s"' % os.path.join(out, "state.txt"))
        lines.append('lcd Screenshot @%s' % os.path.join(out, "lcd.ppm"))
        if a.save_nand:
            lines.append('nand SaveOverlay @%s' % os.path.join(out, "nand.pages"))
        lines.append("quit")
    resc = os.path.join(out, "run.resc")
    open(resc, "w").write("\n".join(lines) + "\n")

    os.environ["D890_OUT_DIR"] = out                 # Pwm.cs writes tx_<n>.adc16/.json here during RF TX
    for f in os.listdir(out):
        if re.match(r"tx_\d+\.(adc16|json)$|lcd_\d+\.(ppm|png)$", f):   # no stale shots from an earlier run
            os.remove(os.path.join(out, f))
    os.environ["D890_BT_LOG"] = os.path.join(out, "bluetooth.log")
    if os.path.exists(os.environ["D890_BT_LOG"]):
        os.remove(os.environ["D890_BT_LOG"])
    os.environ["D890_BB_LOG"] = os.path.join(out, "baseband.log")
    if os.path.exists(os.environ["D890_BB_LOG"]):
        os.remove(os.environ["D890_BB_LOG"])
    cmd = [RENODE, "--disable-gui", "--plain", resc]
    if a.serve:
        cmd += ["-P", str(a.serve)]
        log = open(os.path.join(out, "console.txt"), "w")
        p = subprocess.Popen(cmd, cwd=HERE, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                             start_new_session=True)
        open(os.path.join(out, "renode.pid"), "w").write(str(p.pid))
        time.sleep(3)
        blog = open(os.path.join(out, "broker.txt"), "w")
        subprocess.Popen([sys.executable, os.path.join(HERE, "ctl.py"), "--port", str(a.serve), "broker"],
                         cwd=HERE, stdout=blog, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                         start_new_session=True)
        print("Renode pid %d, monitor on telnet localhost:%d, out %s" % (p.pid, a.serve, out))
        return 0
    if a.gdb:
        cmd += ["-P", "1234"]
        print("Renode monitor on telnet localhost 1234; GDB on :%d (CPU halted until you continue)" % a.gdb)
        return subprocess.call(cmd)
    cmd += ["-P", "-1"]
    t0 = time.time()
    try:
        p = subprocess.run(cmd, cwd=HERE, capture_output=True, text=True, timeout=a.timeout)
        console = p.stdout + p.stderr
    except subprocess.TimeoutExpired as e:
        console = (e.stdout or b"").decode(errors="replace") if isinstance(e.stdout, bytes) else (e.stdout or "")
        console += "\nHOST TIMEOUT after %ds" % a.timeout
    open(os.path.join(out, "console.txt"), "w").write(console)
    dt = time.time() - t0
    state = open(os.path.join(out, "state.txt")).read() if os.path.exists(os.path.join(out, "state.txt")) else ""
    print("== %s: %.1f virtual s in %.0f host s" % (name, a.seconds, dt))
    print(state.strip() or "(no state: see %s/console.txt)" % out)
    import glob
    for p in sorted(glob.glob(os.path.join(out, "lcd_*.ppm"))):
        subprocess.call([sys.executable, "-I", os.path.join(HERE, "ppm2png.py"), p, p[:-4] + ".png"])
    ppm = os.path.join(out, "lcd.ppm")
    if os.path.exists(ppm):
        subprocess.call([sys.executable, "-I", os.path.join(HERE, "ppm2png.py"), ppm, os.path.join(out, "lcd.png")])
        print("-- screen: %s" % os.path.join(out, "lcd.png"))
    then_out = os.path.join(out, "then_out.txt")
    if a.then and os.path.exists(then_out):
        print("-- --then output (%s):" % then_out)
        print(open(then_out).read().rstrip())
    for u in UARTS:
        f = os.path.join(out, "%s.txt" % u)
        if os.path.exists(f) and os.path.getsize(f):
            print("-- %s (%d bytes): %r" % (u, os.path.getsize(f), open(f, "rb").read(200)))
    if "HOST TIMEOUT" in console or "error" in console.lower() and not state:
        return 3
    return 2 if re.search(r"halted|HardFault|LOCKUP|lift_trap=0x(?!0{8})", state) else 0


NAND_PAGES = None


def is_cfw_elf(path):
    """custom firmware (D890UV_src/cfw) rather than the lifted stock build: has the cfw symbol channels_init"""
    try:
        out = subprocess.check_output(["arm-none-eabi-nm", path], text=True, stderr=subprocess.DEVNULL)
    except Exception:
        return b"channels_init" in open(path, "rb").read()
    return any(l.split()[-1] == "channels_init" for l in out.splitlines() if l.split())


def first_builtin(nand=None):
    """Number of the first cfw built-in channel (Info screen; cfw app/channels.c: NAND codeplug channels + 1) on the
    default NAND image: 1404 on images/extflash.pages (the user's AnyTone codeplug, 1403 channels), len(CHANNELS) + 1
    on images/synth.pages (make_nand.py)."""
    nand = nand or default_nand()
    if os.path.basename(nand) == "extflash.pages":
        return 1404
    sys.path.insert(0, HERE)
    import make_nand
    return len(make_nand.CHANNELS) + 1


def default_nand(stock=False):
    """images/extflash.pages (built from the user's own AnyTone package) if present, else images/synth.pages
    (our own synthetic image, generated by make_nand.py when missing). EMU_NO_ANYTONE=1 in the environment ignores
    extflash.pages, as on a fresh clone of the published repo (test runs: cfw/tools/*_test.py)."""
    ext = os.path.join(HERE, "images", "extflash.pages")
    if os.path.exists(ext) and not os.environ.get("EMU_NO_ANYTONE"):
        return ext
    synth = os.path.join(HERE, "images", "synth.pages")
    if not os.path.exists(synth):
        subprocess.check_call([sys.executable, os.path.join(HERE, "make_nand.py"), "--out", synth])
    if stock:
        print("note: images/extflash.pages not found, stock firmware runs on images/synth.pages "
              "(no ICON fonts/logo, no codeplug); see emu/README.md \"Images\"")
    return synth


def make_platform(out=None):
    """Renode 1.17 hangs on relative PythonPeripheral filenames: write an absolute-path copy (into the run's own
    folder: runs in parallel would overwrite a shared copy, NAND path included)."""
    src = open(os.path.join(HERE, "stm32u575.repl")).read()
    dst = os.path.join(out or os.path.join(HERE, "out"), "stm32u575.abs.repl")
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    src = src.replace('"models/', '"%s/' % os.path.join(HERE, "models"))
    src = src.replace('"images/', '"%s/' % os.path.join(HERE, "images"))
    if NAND_PAGES:
        src = src.replace(os.path.join(HERE, "images", "extflash.pages"), os.path.abspath(NAND_PAGES))
    open(dst, "w").write(src)
    return dst


def stage(path, d):
    dst = os.path.join(d, os.path.basename(path).replace(" ", "_"))
    shutil.copyfile(path, dst)
    return dst


def fmt_time(s):
    return "%d:%02d:%09.6f" % (s // 3600, (s % 3600) // 60, s % 60) if s >= 60 else "%.6f" % s


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Stock V1.05 decodes an MDC1200 packet fed through the ADC1 sample-stream injector (`analog Stream`).

Path: Analog.cs -> DAT_20001d34[0] (ADC1 ch1) -> TIM6 IRQ49 (4.8 kHz) FUN_080d7d70 -> MDC1200 decoder FUN_0813f3d8
-> FUN_0813bf48 (CRC ok) -> FUN_08020b40 copies op/arg/unit to DAT_2000c316 / DAT_2000c2f9 / DAT_2000c310.
Set-up pokes (the K6RRR codeplug has no channel with MDC decode and the NAND image has no squelch calibration):
  knob x6 -> channel A = an analog channel at 449.440 (analog); DAT_200042e2 = 0 (main side = A); channel A +0x1a = 0x40 (decode
  type 4 = MDC1200, see FUN_0811e99c); squelch: rf0 carrier -80 dBm + threshold DAT_2000d762 = 0x60; TIM6 started
  like FUN_080d7c30 -> FUN_08063048(249, 99) (PSC 99, ARR 249 = 4.8 kHz, UIE, IRQ49 enabled); DAT_2001af9b = 4.
Expect op 0x01, arg 0x80, unit 0x1234.   --debug: log the 112 received bits per packet (bits.log in the out dir).
"""
import os, re, subprocess, sys
HERE = os.path.dirname(os.path.abspath(__file__))
EMU = os.path.dirname(HERE)


def main():
    debug = "--debug" in sys.argv
    exact = "--exact" in sys.argv
    name = "stock_mdc"
    out = os.path.join(EMU, "out", name)
    raw = os.path.join(HERE, "mdc1200_1234.raw")
    subprocess.check_call([sys.executable, os.path.join(HERE, "gen_mdc1200.py"), raw, "--unit", "0x1234",
                           "--op", "0x01", "--arg", "0x80"], stdout=subprocess.DEVNULL)
    then = ["0|knob Turn 1"] + ["1|knob Turn 1"] * 5 + [
        "1|sysbus WriteByte 0x200042e2 0", "0|sysbus WriteByte 0x20013A42 0x40",
        "0|rf0 SignalHere -80", "0|sysbus WriteWord 0x2000d762 0x60", "0|sysbus WriteByte 0x2001af9b 4",
        "0|sysbus WriteDoubleWord 0x40001028 99", "0|sysbus WriteDoubleWord 0x4000102C 249",
        "0|sysbus WriteDoubleWord 0x4000100C 1", "0|sysbus WriteDoubleWord 0x40001000 0x81",
        "0|sysbus WriteDoubleWord 0xE000E104 0x20000", "0.5|sysbus ReadByte 0x2001321c"]
    if debug:
        log = os.path.join(out, "bits.log")
        if os.path.exists(log):
            os.remove(log)
        then.append("0|cpu AddHook 0x0813BF48 \"import System; b = self.GetRegister(0).RawValue + "
                    "self.GetRegister(1).RawValue*0x1c0 + 0x98; System.IO.File.AppendAllText('%s', "
                    "str(self.GetRegister(1).RawValue) + ' ' + ''.join([str(self.Bus.ReadDoubleWord(b + 4*i)) "
                    "for i in range(112)]) + chr(10))\"" % log)
    then += ["0|analog Stream @%s 48000" % raw,
             "1.0|sysbus ReadByte 0x2000c316", "0|sysbus ReadByte 0x2000c2f9", "0|sysbus ReadWord 0x2000c310",
             "0|shot", "2|analog StreamStatus", "0|shot"]
    args = [sys.executable, os.path.join(EMU, "run.py"), "--orig", "--seconds", "15", "--name", name, "--timeout", "900"]
    for t in then:
        args += ["--then", t]
    if exact:
        args.append("--exact")
    subprocess.call(args, stdout=subprocess.DEVNULL)
    res = open(os.path.join(out, "then_out.txt")).read()
    vals = re.findall(r"> sysbus Read\w+ (0x\w+)\n(0x\w+)", res)
    got = {a.lower(): int(v, 16) for a, v in vals}
    op, arg, unit = got.get("0x2000c316"), got.get("0x2000c2f9"), got.get("0x2000c310")
    print("squelch A open:", got.get("0x2001321c"))
    print("decoded: op=%s arg=%s unit=%s" % (hex(op) if op is not None else None, hex(arg) if arg is not None else None,
                                             hex(unit) if unit is not None else None))
    if debug and os.path.exists(os.path.join(out, "bits.log")):
        sys.path.insert(0, HERE)
        import gen_mdc1200 as g
        print("expected  " + "".join(map(str, g.packet_bits(1, 0x80, 0x1234)[-112:])))
        print(open(os.path.join(out, "bits.log")).read().rstrip())
    ok = (op, arg, unit) == (0x01, 0x80, 0x1234)
    print("PASS" if ok else "FAIL", "screens:", out)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())

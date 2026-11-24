"""Print each uC/OS-II task's saved stack (code-looking words = return addresses) via ctl.py broker.
TCB table OSTCBPrioTbl at 0x2000a48c (FUN_080bff00); TCB+0 = saved SP."""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ctl import talk
port = int(sys.argv[1]) if len(sys.argv) > 1 else 1235
prios = range(0, 64)
r = talk(port, ["sysbus ReadDoubleWord 0x%08X" % (0x2000a48c + 4 * p) for p in prios])
import re
val = lambda s: int(re.findall(r"0x[0-9A-Fa-f]+", s)[-1], 16)
for p, s in zip(prios, r):
    tcb = val(s)
    if tcb < 0x20000000 or tcb == 1:
        continue
    sp = val(talk(port, ["sysbus ReadDoubleWord 0x%08X" % tcb])[0])
    words = [val(x) for x in talk(port, ["sysbus ReadDoubleWord 0x%08X" % (sp + 4 * i) for i in range(int(sys.argv[2]) if len(sys.argv) > 2 else 64)])]
    code = ["%d:%08x" % (i, w) for i, w in enumerate(words) if 0x0800C000 <= w < 0x081A0000]
    print("prio %2d tcb %08x sp %08x  %s" % (p, tcb, sp, " ".join(code[:40])))

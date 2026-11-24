"""hooktrace.py LOG ADDR[,ADDR..] [--mem A,..] -- monitor commands...
Hook each function entry, log 'addr bytes-of-mem' per hit, run the commands, remove the hooks, print log."""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ctl import talk
args = sys.argv[1:]
log, addrs = os.path.abspath(args[0]), args[1].split(",")
mem = []
i = 2
if args[i] == "--mem":
    mem = args[i + 1].split(","); i += 2
cmds = args[i + 1:] if args[i] == "--" else args[i:]
if os.path.exists(log): os.remove(log)
expr = " + ' ' + ".join(["'%s'" % "@"] + ["hex(self.Bus.ReadByte(%s))" % m for m in mem]) if mem else "''"
hooks = ["cpu AddHook %s \"import System; System.IO.File.AppendAllText('%s', '%s ' + %s + chr(10))\"" % (a, log, a, expr)
         for a in addrs]
talk(1235, hooks + cmds + ["cpu RemoveHooksAt %s" % a for a in addrs])
print(open(log).read() if os.path.exists(log) else "(no hits)")

# Renode monitor command: gpio_trace_start <csv>. Logs every GPIO pin change (time, port, pin, level).
import time as _t
_gpio_fh = [None]
def mc_gpio_trace_start(path):
    m = self.Machine
    fh = open(path, "w")
    fh.write("virtual_us,port,pin,level\n")
    _gpio_fh[0] = fh
    for port in "ABCDEFGHI":
        try:
            dev = m["sysbus.gpioPort" + port]
        except Exception:
            continue
        for pin in range(16):
            def cb(state, port=port, pin=pin):
                us = m.ElapsedVirtualTime.TimeElapsed.TotalMicroseconds
                fh.write("%d,%s,%d,%d\n" % (us, port, pin, 1 if state else 0))
            try:
                dev.Connections[pin].AddStateChangedHook(cb)
            except Exception as e:
                print("gpio hook failed %s%d: %s" % (port, pin, e))

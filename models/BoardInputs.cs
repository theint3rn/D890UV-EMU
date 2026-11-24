// Board-level input levels for the D890UV: power switch, keys (active-low, pulled up), detect lines.
// Re-drives every configured pin each millisecond of virtual time, so levels survive the
// firmware's power-on software reset (which resets the GPIO ports).
// PA0 is the momentary power key (high = pressed): held for powerKeyMs after start, then released.
// Held for good, the firmware's power-off check (FUN_080d6954 / FUN_08057db0) shuts down and resets.
//
// Monitor API:
//   board Set "A8" false        hold PA8 low (key pressed) until changed
//   board Press "A8" 150        press PA8 for 150 ms of virtual time, then release
//   board Levels                show all driven pins
//   board PowerOff / board PowerOn   volume knob click off / on (on: after machine Reset)
using System;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890BoardInputs : IDoubleWordPeripheral
    {
        public D890BoardInputs(IMachine machine, string levels = "", int powerKeyMs = 1000)
        {
            this.machine = machine;
            PowerKeyMs = powerKeyMs;
            initial = levels;
            Reset();
            machine.ClockSource.AddClockEntry(new ClockEntry(1, 1000, Tick, this, "board_inputs"));
        }

        public long Size => 0x10;
        public int PowerKeyMs { get; set; }   // 0 = never release PA0

        public void Reset()
        {
            pins.Clear();
            releaseAt.Clear();
            foreach(var item in initial.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = item.Split('=');
                pins[kv[0].Trim().ToUpper()] = kv.Length < 2 || kv[1].Trim() != "0";
            }
            ms = 0;
            foreach(var kv in held) { pins[kv.Key] = kv.Value; }   // a level set with Set (key held) survives a soft reset
            if(powerReleased) { pins["A0"] = false; }   // key stays released across the soft reset
        }

        public uint ReadDoubleWord(long offset) { return 0; }
        public void WriteDoubleWord(long offset, uint value) { }

        public void Set(string pin, bool level)
        {
            held[pin.ToUpper()] = level;
            pins[pin.ToUpper()] = level;
            releaseAt.Remove(pin.ToUpper());
            Drive(pin.ToUpper(), level);
        }

        public void Press(string pin, int milliseconds)
        {
            var p = pin.ToUpper();
            bool idle;
            if(!pins.TryGetValue(p, out idle)) { idle = true; }
            idleLevel[p] = idle;
            pins[p] = !idle;
            releaseAt[p] = ms + milliseconds;
            Drive(p, !idle);
        }

        // Volume/power knob clicked on again (after `machine Reset`): power line high for PowerKeyMs, as at start.
        public void PowerOn()
        {
            powerReleased = false; uptime = 0;
            pins["A0"] = true; releaseAt.Remove("A0");
            Drive("A0", true);
        }

        // Knob clicked off: PA0 high = power-off request (FUN_080d6954 saves, drops the PC3 latch, resets).
        public void PowerOff()
        {
            powerReleased = true;
            pins["A0"] = true; releaseAt.Remove("A0");
            Drive("A0", true);
        }

        public string Levels()
        {
            return string.Join(" ", pins.Select(kv => kv.Key + "=" + (kv.Value ? 1 : 0)));
        }

        private void Tick()
        {
            ms++;
            if(!powerReleased && PowerKeyMs > 0 && ++uptime >= PowerKeyMs)
            {
                powerReleased = true;
                bool on;
                if(pins.TryGetValue("A0", out on) && on) { pins["A0"] = false; releaseAt.Remove("A0"); }
            }
            foreach(var p in releaseAt.Where(kv => kv.Value <= ms).Select(kv => kv.Key).ToList())
            {
                pins[p] = idleLevel[p];
                releaseAt.Remove(p);
            }
            foreach(var kv in pins)
            {
                Drive(kv.Key, kv.Value);
            }
        }

        private void Drive(string pin, bool level)
        {
            IGPIOReceiver port;
            var name = "gpioPort" + pin.Substring(0, 1);
            if(!ports.TryGetValue(name, out port))
            {
                IPeripheral per;
                if(!machine.TryGetByName("sysbus." + name, out per)) { return; }
                port = per as IGPIOReceiver;
                ports[name] = port;
            }
            port?.OnGPIO(int.Parse(pin.Substring(1)), level);
        }

        private readonly IMachine machine;
        private readonly string initial;
        private readonly Dictionary<string, bool> pins = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> held = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> idleLevel = new Dictionary<string, bool>();
        private readonly Dictionary<string, long> releaseAt = new Dictionary<string, long>();
        private readonly Dictionary<string, IGPIOReceiver> ports = new Dictionary<string, IGPIOReceiver>();
        private long ms;
        private long uptime;
        private bool powerReleased;
    }
}

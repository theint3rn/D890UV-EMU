// D890UV front panel: 8080-style parallel TFT controller (MIPI DCS: 2A/2B/2C, RGB565)
// and the 3-column key matrix. Both share the MCU data lines PE7..PE14.
//
// GPIO inputs:  0..7 = PE7..PE14 (LCD D0..D7 / key rows), 8 = PD15 WR#, 9 = PD14 CS#, 10 = PD12 D/C# (1 = data)
// GPIO outputs: Col0 -> PC4, Col1 -> PB1, Col2 -> PB2 (active-low key columns)
// Key codes are the values FUN_08101260 returns (row = PE pin driven low, column = input read low).
//
// Monitor API:
//   lcd Screenshot @file.ppm     write the visible framebuffer (PPM, convert with emu/ppm2png.py)
//   lcd Press 5 150              hold key code 5 for 150 ms of virtual time
//   lcd Hold 5 true|false        press/release a key until changed
//   lcd Info                     controller state (window, writes, MADCTL)
//   lcd WatchPixel 71 45 @f.txt   log every write to that pixel (time, x,y, colour, PC, LR) - find who draws it
//   lcd WatchRect 60 40 85 50 @f.txt   same for a rectangle
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890LcdKeypad : IDoubleWordPeripheral, IGPIOReceiver
    {
        public D890LcdKeypad(IMachine machine, int width = 160, int height = 128)
        {
            this.machine = machine;
            W = width; H = height;
            Col0 = new GPIO(); Col1 = new GPIO(); Col2 = new GPIO();
            fb = new ushort[MaxDim * MaxDim];
            // (row PE pin index 0..7 = PE7..PE14, column 0..2) for each key code
            keys = new Dictionary<int, Tuple<int, int>>
            {
                {2, T(0,0)}, {13, T(0,1)}, {19, T(0,2)},
                {3, T(1,0)}, {15, T(1,1)}, {20, T(1,2)},
                {4, T(2,0)}, {16, T(2,1)},
                {5, T(3,0)}, {8, T(3,1)},
                {6, T(4,0)}, {9, T(4,1)},
                {7, T(5,0)}, {10, T(5,1)},
                {1, T(6,0)}, {12, T(6,1)},
                {11, T(7,0)}, {14, T(7,1)},
            };
            machine.ClockSource.AddClockEntry(new ClockEntry(1, 1000, Tick, this, "lcd_keypad"));
            Reset();
        }

        public GPIO Col0 { get; private set; }
        public GPIO Col1 { get; private set; }
        public GPIO Col2 { get; private set; }
        public long Size => 0x10;
        public int W { get; private set; }
        public int H { get; private set; }
        public long PixelWrites { get; private set; }

        public void Reset()
        {
            cmd = -1; param = 0; x0 = 0; x1 = MaxDim - 1; y0 = 0; y1 = MaxDim - 1; x = 0; y = 0;
            half = -1; madctl = 0;
            UpdateColumns();
        }

        public uint ReadDoubleWord(long offset) { return offset == 0 ? (uint)PixelWrites : 0; }
        public void WriteDoubleWord(long offset, uint value) { }

        public void OnGPIO(int number, bool value)
        {
            if(number < 8)
            {
                if(value) { bus |= (byte)(1 << number); } else { bus &= (byte)~(1 << number); }
                UpdateColumns();
                return;
            }
            switch(number)
            {
            case 8:
                if(value && !wr && !cs) { Strobe(); }
                wr = value; break;
            case 9: cs = value; break;
            case 10: dc = value; break;
            }
        }

        // ---- keypad ----
        public void Hold(int code, bool pressed)
        {
            if(pressed) { held.Add(code); } else { held.Remove(code); }
            UpdateColumns();
        }

        public void Press(int code, int milliseconds)
        {
            Hold(code, true);
            releaseAt[code] = ms + milliseconds;
        }

        private void Tick()
        {
            ms++;
            var done = new List<int>();
            foreach(var kv in releaseAt) { if(kv.Value <= ms) { done.Add(kv.Key); } }
            foreach(var k in done) { releaseAt.Remove(k); Hold(k, false); }
            UpdateColumns();   // re-drive after GPIO port resets
        }

        private void UpdateColumns()
        {
            bool c0 = true, c1 = true, c2 = true;    // pulled up
            foreach(var code in held)
            {
                Tuple<int, int> rc;
                if(!keys.TryGetValue(code, out rc)) { continue; }
                if((bus & (1 << rc.Item1)) != 0) { continue; }  // row not driven low
                if(rc.Item2 == 0) { c0 = false; } else if(rc.Item2 == 1) { c1 = false; } else { c2 = false; }
            }
            Col0.Set(c0); Col1.Set(c1); Col2.Set(c2);
            // Also drive the port inputs directly: GPIO.Set does not re-notify an unchanged level,
            // and the firmware's power-on reset clears the ports.
            Drive("gpioPortC", 4, c0); Drive("gpioPortB", 1, c1); Drive("gpioPortB", 2, c2);
        }

        // ---- LCD ----
        private void Strobe()
        {
            if(!dc) { cmd = bus; param = 0; half = -1; if(cmd == 0x2C) { x = x0; y = y0; } return; }
            switch(cmd)
            {
            case 0x2A: SetWindow(ref x0, ref x1); break;
            case 0x2B: SetWindow(ref y0, ref y1); break;
            case 0x36: madctl = bus; break;
            case 0x2C:
            case 0x3C:
                if(half < 0) { half = bus; break; }
                Pixel((ushort)((half << 8) | bus));
                half = -1;
                break;
            }
            param++;
        }

        private void SetWindow(ref int start, ref int end)
        {
            switch(param)
            {
            case 0: start = (bus << 8) | (start & 0xFF); break;
            case 1: start = (start & 0xFF00) | bus; break;
            case 2: end = (bus << 8) | (end & 0xFF); break;
            case 3: end = (end & 0xFF00) | bus; break;
            }
        }

        public void WatchPixel(int px, int py, string path) { WatchRect(px, py, px, py, path); }
        public void WatchRect(int xa, int ya, int xb, int yb, string path) { watchX = xa; watchY = ya; watchX1 = xb; watchY1 = yb; watchPath = path; }

        private void Pixel(ushort c)
        {
            if(watchPath != null && x >= watchX && x <= watchX1 && y >= watchY && y <= watchY1)
            {
                ulong pc = 0, lr = 0;
                foreach(var cpu in machine.SystemBus.GetCPUs())
                {
                    pc = cpu.PC.RawValue;
                    var rc = cpu as Antmicro.Renode.Peripherals.CPU.ICPUWithRegisters;
                    if(rc != null) { lr = rc.GetRegister(14).RawValue; }
                    break;
                }
                File.AppendAllText(watchPath, string.Format("{0} {4},{5} colour 0x{1:X4} pc 0x{2:X8} lr 0x{3:X8}\n",
                    machine.ElapsedVirtualTime.TimeElapsed, c, pc, lr, x, y));
            }
            if(x >= 0 && x < MaxDim && y >= 0 && y < MaxDim) { fb[y * MaxDim + x] = c; }
            PixelWrites++;
            if(++x > x1) { x = x0; if(++y > y1) { y = y0; } }
        }

        public void Screenshot(string path)
        {
            // Controller memory: column = 2A axis, row = 2B axis. Image is W x H in that order.
            using(var f = File.Create(path))
            {
                var hdr = Encoding.ASCII.GetBytes(string.Format("P6\n{0} {1}\n255\n", W, H));
                f.Write(hdr, 0, hdr.Length);
                var px = new byte[3];
                for(int r = 0; r < H; r++)
                {
                    for(int c = 0; c < W; c++)
                    {
                        var v = fb[r * MaxDim + c];
                        px[0] = (byte)(((v >> 11) & 0x1F) * 255 / 31);
                        px[1] = (byte)(((v >> 5) & 0x3F) * 255 / 63);
                        px[2] = (byte)((v & 0x1F) * 255 / 31);
                        f.Write(px, 0, 3);
                    }
                }
            }
        }

        private int watchX = -1, watchY = -1, watchX1 = -1, watchY1 = -1;
        private string watchPath;

        public string Info()
        {
            return string.Format("window x {0}-{1} y {2}-{3} cursor {4},{5} madctl 0x{6:X2} pixels {7} held [{8}]",
                x0, x1, y0, y1, x, y, madctl, PixelWrites, string.Join(",", held));
        }

        private static Tuple<int, int> T(int r, int c) { return Tuple.Create(r, c); }

        private void Drive(string name, int pin, bool level)
        {
            IGPIOReceiver port;
            if(!ports.TryGetValue(name, out port))
            {
                IPeripheral per;
                if(!machine.TryGetByName("sysbus." + name, out per)) { return; }
                port = per as IGPIOReceiver;
                ports[name] = port;
            }
            port?.OnGPIO(pin, level);
        }
        private readonly Dictionary<string, IGPIOReceiver> ports = new Dictionary<string, IGPIOReceiver>();

        private const int MaxDim = 512;
        private readonly IMachine machine;
        private readonly ushort[] fb;
        private readonly Dictionary<int, Tuple<int, int>> keys;
        private readonly HashSet<int> held = new HashSet<int>();
        private readonly Dictionary<int, long> releaseAt = new Dictionary<int, long>();
        private byte bus;
        private bool wr = true, cs = true, dc = true;
        private int cmd, param, half, madctl, x0, x1, y0, y1, x, y;
        private long ms;
    }
}

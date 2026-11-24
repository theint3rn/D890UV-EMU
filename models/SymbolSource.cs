// Emulator-only symbol source for the custom firmware's MCU-DSP receive path (cfw `make EMU=1`).
// Not a chip on the radio: a FIFO in free address space (0x5FFF0500) that hands demodulated symbols
// (DMR/P25/NXDN/YSF dibits 0..3, D-STAR bits 0/1, one per byte in the file) to the firmware at the
// symbol rate, paced by virtual time. Lets proto/* decoders run inside the firmware against a file.
//
// Registers (32-bit):
//   0x00 ID      'SYMS' (0x534D5953)
//   0x04 AVAIL   symbols due now (time * rate - consumed), capped at what is left; 0 when idle
//   0x08 DATA    pop one symbol (0xFFFFFFFF if none due)
//   0x0C RATE    symbols/s of the loaded stream
//   0x10 COUNT   symbols delivered since Load
//   0x14 BULK    pop up to 4 symbols, packed little-endian bytes; AVAIL tells how many are valid
// Monitor:
//   syms Load @file.dibits 4800     start a stream now (binary, one symbol per byte; a .txt file may hold
//                                   digits 0-3 with any separators)
//   syms Loop true                  restart at the end
//   syms Stop / syms Status
//   syms Gate true                  only deliver while rf0 sees a carrier (rf0 Signal ...); default false
using System;
using System.IO;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890SymbolSource : IDoubleWordPeripheral
    {
        public D890SymbolSource(IMachine machine)
        {
            this.machine = machine;
            Reset();
        }

        public long Size => 0x20;
        public bool Loop { get; set; }
        public bool Gate { get; set; }

        public void Reset() { }

        public void Load(string path, int rate)
        {
            byte[] raw = File.ReadAllBytes(path);
            var list = new List<byte>();
            if(path.EndsWith(".txt"))
            {
                foreach(var b in raw) { if(b >= (byte)'0' && b <= (byte)'3') { list.Add((byte)(b - '0')); } }
            }
            else
            {
                list.AddRange(raw);
            }
            data = list.ToArray();
            this.rate = Math.Max(1, rate);
            pos = 0;
            delivered = 0;
            t0 = Now();
            running = data.Length > 0;
        }

        public void Stop() { running = false; }

        public string Status()
        {
            return string.Format("{0}: {1} symbols at {2}/s, position {3}, delivered {4}, due {5}, loop {6}, gate {7}",
                running ? "running" : "idle", data.Length, rate, pos, delivered, Avail(), Loop, Gate);
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
            case 0x00: return 0x534D5953;
            case 0x04: return (uint)Avail();
            case 0x08: return Avail() > 0 ? Pop() : 0xFFFFFFFF;
            case 0x0C: return (uint)rate;
            case 0x10: return (uint)delivered;
            case 0x14:
                {
                    uint v = 0;
                    int n = (int)Math.Min(4, Avail());
                    for(int i = 0; i < n; i++) { v |= Pop() << (8 * i); }
                    return v;
                }
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value) { }

        private long Avail()
        {
            if(!running) { return 0; }
            if(Gate && !RfCarrier()) { t0 = Now() - (double)delivered / rate; return 0; }   // hold the stream
            long due = (long)((Now() - t0) * rate) - delivered;
            long left = Loop ? long.MaxValue : data.Length - pos;
            if(left <= 0) { running = false; return 0; }
            return Math.Max(0, Math.Min(due, left));
        }

        private uint Pop()
        {
            if(pos >= data.Length) { pos = 0; }
            uint s = data[pos++];
            delivered++;
            if(pos >= data.Length && !Loop) { running = false; }
            return s;
        }

        private bool RfCarrier()
        {
            IPeripheral p;
            if(machine.TryGetByName("sysbus.rf0", out p)) { var rf = p as D890RfTransceiver; return rf != null && rf.Carrier; }
            return false;
        }

        private double Now() { return machine.ElapsedVirtualTime.TimeElapsed.TotalSeconds; }

        private readonly IMachine machine;
        private byte[] data = new byte[0];
        private int rate = 4800, pos;
        private long delivered;
        private double t0;
        private bool running;
    }
}

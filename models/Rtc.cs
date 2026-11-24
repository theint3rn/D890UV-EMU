// STM32U5 RTC @0x46007800 for the D890UV: calendar on virtual time + wakeup timer interrupt (IRQ 2).
// The firmware's RTC IRQ handler (IRQ2_Handler 0x080da7ec) runs once per wakeup (1 s): it reads the time
// for the clock display and counts down many UI/standby timers. Init mode and shadow sync are immediate.
// Registers: TR 00, DR 04, SSR 08, ICSR 0C, PRER 10, WUTR 14, CR 18, SR 50, MISR 54, SCR 5C.
// Monitor: rtc SetTime "2026-03-14 15:09:26"
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890Rtc : IDoubleWordPeripheral
    {
        public D890Rtc(IMachine machine)
        {
            this.machine = machine;
            IRQ = new GPIO();
            machine.ClockSource.AddClockEntry(new ClockEntry(1, 1000, Tick, this, "rtc"));
            Reset();
        }

        public GPIO IRQ { get; }
        public long Size => 0x400;

        public void Reset()
        {
            regs.Clear();
            regs[0x10] = 0x007F00FF;
            // The calendar is battery-backed: it keeps running across resets/power-off (set once here).
            if(!started) { baseTime = new DateTime(2026, 1, 1, 12, 0, 0); baseVt = 0; started = true; }
            wutCount = 0; sr = 0;
            IRQ.Unset();
        }

        public void SetTime(string s) { baseTime = DateTime.Parse(s); baseVt = Now(); }

        public uint ReadDoubleWord(long offset)
        {
            var t = Current();
            switch(offset)
            {
            case 0x00: return Bcd(t.Second) | (Bcd(t.Minute) << 8) | (Bcd(t.Hour) << 16);
            case 0x04: return Bcd(t.Day) | (Bcd(t.Month) << 8) | ((uint)(t.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)t.DayOfWeek) << 13) | (Bcd(t.Year % 100) << 16);
            case 0x08: return (uint)(255 - t.Millisecond * 256 / 1000) & 0xFF;
            case 0x0C:
                {
                    uint v = Get(0x0C) & (1u << 7);
                    if((v & (1u << 7)) != 0) { v |= 1u << 6; }         // INITF follows INIT
                    return v | (1u << 5) | (1u << 4) | (1u << 2);        // RSF INITS WUTWF
                }
            case 0x50: return sr;
            case 0x54: return sr & ((Get(0x18) >> 12) & 0x7);           // MISR: flags with IE set
            default: return Get(offset);
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
            case 0x00:
            case 0x04:
                {
                    var t = Current();
                    uint tr = offset == 0x00 ? value : (Bcd(t.Second) | (Bcd(t.Minute) << 8) | (Bcd(t.Hour) << 16));
                    uint dr = offset == 0x04 ? value : (Bcd(t.Day) | (Bcd(t.Month) << 8) | (Bcd(t.Year % 100) << 16));
                    try
                    {
                        baseTime = new DateTime(2000 + (int)Un(dr >> 16), Math.Max(1, (int)Un((dr >> 8) & 0x1F)), Math.Max(1, (int)Un(dr & 0x3F)),
                                                (int)Un((tr >> 16) & 0x3F), (int)Un((tr >> 8) & 0x7F), (int)Un(tr & 0x7F));
                    }
                    catch(ArgumentOutOfRangeException) { }
                    baseVt = Now();
                    break;
                }
            case 0x18:
                if((value & (1u << 10)) != 0 && (Get(0x18) & (1u << 10)) == 0) { wutCount = 0; }
                regs[offset] = value;
                break;
            case 0x5C:                                   // SCR: write 1 to clear flags
                sr &= ~(value & 0x7F);
                Update();
                break;
            default:
                regs[offset] = value;
                break;
            }
        }

        private void Tick()
        {
            ms++;
            uint cr = Get(0x18);
            if((cr & (1u << 10)) == 0) { return; }           // WUTE
            uint wucksel = cr & 7;
            double period = wucksel >= 4 ? ((Get(0x14) & 0xFFFF) + 1 + (wucksel >= 6 ? 65536 : 0))
                                         : ((Get(0x14) & 0xFFFF) + 1) * (16 >> (int)wucksel) / 32768.0;
            wutCount += 0.001;
            if(wutCount + 1e-9 >= period)
            {
                wutCount -= period;
                sr |= 1u << 2;                               // WUTF
                Update();
            }
        }

        private void Update()
        {
            bool wut = (sr & 4) != 0 && (Get(0x18) & (1u << 14)) != 0;
            bool alra = (sr & 1) != 0 && (Get(0x18) & (1u << 12)) != 0;
            if(wut || alra) { IRQ.Set(); } else { IRQ.Unset(); }
        }

        private DateTime Current() { return baseTime.AddSeconds(Now() - baseVt); }
        // Virtual time = 1 ms ticks counted by Tick (touching the time source from bus accesses can deadlock).
        private double Now() { return ms / 1000.0; }
        private uint Get(long o) { uint v; return regs.TryGetValue(o, out v) ? v : 0; }
        private static uint Bcd(int v) { return (uint)(((v / 10) << 4) | (v % 10)); }
        private static uint Un(uint v) { v &= 0xFF; return (v >> 4) * 10 + (v & 0xF); }

        private readonly IMachine machine;
        private readonly Dictionary<long, uint> regs = new Dictionary<long, uint>();
        private DateTime baseTime;
        private double baseVt;
        private double wutCount;
        private long ms;
        private uint sr;
        private bool started;
    }
}

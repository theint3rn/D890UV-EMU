// STM32U5 TIM4 @0x40000800 as the MCU TX-audio PWM (PB9 = TIM4_CH4) with capture. Replaces Renode's STM32_Timer
// for timer4 (stm32u575.repl); neither the stock firmware nor the cfw uses TIM4 interrupts.
// Stock V1.05 (cfw/docs/AUDIO_PATHS.md): PWM ch4 on PB9, CCR4 written per sample from the TIM5 IRQ (MDC-style data,
// 9600 samples/s, ARR 255) or by GPDMA1 ch0 on the CC4 request (APRS AFSK, ARR 1561 = 76.8 kHz).
// cfw (cfw/drivers/dac.c): ARR 2499 (48 kHz), CCR4 reloaded every period by GPDMA1 ch1 on the update request (UDE).
// Model: register file of a general-purpose timer. While CR1.CEN is set a clock entry at
// TimerClockHz / ((PSC+1)(ARR+1)) runs one PWM period: with DIER.UDE or DIER.CC4DE the GPDMA1 channel whose CxDAR is
// CCR4 (0x40000840) delivers the next halfword (ring from CxSAR/CxBR1 or its linked-list item, BNDT kept = bytes left,
// HTF/TCF + GPDMA IRQ 29+n when enabled); then, if CCER.CC4E, the duty is recorded as a 12-bit sample
// CCR4 * 4096 / (ARR+1) (= the RC-filtered PWM level). Rates above 200 kHz (stock idle ARR 255 = 469 kHz) record one
// sample per CCR4 write instead (software-paced playback).
// Capture: one uint16 LE per period, .adc16 = what `analog Stream` plays into another radio. Automatic while rf0
// transmits: $D890_OUT_DIR/tx_<n>.adc16 + .json. Monitor (device "pwm"): pwm Capture @f.adc16 / pwm CaptureStop /
// pwm Status / pwm AutoCapture false.
using System;
using System.IO;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890Pwm : IDoubleWordPeripheral, IWordPeripheral
    {
        public D890Pwm(IMachine machine)
        {
            this.machine = machine;
            machine.ClockSource.AddClockEntry(new ClockEntry(1, 1000, Tick, this, "pwm"));
        }

        public long Size => 0x400;
        public double TimerClockHz { get; set; } = 120000000;
        public bool AutoCapture { get; set; } = true;
        public uint Ccr4 => Reg(0x40);
        public double Rate => rate;

        public void Reset() { regs.Clear(); }

        public uint ReadDoubleWord(long offset)
        {
            if(offset == 0x24) { return cnt; }
            return Reg(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
            case 0x10: regs[0x10] = Reg(0x10) & value; break;        // SR: write 0 to clear
            case 0x14: regs[0x14] = 0; break;                          // EGR: self-clearing
            case 0x40:
                regs[0x40] = value;
                if(cap != null && rate > SoftRate && (Reg(0x20) & 0x1000) != 0) { Record(); }   // software-paced
                break;
            default: regs[offset] = value; break;
            }
        }

        public ushort ReadWord(long offset) { return (ushort)ReadDoubleWord(offset & ~3L); }
        public void WriteWord(long offset, ushort value) { WriteDoubleWord(offset & ~3L, value); }

        public void Capture(string path) { Open(path, false); }
        public void CaptureStop() { Close(); }
        public void Stop() { Close(); }
        public string Status()
        {
            return string.Format("cen {0} psc {1} arr {2} rate {3:F1} cc4e {4} ccr4 {5} ude {6} cc4de {7} dma ch {8} ring 0x{9:X8} {10}; " +
                "capture {11} ({12} samples), rf tx {13}, captures {14}",
                (Reg(0) & 1) != 0, Reg(0x28) & 0xFFFF, Reg(0x2C), rate, (Reg(0x20) & 0x1000) != 0, Reg(0x40),
                (Reg(0x0C) & 0x100) != 0, (Reg(0x0C) & 0x1000) != 0, dmaCh, ringAddr, ringLen, capFile ?? "off",
                capCount, RfTx(), txIndex);
        }

        private const double SoftRate = 200000;
        private uint Reg(long o) { uint v; return regs.TryGetValue(o, out v) ? v : 0; }

        private void Tick()
        {
            double r = 0;
            if((Reg(0) & 1) != 0)
            {
                double psc = (Reg(0x28) & 0xFFFF) + 1.0, arr = Reg(0x2C) + 1.0;
                r = TimerClockHz / (psc * arr);
            }
            if(Math.Abs(r - rate) > 0.01)
            {
                if(rate > 0 && rate <= SoftRate) { machine.ClockSource.TryRemoveClockEntry(Period); }
                rate = r;
                dmaCh = -1;
                if(r > 0 && r <= SoftRate) { machine.ClockSource.AddClockEntry(new ClockEntry(1, (ulong)Math.Round(r), Period, this, "pwm-period")); }
            }
            if(cap != null)
            {
                if(rate > 0 && rate <= SoftRate) { capRate = rate; }
                IPeripheral dp;
                if(machine.TryGetByName("sysbus.dac1", out dp)) { var d = dp as D890Dac; if(d != null && apc.Count < 8 && !apc.Contains(d.Dor1)) { apc.Add(d.Dor1); } }
            }
            if(AutoCapture)
            {
                bool tx = RfTx();
                if(tx && !txOn) { txOn = true; txIndex++; StartAuto(); }
                else if(!tx && txOn) { txOn = false; if(autoFile) { Close(); } }
            }
        }

        private void Period()
        {
            cnt = 0;
            if((Reg(0x0C) & 0x1100) != 0) { DmaFetch(); }
            regs[0x10] = Reg(0x10) | 1u | 0x10u;                      // UIF, CC4IF
            if((Reg(0x20) & 0x1000) != 0 && cap != null) { Record(); }
        }

        private void Record()
        {
            double arr = Reg(0x2C) + 1.0;
            double v = Math.Min(Reg(0x40), arr) * 4096.0 / arr;
            cap.Write((ushort)Math.Min(4095.0, Math.Round(v)));
            capCount++;
        }

        private void DmaFetch()
        {
            var bus = machine.SystemBus;
            if(dmaCh < 0) { FindDma(); if(dmaCh < 0) { return; } }
            ulong gb = 0x40020000UL + 0x50 + 0x80 * (ulong)dmaCh;
            if((bus.ReadDoubleWord(gb + 0x14) & 1) == 0 || bus.ReadDoubleWord(gb + 0x50) != 0x40000840) { dmaCh = -1; return; }
            regs[0x40] = bus.ReadWord(ringAddr + (ulong)(2 * ringPos));
            ringPos++;
            if(ringPos >= ringLen) { ringPos = 0; }
            uint br1 = bus.ReadDoubleWord(gb + 0x48);
            bus.WriteDoubleWord(gb + 0x48, (br1 & 0xFFFF0000u) | (uint)(2 * (ringLen - ringPos)));
            uint sr = bus.ReadDoubleWord(gb + 0x10);
            uint fcr = bus.ReadDoubleWord(gb + 0x0C);
            if(fcr != 0) { sr &= ~(fcr & 0x7F00); bus.WriteDoubleWord(gb + 0x0C, 0); }
            if(ringPos == ringLen / 2) { sr |= 1u << 9; }
            if(ringPos == 0) { sr |= 1u << 8; }
            bus.WriteDoubleWord(gb + 0x10, sr);
            bool pending = (sr & bus.ReadDoubleWord(gb + 0x14) & 0x7F00) != 0;
            if(nvic == null) { IPeripheral p; if(machine.TryGetByName("sysbus.nvic", out p)) { nvic = p as IGPIOReceiver; } }
            if(nvic != null && pending != irq) { nvic.OnGPIO(29 + dmaCh, pending); irq = pending; }
        }

        private void FindDma()
        {
            var bus = machine.SystemBus;
            for(int ch = 0; ch < 16; ch++)
            {
                ulong gb = 0x40020000UL + 0x50 + 0x80 * (ulong)ch;
                if(bus.ReadDoubleWord(gb + 0x50) != 0x40000840 || (bus.ReadDoubleWord(gb + 0x14) & 1) == 0) { continue; }
                uint sar = bus.ReadDoubleWord(gb + 0x4C);
                uint bndt = bus.ReadDoubleWord(gb + 0x48) & 0xFFFF;
                uint llr = bus.ReadDoubleWord(gb + 0x7C);
                if((llr & 0xFFFC) != 0)
                {
                    ulong p = (bus.ReadDoubleWord(gb) & 0xFFFF0000u) | (llr & 0xFFFC);
                    if((llr & (1u << 31)) != 0) { p += 4; }
                    if((llr & (1u << 30)) != 0) { p += 4; }
                    if((llr & (1u << 29)) != 0) { bndt = bus.ReadDoubleWord(p) & 0xFFFF; p += 4; }
                    if((llr & (1u << 28)) != 0) { sar = bus.ReadDoubleWord(p); }
                }
                dmaCh = ch; ringAddr = sar; ringLen = Math.Max(1, (int)(bndt / 2)); ringPos = 0;
                return;
            }
        }

        private bool RfTx()
        {
            IPeripheral p;
            if(!machine.TryGetByName("sysbus.rf0", out p)) { return false; }
            var rf = p as D890RfTransceiver;
            return rf != null && rf.Transmitting;
        }

        private void StartAuto()
        {
            string dir = Environment.GetEnvironmentVariable("D890_OUT_DIR");
            if(string.IsNullOrEmpty(dir)) { return; }
            Open(Path.Combine(dir, string.Format("tx_{0}.adc16", txIndex)), true);
        }

        private void Open(string path, bool auto)
        {
            Close();
            capFile = path; autoFile = auto; capCount = 0; capStart = Now(); capRate = 0;
            cap = new BinaryWriter(File.Open(path, FileMode.Create));
            IPeripheral p;
            if(machine.TryGetByName("sysbus.rf0", out p)) { var rf = p as D890RfTransceiver; if(rf != null) { capHz = rf.TunedHz; } }
            apc.Clear();
        }

        private void Close()
        {
            if(cap == null) { return; }
            cap.Close();
            cap = null;
            string apcs = string.Join(", ", apc.ConvertAll(v => "\"0x" + v.ToString("X3") + "\""));
            string json = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{{\"file\": \"{0}\", \"samples\": {1}, \"rate\": {2:F1}, \"duration_s\": {3:F4}, \"start_s\": {4:F4}, " +
                "\"rf_tx_reg_hz\": {5}, \"source\": \"TIM4_CH4 PWM PB9\", \"apc_dac_ch1_levels\": [{6}], \"auto\": {7}}}\n",
                Path.GetFileName(capFile), capCount, capRate, Now() - capStart, capStart, capHz, apcs,
                autoFile ? "true" : "false");
            File.WriteAllText(Path.ChangeExtension(capFile, ".json"), json);
            capFile = null;
        }

        private double Now() { return machine.ElapsedVirtualTime.TimeElapsed.TotalSeconds; }

        private readonly IMachine machine;
        private readonly Dictionary<long, uint> regs = new Dictionary<long, uint>();
        private readonly List<uint> apc = new List<uint>();
        private double rate, capStart, capRate;
        private int dmaCh = -1, ringLen, ringPos, txIndex;
        private ulong ringAddr;
        private uint cnt;
        private bool irq;
        private IGPIOReceiver nvic;
        private BinaryWriter cap;
        private string capFile;
        private bool autoFile, txOn;
        private long capCount, capHz;
    }
}

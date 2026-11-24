// STM32U5 DAC1 (0x46021800) with TX baseband capture.
// Replaces the plain register store. Stock V1.05 writes DAC1 only as static levels (cfw/docs/AUDIO_PATHS.md):
// ch1 PA4 = TX power control (APC, IRQ17_Handler on the baseband TX edge; 0x0FF0 with the emulator's erased
// calibration block), ch2 PA5 = UHF RX front-end tune (FUN_0811c8f0). The MCU TX audio is TIM4_CH4 PWM on PB9
// (models/Pwm.cs); the cfw no longer streams through the DAC. Timer/DMA streaming is kept for experiments.
//
// Registers: CR 0x00 (EN1 b0, TEN1 b1, TSEL1 b5:2, DMAEN1 b12; channel 2 = +16), SWTRGR 0x04, DHR12R1 0x08,
// DHR12L1 0x0C, DHR8R1 0x10, DHR12R2 0x14, DHR12L2 0x18, DHR8R2 0x1C, DHR12RD 0x20, DHR12LD 0x24, DHR8RD 0x28,
// DOR1 0x2C, DOR2 0x30, SR 0x34 (DACxRDY b11/b27 read as set); everything else is plain storage.
// Output: without trigger DOR = DHR at once; with TENx, DOR loads DHR on the trigger (SWTRGR or a timer TRGO).
// Timer trigger: TSELx 1 TIM1, 2 TIM2, 3 TIM4, 4 TIM5, 5 TIM6, 6 TIM7, 7 TIM8, 8 TIM15 (TRGO; override TriggerTimer1/2).
// Rate = TimerClockHz / ((PSC+1)(ARR+1)) (the ideal rate; Renode's own timer runs ARR, not ARR+1, counts).
// DMA (DMAENx): per trigger the next halfword of the GPDMA channel whose CxDAR is this DAC's DHR register is
// loaded (ring from CxSAR/CxBR1 or the linked-list item), CxBR1.BNDT kept = bytes left, HTF/TCF set (+ IRQ 29+n
// when the channel enables them).
//
// Capture (channel CaptureChannel, default 2 = cfw TX baseband DHR12R2, PA5; ch1 PA4 is the APC-like level): one uint16 LE 12-bit sample per DOR update (trigger), or per DHR
// write when the channel has no trigger. Same format as the ADC injector's .adc16, so a capture can be played into
// another radio with `analog Stream @tx_1.adc16 48000`.
//   Automatic (AutoCapture, default off since the TX audio moved to Pwm.cs): while rf0 transmits (RF model: reg 0x52 & 0x3000 = 0x3000 as stock keys TX, or reg 0x30 = 0x5F64 as
//   cfw rf_tx_fm) into $D890_OUT_DIR/tx_<n>.adc16 + tx_<n>.json (rate, RF TX frequency, regs 0x30/0x52/0x56,
//   DAC static levels = possible power control, sample count, duration). AutoCapture false turns it off.
//   Monitor device name: dac1.  Manual: dac1 Capture @file.adc16 / dac Stop (= CaptureStop).   dac Status (= CaptureStatus).
using System;
using System.IO;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890Dac : IDoubleWordPeripheral
    {
        public D890Dac(IMachine machine)
        {
            this.machine = machine;
            machine.ClockSource.AddClockEntry(new ClockEntry(1, 1000, Tick, this, "dac"));
        }

        public long Size => 0x400;
        public double TimerClockHz { get; set; } = 120000000;
        public uint TriggerTimer1 { get; set; }
        public uint TriggerTimer2 { get; set; }
        public int CaptureChannel { get; set; } = 2;      // cfw TX baseband = ch2 (PA5); ch1 (PA4) = stock APC level
        public bool AutoCapture { get; set; } = false;    // TX audio capture is models/Pwm.cs (TIM4_CH4 PB9) now
        public uint Dor1 => dor[0];
        public uint Dor2 => dor[1];

        public void Reset() { regs.Clear(); dor[0] = dor[1] = 0; }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
            case 0x2C: return dor[0];
            case 0x30: return dor[1];
            case 0x34: return Reg(0x34) | (1u << 11) | (1u << 27);
            default: return Reg(offset);
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            regs[offset] = value;
            switch(offset)
            {
            case 0x04:                                                   // SWTRGR
                if((value & 1) != 0) { Trigger(0); }
                if((value & 2) != 0) { Trigger(1); }
                regs[0x04] = 0;
                break;
            case 0x08: case 0x0C: case 0x10: dhr[0] = Dhr(offset, value); Loaded(0); break;
            case 0x14: case 0x18: case 0x1C: dhr[1] = Dhr(offset - 0x0C, value); Loaded(1); break;
            case 0x20: dhr[0] = value & 0xFFF; dhr[1] = (value >> 16) & 0xFFF; Loaded(0); Loaded(1); break;
            case 0x24: dhr[0] = (value >> 4) & 0xFFF; dhr[1] = (value >> 20) & 0xFFF; Loaded(0); Loaded(1); break;
            case 0x28: dhr[0] = (value & 0xFF) << 4; dhr[1] = ((value >> 8) & 0xFF) << 4; Loaded(0); Loaded(1); break;
            }
        }

        // ---------------- capture control ----------------
        public void Capture(string path) { Open(path, false); }
        public void Stop() { Close(); }
        public void CaptureStop() { Close(); }
        public string CaptureStatus() { return Status(); }

        public string Status()
        {
            return string.Format("ch1: en {0} ten {1} tsel {2} dma {3} dhr 0x{4:X3} dor 0x{5:X3} rate {6:F1} Hz; ch2: en {7} dhr 0x{8:X3} dor 0x{9:X3} rate {10:F1}; " +
                "capture {11} ({12} samples), tx {13}, captures {14}",
                En(0), Ten(0), Tsel(0), DmaEn(0), dhr[0], dor[0], rate[0], En(1), dhr[1], dor[1], rate[1],
                capFile ?? "off", capCount, RfTx(), txIndex);
        }

        // ---------------- internals ----------------
        private uint Reg(long o) { uint v; return regs.TryGetValue(o, out v) ? v : 0; }
        private static uint Dhr(long off, uint v)
        {
            if(off == 0x08) { return v & 0xFFF; }
            if(off == 0x0C) { return (v >> 4) & 0xFFF; }
            return (v & 0xFF) << 4;
        }
        private bool En(int c) { return (Reg(0) & (1u << (16 * c))) != 0; }
        private bool Ten(int c) { return (Reg(0) & (2u << (16 * c))) != 0; }
        private uint Tsel(int c) { return (Reg(0) >> (2 + 16 * c)) & 0xF; }
        private bool DmaEn(int c) { return (Reg(0) & (1u << (12 + 16 * c))) != 0; }

        private void Loaded(int c)
        {
            if(cap != null && levels[c].Count < 8 && !levels[c].Contains(dhr[c])) { levels[c].Add(dhr[c]); }
            if(!Ten(c) || !En(c))
            {
                dor[c] = dhr[c];
                if(c == CaptureChannel - 1) { Record(dor[c]); }          // software-paced: one sample per write
            }
        }

        private void Trigger(int c)
        {
            if(DmaEn(c)) { DmaFetch(c); }
            dor[c] = dhr[c];
            if(c == CaptureChannel - 1) { Record(dor[c]); }
        }

        private uint TimerFor(int c)
        {
            uint ov = c == 0 ? TriggerTimer1 : TriggerTimer2;
            if(ov != 0) { return ov; }
            switch(Tsel(c))
            {
            case 1: return 0x40012C00; case 2: return 0x40000000; case 3: return 0x40000800; case 4: return 0x40000C00;
            case 5: return 0x40001000; case 6: return 0x40001400; case 7: return 0x40013400; case 8: return 0x40014000;
            default: return 0;
            }
        }

        // 1 ms: trigger clocks, DMA ring discovery, automatic capture on RF TX
        private void Tick()
        {
            var bus = machine.SystemBus;
            for(int c = 0; c < 2; c++)
            {
                double r = 0;
                uint tim = TimerFor(c);
                if(En(c) && Ten(c) && tim != 0 && (bus.ReadDoubleWord(tim) & 1) != 0)
                {
                    double psc = (bus.ReadDoubleWord(tim + 0x28) & 0xFFFF) + 1;
                    double arr = bus.ReadDoubleWord(tim + 0x2C) + 1.0;
                    r = TimerClockHz / (psc * arr);
                    if(r > 2e6) { r = 0; }
                }
                if(Math.Abs(r - rate[c]) > 0.01)
                {
                    if(rate[c] > 0) { machine.ClockSource.TryRemoveClockEntry(c == 0 ? (Action)Trig0 : Trig1); }
                    rate[c] = r;
                    dmaCh[c] = -1;
                    if(r > 0) { machine.ClockSource.AddClockEntry(new ClockEntry(1, (ulong)Math.Round(r), c == 0 ? (Action)Trig0 : Trig1, this, "dac-trg" + c)); }
                }
            }
            if(cap != null && !capKeyed && RfTx()) { capKeyed = true; ReadRf(); }        // TX regs once keyed
            if(AutoCapture)
            {
                bool tx = RfTx();
                if(tx && !txOn) { txOn = true; txIndex++; txStart = Now(); StartAuto(); }
                else if(!tx && txOn) { txOn = false; if(autoFile) { Close(); } }
            }
        }

        private void Trig0() { Trigger(0); }
        private void Trig1() { Trigger(1); }

        private void DmaFetch(int c)
        {
            var bus = machine.SystemBus;
            if(dmaCh[c] < 0) { FindDma(c); if(dmaCh[c] < 0) { return; } }
            long gb = 0x40020000 + 0x50 + 0x80 * dmaCh[c];
            if((bus.ReadDoubleWord((ulong)(gb + 0x14)) & 1) == 0) { dmaCh[c] = -1; return; }   // channel disabled
            uint v = bus.ReadWord(ringAddr[c] + (ulong)(2 * ringPos[c]));
            ringPos[c]++;
            if(ringPos[c] >= ringLen[c]) { ringPos[c] = 0; }
            uint dar = bus.ReadDoubleWord((ulong)(gb + 0x50));
            dhr[c] = Dhr(dar - 0x46021800 - (c == 1 ? 0x0Cu : 0u), v);
            uint br1 = bus.ReadDoubleWord((ulong)(gb + 0x48));
            bus.WriteDoubleWord((ulong)(gb + 0x48), (br1 & 0xFFFF0000u) | (uint)(2 * (ringLen[c] - ringPos[c])));
            uint sr = bus.ReadDoubleWord((ulong)(gb + 0x10));
            uint fcr = bus.ReadDoubleWord((ulong)(gb + 0x0C));
            if(fcr != 0) { sr &= ~(fcr & 0x7F00); bus.WriteDoubleWord((ulong)(gb + 0x0C), 0); }
            if(ringPos[c] == ringLen[c] / 2) { sr |= 1u << 9; }
            if(ringPos[c] == 0) { sr |= 1u << 8; }
            bus.WriteDoubleWord((ulong)(gb + 0x10), sr);
            bool pending = (sr & bus.ReadDoubleWord((ulong)(gb + 0x14)) & 0x7F00) != 0;
            if(nvic == null) { IPeripheral p; if(machine.TryGetByName("sysbus.nvic", out p)) { nvic = p as IGPIOReceiver; } }
            if(nvic != null && pending != irq[c]) { nvic.OnGPIO(29 + dmaCh[c], pending); irq[c] = pending; }
        }

        private void FindDma(int c)
        {
            var bus = machine.SystemBus;
            uint lo = c == 0 ? 0x46021808u : 0x46021814u;
            for(int ch = 0; ch < 16; ch++)
            {
                long gb = 0x40020000 + 0x50 + 0x80 * ch;
                uint dar = bus.ReadDoubleWord((ulong)(gb + 0x50));
                if(dar < lo || dar > lo + 8 || (bus.ReadDoubleWord((ulong)(gb + 0x14)) & 1) == 0) { continue; }
                uint sar = bus.ReadDoubleWord((ulong)(gb + 0x4C));
                uint bndt = bus.ReadDoubleWord((ulong)(gb + 0x48)) & 0xFFFF;
                uint llr = bus.ReadDoubleWord((ulong)(gb + 0x7C));
                if((llr & 0xFFFC) != 0)
                {
                    ulong p = (bus.ReadDoubleWord((ulong)gb) & 0xFFFF0000u) | (llr & 0xFFFC);
                    if((llr & (1u << 31)) != 0) { p += 4; }
                    if((llr & (1u << 30)) != 0) { p += 4; }
                    if((llr & (1u << 29)) != 0) { bndt = bus.ReadDoubleWord(p) & 0xFFFF; p += 4; }
                    if((llr & (1u << 28)) != 0) { sar = bus.ReadDoubleWord(p); }
                }
                dmaCh[c] = ch; ringAddr[c] = sar; ringLen[c] = Math.Max(1, (int)(bndt / 2)); ringPos[c] = 0;
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
            capFile = path;
            autoFile = auto;
            capCount = 0;
            capStart = Now();
            levels[0].Clear(); levels[1].Clear(); levels[0].Add(dor[0]); levels[1].Add(dor[1]);
            cap = new BinaryWriter(File.Open(path, FileMode.Create));
            capKeyed = RfTx();
            ReadRf();
        }

        private void ReadRf()
        {
            IPeripheral p;
            if(machine.TryGetByName("sysbus.rf0", out p)) { var rf = p as D890RfTransceiver; if(rf != null) { capHz = rf.TunedHz; capR30 = rf.Peek(0x30); capR52 = rf.Peek(0x52); capR56 = rf.Peek(0x56); } }
        }

        private void Record(uint v)
        {
            if(cap == null) { return; }
            cap.Write((ushort)v);
            capCount++;
        }

        private void Close()
        {
            if(cap == null) { return; }
            cap.Close();
            cap = null;
            double dur = Now() - capStart;
            double sr = rate[CaptureChannel - 1];                     // 0 = software-paced (one sample per DHR write)
            string json = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{{\"file\": \"{0}\", \"samples\": {1}, \"rate\": {2:F1}, \"duration_s\": {3:F4}, \"start_s\": {4:F4}, " +
                "\"rf_tx_reg_hz\": {5}, \"rf_reg30\": \"0x{6:X4}\", \"rf_reg52\": \"0x{7:X4}\", \"rf_reg56\": \"0x{8:X4}\", " +
                "\"dac_ch1_levels\": [{9}], \"dac_ch2_levels\": [{10}], \"capture_channel\": {11}, \"auto\": {12}}}\n",
                Path.GetFileName(capFile).Replace("\\", "/"), capCount, sr, dur, capStart, capHz, capR30, capR52, capR56,
                Levels(0), Levels(1), CaptureChannel, autoFile ? "true" : "false");
            File.WriteAllText(Path.ChangeExtension(capFile, ".json"), json);
            capFile = null;
        }

        private string Levels(int c) { return string.Join(", ", levels[c].ConvertAll(v => "\"0x" + v.ToString("X3") + "\"")); }
        private readonly List<uint>[] levels = { new List<uint>(), new List<uint>() };

        private double Now() { return machine.ElapsedVirtualTime.TimeElapsed.TotalSeconds; }

        private readonly IMachine machine;
        private readonly Dictionary<long, uint> regs = new Dictionary<long, uint>();
        private readonly uint[] dhr = new uint[2], dor = new uint[2];
        private readonly double[] rate = new double[2];
        private readonly int[] dmaCh = { -1, -1 }, ringLen = new int[2], ringPos = new int[2];
        private readonly ulong[] ringAddr = new ulong[2];
        private readonly bool[] irq = new bool[2];
        private IGPIOReceiver nvic;
        private BinaryWriter cap;
        private string capFile;
        private bool autoFile, txOn, capKeyed;
        private long capCount, capHz;
        private int txIndex;
        private ushort capR30, capR52, capR56;
        private double capStart, txStart;
    }
}

// Analog front end of the D890UV: ADC1 scan + GPDMA transfer into RAM, plus an audio sample-stream injector.
// The firmware (FUN_0805c4ac) runs ADC1 in a 5-channel scan with DMA into DAT_20001d34 (5 halfwords):
//   [0] ch1 = discriminator audio (stock: read by TIM6 IRQ49 at 4.8 kHz, FUN_080d7d70 -> MDC1200 decoder
//             FUN_0813f3d8, sync 0x07092A446F)
//   [1] ch2 = battery (FUN_08039024: V*100 = (raw*0x528>>12) + cal(DAT_200152d2) - 100; < 6.01 V -> "Battery Volt Too Low!")
//   [2] ch3   [3] ch12   [4] ch19 = internal temperature sensor (FUN_080392a8, TS_CAL at 0x0BFA0710/0742)
// GPDMA1 and the ADC are storage models, so nothing fills the buffer: this model does.
//
// Two modes, chosen from the firmware's own register setup (re-read every 1 ms):
//  * Free-running (ADC1 CFGR1.EXTEN = 0, stock): every 1 ms the scan values are written to the destination of the
//    GPDMA channel whose source is ADC1_DR (fallback 0x20001d34). While a stream plays, the ch1 slot is also
//    updated at ServiceHz (default 48 kHz) with the stream sample at the current virtual time; the firmware samples
//    it whenever it reads the buffer (stock: TIM6 at 4.8 kHz).
//  * Timer-triggered (CFGR1.EXTEN != 0, e.g. cfw MCU-DSP): the trigger timer (EXTSEL -> TIMx TRGO, or TriggerTimer)
//    gives the rate = TimerClockHz / ((PSC+1)(ARR+1)). One scan frame (SQR1 length, ch1 slot = rank of channel 1)
//    is written per trigger into the GPDMA channel's buffer (CxDAR, CxBR1.BNDT, or the first linked-list item),
//    circularly; CxSR HTF/TCF are set at half/full and GPDMA1 channel n's IRQ (29 + n) is raised while an enabled
//    flag is pending. The firmware clears flags through CxFCR (polled here: the GPDMA is a storage model).
//    Without a stream the ch1 slot holds Set(0, ...) (mid-scale), so the DMA interrupts run either way.
//
// Stream file: mono 16-bit little-endian .raw (rate given) or .wav (PCM16, rate from the header; first channel):
// ADC value = 2048 + sample * StreamGain / 16. Or .adc16 = uint16 LE 12-bit ADC counts as the ADC would read them
// (cfw/dsp `make waves`): value = 2048 + (counts - 2048) * StreamGain. Clamped to 0..4095, resampled (linear) to
// the firmware's rate.
// Timer-triggered mode also keeps CxBR1.BNDT = bytes left in the block (cfw drivers/adc.c reads its write position
// from it, without interrupts) and plays the stream by frame count (one frame = 1 / firmware rate of signal), so
// Renode's 100 us quantum does not stair-step the samples.
// Monitor:
//   analog Set 1 2600   (scan index, 12-bit raw)   analog Battery 7.6   (volts)   analog Values
//   analog Stream @file.raw 48000    analog StreamLoop true (= analog Loop true)    analog StreamStop    analog StreamStatus
//   analog StreamGate true (stream only while rf0 has a carrier; else noise)
//   analog StreamGain 0.5            analog ServiceHz 48000    analog TriggerTimer 0x40001000 (0 = from EXTSEL)
using System;
using System.IO;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890Analog : IDoubleWordPeripheral
    {
        public D890Analog(IMachine machine)
        {
            this.machine = machine;
            Reset();
            machine.ClockSource.AddClockEntry(new ClockEntry(1, 1000, Tick, this, "analog"));
        }

        public long Size => 0x10;
        public uint Fallback { get; set; } = 0x20001d34;
        public double TimerClockHz { get; set; } = 120000000;
        public uint TriggerTimer { get; set; } = 0;
        public double StreamGain { get; set; } = 1.0;
        public bool StreamLoop { get; set; }
        // RF gate: the stream reaches ch1 only while rf0 sees a carrier (rf0 Signal <Hz> <dBm> on its tuned frequency);
        // otherwise ch1 gets discriminator noise (no carrier), so a receiver on the wrong frequency hears nothing.
        public bool StreamGate { get; set; }
        public bool Loop { get { return StreamLoop; } set { StreamLoop = value; } }      // alias: analog Loop true
        public long ServiceHz { get; set; } = 48000;
        // Free-running mode: the timer whose update interrupt reads the buffer (stock: TIM6 IRQ49, 4.8 kHz). Samples are
        // taken at that timer's exact tick times (from CNT), not at Renode's 100 us quantum boundaries; 0 = off.
        public uint ReaderTimer { get; set; } = 0x40001000;

        public void Reset() { }
        public uint ReadDoubleWord(long offset) { return 0; }
        public void WriteDoubleWord(long offset, uint value) { }

        public void Set(int index, int raw) { values[index] = (ushort)(raw & 0xFFF); }
        // Inverse of FUN_08039024 with calibration byte 0x64 (cal - 100 = 0); real cal byte comes from NAND.
        public void Battery(double volts) { values[1] = (ushort)Math.Min(4095, Math.Round(volts * 100 * 4096 / 0x528)); }
        public string Values() { return string.Join(" ", values.Select(v => v.ToString())); }

        // ---------------- stream ----------------
        public void Stream(string path, int rate)
        {
            var b = File.ReadAllBytes(path);
            counts = path.EndsWith(".adc16");
            int start = 0, step = 2, n;
            if(b.Length > 44 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F')
            {
                int p = 12, ch = 1;
                start = -1;
                while(p + 8 <= b.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(b, p, 4);
                    int len = BitConverter.ToInt32(b, p + 4);
                    if(id == "fmt ")
                    {
                        ch = BitConverter.ToInt16(b, p + 10);
                        rate = BitConverter.ToInt32(b, p + 12);
                        if(BitConverter.ToInt16(b, p + 22) != 16) { throw new ArgumentException("wav: only PCM16"); }
                    }
                    else if(id == "data") { start = p + 8; break; }
                    p += 8 + len + (len & 1);
                }
                if(start < 0) { throw new ArgumentException("wav: no data chunk"); }
                step = 2 * ch;
            }
            n = (b.Length - start) / step;
            stream = new short[n];
            for(int i = 0; i < n; i++) { stream[i] = BitConverter.ToInt16(b, start + i * step); }
            srcRate = Math.Max(1, rate);
            t0 = Now();
            streaming = n > 0;
            frame0 = fed;
            UpdateEntry(true);
        }

        public void StreamStop() { streaming = false; UpdateEntry(true); }

        public string StreamStatus()
        {
            return string.Format("{0}: {1} samples at {2} Hz ({3:F2} s), at {4:F3} s, loop {5}, gain {6}; mode {7}, firmware rate {8:F1} Hz, " +
                "scan {9} slots (ch1 slot {10}), dma ch {11} buf 0x{12:X8} {13} halfwords, frames written {14}, irqs {15}",
                streaming ? "playing" : "idle", stream.Length, srcRate, stream.Length / (double)srcRate,
                streaming ? Now() - t0 : 0, StreamLoop, StreamGain, triggered ? "timer-triggered" : "free-running",
                fwRate, scanLen, ch1Slot, dmaCh, bufAddr, bufLen, fed, irqs);
        }

        private readonly Random rng = new Random(7);
        private bool RfCarrier()
        {
            IPeripheral p;
            if(!machine.TryGetByName("sysbus.rf0", out p)) { return true; }
            var rf = p as D890RfTransceiver;
            return rf == null || rf.Carrier;
        }

        private double Now() { return machine.ElapsedVirtualTime.TimeElapsed.TotalSeconds; }

        // current ch1 value: stream sample at virtual time t (linear interpolation), else the static value
        private ushort Ch1(double t)
        {
            if(!streaming) { return values[0]; }
            if(StreamGate && !RfCarrier()) { return (ushort)(2048 + rng.Next(-400, 401)); }
            double x = (t - t0) * srcRate;
            if(x < 0) { x = 0; }
            int n = stream.Length;
            if(x >= n - 1)
            {
                if(!StreamLoop) { streaming = false; return values[0]; }
                x = x % n;
            }
            int i = (int)x;
            double f = x - i;
            double s = stream[i] * (1 - f) + stream[(i + 1) % n] * f;
            double v = counts ? 2048 + (s - 2048) * StreamGain : 2048 + s * StreamGain / 16;
            return (ushort)Math.Max(0, Math.Min(4095, Math.Round(v)));
        }

        // ---------------- 1 ms: scan values, configuration ----------------
        private void Tick()
        {
            var bus = machine.SystemBus;
            Configure();
            if(triggered) { return; }                     // the trigger entry writes whole frames
            uint dst = adcDst;
            if(dst < 0x20000000 || dst >= 0x200C0000) { dst = Fallback; }
            for(int i = 0; i < values.Length; i++)
            {
                bus.WriteWord((ulong)(dst + 2 * i), i == 0 && streaming ? Ch1(ReaderTick(Now())) : values[i]);
            }
        }

        private void Configure()
        {
            var bus = machine.SystemBus;
            adcDst = 0; dmaCh = -1;
            for(int ch = 0; ch < 16; ch++)
            {
                long b = 0x40020000 + 0x50 + 0x80 * ch;
                if(bus.ReadDoubleWord((ulong)(b + 0x4C)) == 0x42028040)        // CxSAR = ADC1_DR
                {
                    adcDst = bus.ReadDoubleWord((ulong)(b + 0x50));             // CxDAR
                    dmaCh = ch;
                    break;
                }
            }
            uint cfgr1 = bus.ReadDoubleWord(0x4202800C);
            uint cr = bus.ReadDoubleWord(0x42028008);
            uint sqr1 = bus.ReadDoubleWord(0x42028030);
            scanLen = (int)(sqr1 & 0xF) + 1;
            ch1Slot = 0;
            for(int r = 0; r < Math.Min(scanLen, 4); r++)
            {
                if(((sqr1 >> (6 + 6 * r)) & 0x1F) == 1) { ch1Slot = r; break; }
            }
            bool was = triggered;
            double oldRate = fwRate;
            triggered = false;
            if(((cfgr1 >> 10) & 3) != 0 && (cr & 1) != 0 && dmaCh >= 0)
            {
                uint tim = TriggerTimer != 0 ? TriggerTimer : TimerForExtsel((cfgr1 >> 5) & 0x1F);
                long gb = 0x40020000 + 0x50 + 0x80 * dmaCh;
                bool dmaOn = (bus.ReadDoubleWord((ulong)(gb + 0x14)) & 1) != 0;
                if(tim != 0 && dmaOn && (bus.ReadDoubleWord(tim) & 1) != 0)
                {
                    double psc = bus.ReadDoubleWord(tim + 0x28) & 0xFFFF;
                    double arr = bus.ReadDoubleWord(tim + 0x2C);
                    fwRate = TimerClockHz / ((psc + 1) * (arr + 1));
                    triggered = fwRate > 0 && fwRate <= 1e6;
                    if(triggered && (!was || bufAddr == 0)) { SetupBuffer(gb); }
                }
            }
            if(!triggered) { fwRate = 0; }
            if(was != triggered || Math.Abs(oldRate - fwRate) > 0.01) { UpdateEntry(false); }
        }

        // ADC1 EXTSEL -> trigger timer base (RM0456 ADC1 regular trigger table, TRGO entries); 0 = unknown
        private static uint TimerForExtsel(uint e)
        {
            switch(e)
            {
            case 4: return 0x40000400;   // TIM3_TRGO
            case 7: return 0x40013400;   // TIM8_TRGO
            case 9: return 0x40012C00;   // TIM1_TRGO
            case 11: return 0x40000000;  // TIM2_TRGO
            case 12: return 0x40000800;  // TIM4_TRGO
            case 13: return 0x40001000;  // TIM6_TRGO
            case 14: return 0x40014000;  // TIM15_TRGO
            default: return 0;
            }
        }

        private void SetupBuffer(long gb)
        {
            var bus = machine.SystemBus;
            bufAddr = bus.ReadDoubleWord((ulong)(gb + 0x50));                   // CxDAR
            uint bndt = bus.ReadDoubleWord((ulong)(gb + 0x48)) & 0xFFFF;       // CxBR1.BNDT (bytes)
            uint llr = bus.ReadDoubleWord((ulong)(gb + 0x7C));                  // CxLLR
            if((llr & 0xFFFC) != 0)                                           // prefer the list item (full block)
            {
                // first linked-list item: fields present in update-bit order TR1 TR2 BR1 SAR DAR (TR3 BR2) LLR
                uint lbar = bus.ReadDoubleWord((ulong)(gb + 0x00)) & 0xFFFF0000;
                ulong p = lbar | (llr & 0xFFFC);
                if((llr & (1u << 31)) != 0) { p += 4; }                         // UT1
                if((llr & (1u << 30)) != 0) { p += 4; }                         // UT2
                if((llr & (1u << 29)) != 0) { bndt = bus.ReadDoubleWord(p) & 0xFFFF; p += 4; }   // UB1
                if((llr & (1u << 28)) != 0) { p += 4; }                         // USA
                if((llr & (1u << 27)) != 0) { bufAddr = bus.ReadDoubleWord(p); }                // UDA
            }
            bufLen = (int)(bndt / 2);
            if(bufLen < scanLen) { bufLen = scanLen; }
            pos = 0;
        }

        // the sample clock: firmware rate when timer-triggered, ServiceHz while a stream plays free-running
        private void UpdateEntry(bool force)
        {
            long hz = triggered ? (long)Math.Round(fwRate) : (streaming ? ServiceHz : 0);
            if(!force && hz == entryHz) { return; }
            if(entryHz != 0) { machine.ClockSource.TryRemoveClockEntry(Sample); }
            entryHz = hz;
            if(hz > 0) { machine.ClockSource.AddClockEntry(new ClockEntry(1, (ulong)hz, Sample, this, "analog-stream")); }
        }

        private void Sample()
        {
            var bus = machine.SystemBus;
            double t = Now();
            if(!triggered)
            {
                if(!streaming) { return; }
                uint dst = adcDst;
                if(dst < 0x20000000 || dst >= 0x200C0000) { dst = Fallback; }
                bus.WriteWord(dst, Ch1(ReaderTick(t)));     // scan slot 0 = ch1 (stock order)
                return;
            }
            // one scan frame per trigger; stream time by frame count
            ushort c1 = Ch1(t0 + (fed - frame0) / fwRate);
            for(int i = 0; i < scanLen; i++)
            {
                ushort v = i == ch1Slot ? c1 : (i < values.Length ? values[i] : (ushort)2048);
                bus.WriteWord(bufAddr + (ulong)(2 * pos), v);
                pos++;
                if(pos >= bufLen) { pos = 0; }
            }
            fed++;
            long gb = 0x40020000 + 0x50 + 0x80 * dmaCh;
            uint br1 = bus.ReadDoubleWord((ulong)(gb + 0x48));
            bus.WriteDoubleWord((ulong)(gb + 0x48), (br1 & 0xFFFF0000u) | (uint)(2 * (bufLen - pos)));   // BNDT
            uint sr = bus.ReadDoubleWord((ulong)(gb + 0x10));
            uint fcr = bus.ReadDoubleWord((ulong)(gb + 0x0C));
            if(fcr != 0) { sr &= ~(fcr & 0x7F00); bus.WriteDoubleWord((ulong)(gb + 0x0C), 0); }
            int half = bufLen / 2;
            int frameEnd = pos;                                 // position after this frame
            if(frameEnd >= half && frameEnd - scanLen < half) { sr |= 1u << 9; }       // HTF
            if(frameEnd < scanLen) { sr |= 1u << 8; }                                  // TCF (wrapped)
            bus.WriteDoubleWord((ulong)(gb + 0x10), sr);
            uint ccr = bus.ReadDoubleWord((ulong)(gb + 0x14));
            bool pending = (sr & ccr & 0x7F00) != 0;
            if(nvic == null)
            {
                IPeripheral p;
                if(machine.TryGetByName("sysbus.nvic", out p)) { nvic = p as IGPIOReceiver; }
            }
            if(nvic != null && pending != irqLevel)
            {
                nvic.OnGPIO(29 + dmaCh, pending);
                if(pending) { irqs++; }
                irqLevel = pending;
            }
        }

        // Stream time for the reader timer's update event nearest to t (the one the firmware reads next or just read).
        // Renode's STM32_Timer counts ARR (not ARR + 1) ticks per period, so TIM6 with ARR 249 runs at 4819 Hz instead
        // of 4800 Hz; the tick grid is located with Renode's period and mapped onto the ideal one, so the firmware
        // gets exactly one stream sample per 1/4800 s of signal (otherwise the MDC1200 decoder slips a bit per packet).
        private double ReaderTick(double t)
        {
            if(ReaderTimer == 0) { return t; }
            var bus = machine.SystemBus;
            if((bus.ReadDoubleWord(ReaderTimer) & 1) == 0) { return t; }
            double psc = (bus.ReadDoubleWord(ReaderTimer + 0x28) & 0xFFFF) + 1;
            double arr = bus.ReadDoubleWord(ReaderTimer + 0x2C);
            if(arr < 1) { return t; }
            double cnt = bus.ReadDoubleWord(ReaderTimer + 0x24);
            double renodePeriod = arr * psc / TimerClockHz;
            double idealPeriod = (arr + 1) * psc / TimerClockHz;
            double next = t + Math.Max(0, arr - cnt) * psc / TimerClockHz;
            double prev = next - renodePeriod;
            double tick = (next - t) < (t - prev) ? next : prev;
            return t0 + (tick - t0) * idealPeriod / renodePeriod;
        }

        private readonly IMachine machine;
        // ch1, battery 7.6 V, ch3, ch12, temperature ~30 C with TS_CAL1 0x1400 (devinfo)
        private readonly ushort[] values = { 2048, 2358, 2048, 2048, 1164 };
        private short[] stream = new short[0];
        private double srcRate = 48000, t0, fwRate;
        private bool streaming, triggered, irqLevel, counts;
        private long frame0;
        private uint adcDst;
        private ulong bufAddr;
        private int dmaCh = -1, scanLen = 5, ch1Slot, bufLen, pos;
        private long entryHz, fed, irqs;
        private IGPIOReceiver nvic;
    }
}

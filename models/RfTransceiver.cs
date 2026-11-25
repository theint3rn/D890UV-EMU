// RF transceiver chip on the D890UV (two fitted, one per CS line), 3-wire bit-banged bus
// (FUN_080e2f64 write / FUN_080e2cac read): CS# low, 8 bits (reg << 1 | R), then 16 data bits, MSB first.
// SCK = PB3, SDA = PB4 (bidirectional), CS# = PB5 (chip 0) / PD3 (chip 1).
// MCU samples SDA while SCK is high, so the model drives each read bit on the SCK rising edge.
//
// GPIO inputs: 0 = CS#, 1 = SCK, 2 = SDA (MCU -> chip)
// Registers are a plain 128 x 16-bit file (enough for the boot self-test: reg 0x57 write/read-back).
// Monitor: rf0 Peek 0x57 / rf0 Poke 0x67 0x1234 / rf0 Trace true
//
// Received signal ("on air", shared by both chips): rf0 Signal 146520000 -80  (Hz, dBm) / rf0 SignalOff /
// rf0 Status, rf0 SignalHere -80 (on the chip's current frequency). A chip whose frequency regs 0x69:0x68 (Hz) are within Window Hz (default 6250) of the signal and
// whose RX is on (reg 0x30 low byte != 0; 0x5F00 = idle) sees it; otherwise the noise floor (FloorDbm, -130).
// Status registers the stock firmware reads (V1.05):
//   0x02 [9:0]  RSSI. FUN_08049e3c / squelch FUN_080d22f0 (via FUN_08049e10 -> FUN_080e2cac): open when
//               RSSI >= threshold (the value written to reg 0x49 by FUN_080e36f4), close below threshold - 12.
//               Scale is not known from the code: the model uses raw = 2 * (dBm + 160) (0.5 dB steps,
//               -80 dBm = 160, floor -130 dBm = 60), clamped to 0..0x3FF.
//   0x01 [15:8] noise (squelch closes when > 0x50), [3:0] glitch count (> 8 with high RSSI = no open).
//               Model: carrier -> noise 0x10, glitch 0; none -> noise 0x7F, glitch 15.
//   0x0E bit 14 carrier / squelch-detect flag (DAT_2000406c, FUN_080df38c). bits 9:8 = tone-scan result type
//               (FUN_080d5bd4) are kept from the register file.
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890RfTransceiver : IDoubleWordPeripheral, IGPIOReceiver
    {
        // Renode limitation: SCK and SDA share GPIOB, and the port re-applies its output register to
        // PB4 after every clock write, so bit-level SDA reads cannot reach the CPU. emu/run.py hooks the
        // firmware's 16-bit read routine (FUN_080e2da4) and returns ActiveRead() instead.
        public static ushort ActiveRead()
        {
            foreach(var c in instances) { if(c.reading && !c.cs) { c.bits = 24; return c.readValue; } }
            return 0xFFFF;
        }
        public ushort ReadActive() { return ActiveRead(); }
        private static readonly System.Collections.Generic.List<D890RfTransceiver> instances = new System.Collections.Generic.List<D890RfTransceiver>();

        public D890RfTransceiver(IMachine machine, string sdaPort = "gpioPortB", int sdaPin = 4)
        {
            instances.Add(this);
            this.machine = machine;
            this.sdaPort = sdaPort;
            this.sdaPin = sdaPin;
            regs = new ushort[128];
            sub = new ushort[128 * 16];
            Reset();
        }

        // Register this as an external driver of SDA in the .repl (Sda -> gpioPortB@4); Renode's port
        // otherwise lets the pull-up win on input pins that have no connected source.
        public GPIO Sda { get; } = new GPIO();
        public long Size => 0x10;
        public bool Trace { get; set; }
        public long Writes { get; private set; }
        public long Reads { get; private set; }

        public void Reset()
        {
            cs = true; sck = false; sda = false; bits = 0; shift = 0;
        }

        public uint ReadDoubleWord(long offset) { return 0; }
        public void WriteDoubleWord(long offset, uint value) { }

        public ushort Peek(int reg) { return ReadReg(reg & 0x7F); }

        // ---- received signal (static: one air for both chips) ----
        private static long sigHz;
        private static double sigDbm;
        private static bool sigOn;
        public static double FloorDbm = -130;
        public static long Window = 6250;
        public double Floor { get { return FloorDbm; } set { FloorDbm = value; } }
        public long SignalWindow { get { return Window; } set { Window = value; } }

        public void Signal(long hz, double dbm) { sigHz = hz; sigDbm = dbm; sigOn = true; }
        public void SignalOff() { sigOn = false; }
        public void SignalHere(double dbm) { Signal(TunedHz, dbm); }       // on this chip's current frequency
        public long TunedHz => ((long)regs[0x69] << 16) | regs[0x68];
        // receiver on: reg 0x56 = 1 (6 = AM down-converter, chip 1) as the stock firmware ends every receive set-up
        public bool RxOn => (regs[0x30] & 0xFF) != 0 && regs[0x30] != 0x5F64 && (regs[0x56] == 1 || regs[0x56] == 6);
        // StrictRx (emu/run.py sets it for cfw runs, chip 0): the signal is seen only when the registers match a stock
        // receive state (V1.05 register traces, cfw/docs/RF_COMPARE.md), else Status says which one does not.
        public bool StrictRx { get; set; }
        public string RxFault()
        {
            bool u = TunedHz >= 300000000, dig = regs[0x2A] == 0x108C;
            if(regs[0x56] == 6) { return ""; }
            if(regs[0x56] != 1) { return "reg 0x56"; }
            if(regs[0x5F] != (TunedHz < 390000000 ? 0x0468 : 0x0569)) { return "reg 0x5F (band)"; }
            if(regs[0x60] != 0x0309) { return "reg 0x60"; }
            if(Sub(0x53, 4) != (u ? 0x463D : 0x467F) || Sub(0x53, 2) != (u ? 0x2637 : 0x263A)) { return "reg 0x53 (band table)"; }
            bool s0 = (Sub(0x52, 0) & 1) != 0, s4 = (Sub(0x52, 4) & 1) != 0, s6 = (Sub(0x52, 6) & 1) != 0, s7 = (Sub(0x52, 7) & 1) != 0;
            bool normal = s0 == u && s6 == !u && s7 == !u && s4 == u, am = s0 == u && !s6 && s7 == u && s4 == !u;
            if(!normal && !am) { return "reg 0x52 (band switches)"; }
            if(!dig && regs[0x2A] != 0x1081) { return "reg 0x2A"; }
            if(regs[0x32] != (dig ? 0x0622 : 0x22A2)) { return "reg 0x32 (receive filter set)"; }
            if(dig ? regs[0x48] != 0x016E : (regs[0x48] != 0x082E && regs[0x48] != 0x9826)) { return "reg 0x48 (bandwidth)"; }
            return "";
        }
        private int Sub(int r, int idx) { return sub[r * 16 + idx]; }
        // TX keyed: stock FUN_080e3c68 reg 0x56 = 0x0002 (0xE042 variant) as the last step. Reg 0x52 switch 3 is the UHF / VHF
        // transmit path switch (not written at all on VHF), not the key.
        public bool Transmitting => (regs[0x56] & 0x000F) == 2 || regs[0x30] == 0x5F64;
        public bool Carrier => sigOn && RxOn && (!StrictRx || RxFault() == "") && Math.Abs(TunedHz - sigHz) <= Window && sigDbm > FloorDbm;
        public double Dbm => Carrier ? sigDbm : FloorDbm;
        // Opt-in (rf0 SquelchFlag true): reg 0x0E bit 14 = carrier AND RSSI >= the threshold in reg 0x49 [9:0], i.e. the
        // chip's own squelch-detect against the threshold the firmware writes (assumed; default off = any carrier).
        // Noise / glitch (reg 0x01) still follow the signal, so the firmware's close hysteresis (threshold - 12) shows.
        public bool SquelchFlag { get; set; }
        bool CarrierFlag => Carrier && (!SquelchFlag || RawRssi >= (regs[0x49] & 0x3FF));
        public int RawRssi => Math.Max(0, Math.Min(0x3FF, (int)Math.Round(2 * (Dbm + 160))));
        public string Status()
        {
            return string.Format("tuned {0} Hz, rx {1}, signal {2}, carrier {3}, {4} dBm, RSSI reg 0x02 = 0x{5:X4}, reg 0x01 = 0x{6:X4}, reg 0x0E = 0x{7:X4}, sq thr reg 0x49 = 0x{8:X4}",
                TunedHz, Transmitting ? "off (TX)" : !RxOn ? "off" : StrictRx && RxFault() != "" ? "FAULT " + RxFault() : "on", sigOn ? string.Format("{0} Hz {1} dBm", sigHz, sigDbm) : "off",
                Carrier, Dbm, ReadReg(0x02), ReadReg(0x01), ReadReg(0x0E), regs[0x49]);
        }

        private ushort ReadReg(int r)
        {
            switch(r)
            {
            case 0x02: return (ushort)((regs[0x02] & 0xFC00) | RawRssi);
            case 0x01: return Carrier ? (ushort)0x1000 : (ushort)0x7F0F;
            case 0x0E: return CarrierFlag ? (ushort)(regs[0x0E] | 0x4000) : (ushort)(regs[0x0E] & ~0x4000);
            default: return regs[r];
            }
        }
        public void Poke(int reg, ushort value) { regs[reg & 0x7F] = value; }

        public void OnGPIO(int number, bool value)
        {
            switch(number)
            {
            case 0:
                if(value && !cs && Trace) { this.Log(LogLevel.Info, "RF end: {0} rising edges, read={1}, driven {2}", bits, reading, driven); }
                if(!value && cs) { bits = 0; shift = 0; reading = false; driven = ""; }
                cs = value;
                break;
            case 1:
                if(!cs && value && !sck) { Rising(); }
                else if(!cs && !value && sck && reading)
                {
                    if(bits < 24) { DriveSda(((readValue >> (23 - bits)) & 1) != 0); }
                }
                sck = value;
                break;
            case 2:
                sda = value;
                break;
            }
        }

        private void Rising()
        {
            if(reading)
            {
                if(bits < 24)
                {
                    var bit = ((readValue >> (23 - bits)) & 1) != 0;
                    DriveSda(bit);
                    if(Trace) { driven += bit ? "1" : "0"; }
                }
                bits++;
                return;
            }
            shift = (shift << 1) | (sda ? 1 : 0);
            bits++;
            if(bits == 8)
            {
                reg = (shift >> 1) & 0x7F;
                if((shift & 1) != 0)
                {
                    reading = true;
                    readValue = ReadReg(reg);
                    DriveSda(((readValue >> 15) & 1) != 0);
                    Reads++;
                    if(Trace) { this.Log(LogLevel.Info, "RF read  reg 0x{0:X2} = 0x{1:X4}", reg, readValue); }
                }
            }
            else if(bits == 24)
            {
                regs[reg] = (ushort)(shift & 0xFFFF);
                sub[reg * 16 + (regs[reg] >> 12)] = regs[reg];        // registers whose top nibble selects a sub-register
                Writes++;
                if(Trace) { this.Log(LogLevel.Info, "RF write reg 0x{0:X2} = 0x{1:X4}", reg, regs[reg]); }
            }
        }

        private void DriveSda(bool level)
        {
            if(port == null)
            {
                IPeripheral per;
                if(machine.TryGetByName("sysbus." + sdaPort, out per)) { port = per as IGPIOReceiver; }
            }
            Sda.Set(level);
            port?.OnGPIO(sdaPin, level);
            // Renode's STM32 GPIO port re-applies the output data register to the pin after every
            // BSRR write, even in input mode. The MCU leaves ODR4 = 1 before reading, so also set
            // the ODR bit to the chip's level (BSRR at port base + 0x18; GPIOB = 0x42020400).
            machine.SystemBus.WriteDoubleWord(0x42020418, level ? (1u << sdaPin) : (1u << (sdaPin + 16)));
        }

        private readonly IMachine machine;
        private readonly string sdaPort;
        private readonly int sdaPin;
        private readonly ushort[] regs, sub;
        private IGPIOReceiver port;
        private bool cs, sck, sda, reading;
        private int bits, shift, reg;
        private ushort readValue;
        private string driven = "";
    }
}

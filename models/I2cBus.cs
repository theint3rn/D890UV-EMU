// D890UV bit-banged I2C bus: SDA PB6, SCL PB7 (stock V1.05 FUN_080585b4 start, FUN_080583ac stop, FUN_080584d4 byte).
// Same bus logic as D168UV/emu/models/I2cBus168.cs. Slaves:
//   0x19 (0x32/0x33) LIS2DW12 accelerometer, register access with auto-increment, WHO_AM_I 0x0F = 0x44.
//        Man down (stock FUN_08037314 / FUN_080373dc, cfw app/mandown.c) reads STATUS 0x27 bit0 and SIXD_SRC 0x3A bit0
//        (XL = upright). Monitor: `i2c Fall`, `i2c Upright`, `i2c Orientation`.
//   0x2E (0x5C) speaker volume potentiometer: stock FUN_0806536c writes [0x00, value] (value 0..0x7F from table
//        0x0815153E[Vol Level][knob step]). Monitor: `i2c Pot` (last value, -1 = never written), `i2c PotLog`.
//   0x35 (0x6A/0x6B): seen in the stock code, not modelled (NACK).
// Open drain: the line level the MCU reads = MCU level AND slave level; the slave's level is pushed into
// the GPIO port input (pin 6) on every change.
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890I2cBus : IDoubleWordPeripheral, IGPIOReceiver
    {
        public D890I2cBus(IMachine machine)
        {
            this.machine = machine;
            Accel[0x0F] = 0x44;
            Upright();
            Reset();
        }

        public long Size => 0x10;
        public bool Trace { get; set; }
        public byte[] Accel { get; } = new byte[256];
        public byte[] Fm { get; } = new byte[64];
        public int Pot { get; private set; } = -1;
        public string PotLog { get; private set; } = "";
        public string LastFmWrite { get; private set; } = "";

        public string Orientation { get; private set; } = "";

        // Radio standing upright: X axis ~ -1 g (OUT_X_H 0xC0), 6D source IA|XL.
        public void Upright() { SetAccel("Upright", 0x41, 0, 0xC0, 0); }
        // Radio lying flat (fallen): Z axis ~ +1 g, 6D source IA|ZH, XL clear.
        public void Fall() { SetAccel("Fallen", 0x60, 0, 0, 0x40); }
        // Raw 6D source byte (bit0 XL is the only bit the stock firmware tests).
        public void SixD(int value) { Accel[0x3A] = (byte)value; Orientation = "SIXD_SRC 0x" + value.ToString("X2"); }

        private void SetAccel(string name, byte sixd, byte xh, byte xhX, byte zh)
        {
            Orientation = name;
            Accel[0x27] |= 0x01;                     // STATUS: DRDY
            Accel[0x37] = Accel[0x27];               // STATUS_DUP
            Accel[0x3A] = sixd;                      // SIXD_SRC
            Accel[0x29] = xhX; Accel[0x2B] = xh; Accel[0x2D] = zh;   // OUT_X_H / OUT_Y_H / OUT_Z_H
        }

        public void Reset()
        {
            sda = true; scl = true; state = State.Idle; slaveLow = false;
        }

        public uint ReadDoubleWord(long offset) { return 0; }
        public void WriteDoubleWord(long offset, uint value) { }

        public void OnGPIO(int number, bool value)
        {
            if(number == 0)                                   // SDA from MCU
            {
                if(driving) { return; }                       // echo of our own port drive
                if(slaveLow && value)                         // MCU releases/pulls up while the slave holds low
                {
                    if(scl && !sda) { Stop(); sda = true; return; }
                    sda = value; Drive(false); return;
                }
                if(scl && sda && !value) { Start(); }
                else if(scl && !sda && value) { Stop(); }
                sda = value;
            }
            else if(number == 1)                              // SCL
            {
                if(!scl && value) { Rising(); }
                else if(scl && !value) { Falling(); }
                scl = value;
            }
        }

        private enum State { Idle, Addr, Write, Read, Nack }

        private void Start()
        {
            if(state == State.Write && dev == 0x2E && wbuf.Count > 0) { PotWrite(); }
            state = State.Addr; bits = 0; shift = 0; ackPhase = false; wbuf.Clear(); Release();
        }

        private void Stop()
        {
            if(state == State.Write && dev == 0x2E && wbuf.Count > 0) { PotWrite(); }
            state = State.Idle; Release();
        }

        private void Rising()
        {
            // Renode's F1 port ignores input levels on pins in output mode: the MCU switches SDA to input just
            // before clocking (FUN_0808dfdc), so re-apply the slave's level at every SCL rise it may sample.
            if((ackPhase && slaveAck) || (state == State.Read && !ackPhase)) { Drive(!slaveLow); }
            if(ackPhase) { if(!slaveAck) { ackMaster = sda; } return; }   // master ACK/NACK during reads
            if(state == State.Addr || state == State.Write)
            {
                shift = (byte)((shift << 1) | (sda ? 1 : 0));
                bits++;
            }
            else if(state == State.Read)
            {
                bits++;
            }
        }

        private void Falling()
        {
            if(ackPhase)
            {
                ackPhase = false; Release();
                if(slaveAck)
                {
                    slaveAck = false;
                    if(state == State.Read) { bits = 0; DriveBit(); }     // first data bit after address ACK
                    return;
                }
                if(state == State.Read)
                {
                    if(ackMaster) { state = State.Nack; return; }      // master NACK: done
                    bits = 0; cur = NextRead(); bitIdx = 0; Drive(((cur >> 7) & 1) != 0); bitIdx = 1;
                }
                return;
            }
            if(bits < 8)
            {
                if(state == State.Read) { DriveBit(); }
                return;
            }
            bits = 0;
            if(state == State.Addr)
            {
                dev = shift >> 1;
                bool rd = (shift & 1) != 0;
                if(dev != 0x2E && dev != 0x19) { state = State.Nack; return; }
                if(Trace) { this.Log(LogLevel.Info, "I2C start dev 0x{0:X2} {1}", dev, rd ? "read" : "write"); }
                state = rd ? State.Read : State.Write;
                first = true;
                if(rd) { readStart = true; }
                Ack();
            }
            else if(state == State.Write)
            {
                if(dev == 0x19)
                {
                    if(first) { reg = shift & 0x7F; first = false; }
                    else { Accel[reg & 0xFF] = shift; reg++; }
                }
                else { wbuf.Add(shift); }
                Ack();
            }
            else if(state == State.Read)
            {
                Release(); ackPhase = true; slaveAck = false;   // master drives ACK
            }
        }

        private void Ack() { ackPhase = true; slaveAck = true; Drive(false); }

        private byte NextRead()
        {
            if(dev == 0x19) { var v = Accel[reg & 0xFF]; reg++; return v; }
            var f = Fm[fmIdx % Fm.Length]; fmIdx++; return f;
        }

        // first data bit of a read goes out right after the address ACK
        private void DriveBit()
        {
            if(readStart) { readStart = false; cur = NextRead(); bitIdx = 0; }
            if(bitIdx > 7) { return; }
            Drive(((cur >> (7 - bitIdx)) & 1) != 0);
            bitIdx++;
        }

        private void PotWrite()
        {
            LastFmWrite = BitConverter.ToString(wbuf.ToArray());
            if(wbuf.Count >= 2) { Pot = wbuf[wbuf.Count - 1]; PotLog = (PotLog + " " + Pot.ToString("X2")).Trim(); }
            if(Trace) { this.Log(LogLevel.Info, "pot write {0}", LastFmWrite); }
            wbuf.Clear(); fmIdx = 0;
        }

        private void Release() { Drive(true); }

        private void Drive(bool level)
        {
            slaveLow = !level;
            if(port == null)
            {
                IPeripheral p;
                if(machine.TryGetByName("sysbus.gpioPortB", out p)) { port = p as IGPIOReceiver; }
            }
            driving = true;
            port?.OnGPIO(6, level && (sda || !level));
            driving = false;
        }

        private readonly IMachine machine;
        private readonly List<byte> wbuf = new List<byte>();
        private IGPIOReceiver port;
        private bool driving, sda, scl, slaveLow, ackPhase, slaveAck, ackMaster, first, readStart;
        private State state;
        private int bits, dev, reg, fmIdx, bitIdx;
        private byte shift, cur;
    }
}

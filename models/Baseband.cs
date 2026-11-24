// SPI2 @0x40003800 + SCT3288 DMR baseband (behavioural), D890UV.
// Link (FUN_0810a674 / FUN_0810a6dc): CS# PB12; MCU reads while PD11 (byte ready) and PD7 (frame pending) are high.
// Frames both ways: 84 A9 61 LenH LenL B0 ID <Len-1 data bytes> [pad 00 if Len odd]   (parser FUN_0813af78,
// message entry = frame+5 -> B0, ID, data; FUN_0802de84 / FUN_080dee40).
// Behaviour:
//   * on SPI enable: send Startup messages
//   * every PeriodMs with an empty queue: send Periodic messages (status the MCU polls for, e.g. ID 0x17)
//   * MCU frame ID x -> reply from Rules[x] (data bytes) or default status 00
//   * MCU frame ID x -> also, EventDelayMs later, the messages in Events[x]. Default: cmd 0x43 (start repeater
//     search / roam step, FUN_080ff350 sets busy DAT_200212fc) -> 00 17 0A (status 0x0A = search done, nothing
//     found; dispatcher FUN_08146930 -> FUN_080fbc44 -> FUN_080ffc50 / FUN_080f1a94 clear busy). Without it the
//     radio hangs on "Roaming.... Please Wait!" and every later command fails (-6).
//   * boot status (ID 0x17): 0x0E until the MCU sends its raw 6-byte config "74 A9 ..." (FUN_08100664),
//     then 0x00 (= ready; FUN_0810061c / FUN_081005cc). PD9 = baseband reset (FUN_080f4698), not modelled.
//   * AMBE-3000-style vocoder packets (B0 = packet type 0x80 control / 0x81 channel / 0x82 speech; stock V1.05 never
//     sends these, SCT_Port CommandArray_p_ambe3000.xml does): fake vocoder, see cfw/docs/SCT3288_VOCODER.md.
//     0x82 [00 n s16be*n] -> 0x81 [01 bits data]: data = 'F' freqBE16 (0.25 Hz units, interpolated zero crossings) peak/128 + zeros;
//     0x81 [01 bits data] -> 0x82 [00 A0 160 samples]: sine with that freq/amp (phase-continuous); other data -> silence.
//     0x80 fields: 30 PRODID, 31 VERSTRING, 09 RATET, 0A RATEP, 33 RESET, 34, 35, 36 GETCFG, 05 ECMODE, 0B INIT ...
//     -> echo field + status 00 (strings: VocoderProdId / VocoderVersion). Rate 33 -> 72 bits, 34 -> 49 bits.
// Monitor: spi2 Send "00 17 00"  (queue a message: B0 ID data...), spi2 SetRule 0x30 "00 00 ...",
//          spi2 SetEvent 0x43 "00 17 0A" (empty string removes), spi2 EventDelayMs 500, spi2 FromMcu "84 A9 61 00 01 80 30" (inject MCU bytes)
// Log: env D890_BB_LOG (one line per frame).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890Baseband : IDoubleWordPeripheral, IBytePeripheral
    {
        public D890Baseband(IMachine machine, int periodMs = 50)
        {
            this.machine = machine;
            PeriodMs = periodMs;
            Startup = new List<byte[]> { Hex("00 10 00") };
            Periodic = new List<byte[]> { Hex("00 10 00") };
            Rules = new Dictionary<int, byte[]> { { 0x33, new byte[9] } };   // 0x33 = version (BCD date at +3..7)
            Events = new Dictionary<int, byte[]> { { 0x43, Hex("00 17 0A") } };
            EventDelayMs = 500;
            logPath = Environment.GetEnvironmentVariable("D890_BB_LOG");
            machine.ClockSource.AddClockEntry(new ClockEntry(1, 1000, Tick, this, "baseband"));
            Reset();
        }

        public int PeriodMs { get; set; }
        public List<byte[]> Startup { get; }
        public List<byte[]> Periodic { get; }
        public Dictionary<int, byte[]> Rules { get; }
        public Dictionary<int, byte[]> Events { get; }
        public int EventDelayMs { get; set; }
        public int BootStatus { get; set; }
        public long Size => 0x400;

        public void Reset()
        {
            regs.Clear(); rx.Clear(); inbuf.Clear(); raw.Clear(); pending.Clear(); started = false; BootStatus = 0x0E; sinceSent = 0; rxd = 0xFF; lastTx = 0;
            Ready(false);
        }

        public void Send(string hex) { Queue(Hex(hex), "manual"); }
        // Test without MCU code: feed bytes as if the MCU wrote them (e.g. a whole 84 A9 61 frame).
        public void FromMcu(string hex) { foreach(var x in Hex(hex)) { Assemble(x); } }
        public void SetRule(int id, string hex) { Rules[id] = Hex(hex); }
        public void SetEvent(int id, string hex) { var b = Hex(hex); if(b.Length == 0) { Events.Remove(id); } else { Events[id] = b; } }

        public byte ReadByte(long offset) { return (byte)ReadDoubleWord(offset & ~3) ; }
        public void WriteByte(long offset, byte value) { WriteDoubleWord(offset, value); }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
            case 0x14: return 0x1 | 0x2 | 0x8 | 0x10 | (1u << 13);   // RXP TXP EOT TXTF, 1 frame in FIFO
            case 0x30: return rxd;
            default: uint v; return regs.TryGetValue(offset, out v) ? v : 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset == 0x20)
            {
                var b = (byte)value;
                // MCU read = dummy write of its last TX byte (FUN_08103838 resends DAT_2001deee). A different byte,
                // or any byte while a frame/raw write is in progress, is a real write (FUN_0810a674).
                bool reading = rx.Count > 0 && b == lastTx && inbuf.Count == 0 && raw.Count == 0;
                lastTx = b;
                if(reading)
                {
                    rxd = rx.Dequeue();                 // MCU reading: TX byte is a dummy
                    if(rx.Count == 0) { Ready(false); }
                }
                else
                {
                    rxd = 0xFF;
                    Assemble(b);
                }
                return;
            }
            if(offset == 0x00 && (value & 1) != 0 && !started)
            {
                started = true;
                foreach(var m in Startup) { Queue(m, "startup"); }
                Queue(new byte[] { 0, 0x17, (byte)BootStatus }, "startup");
            }
            if(offset != 0x18) { regs[offset] = value; }
        }

        private void Assemble(byte b)
        {
            if(raw.Count > 0 || (inbuf.Count == 0 && b == 0x74))
            {
                raw.Add(b);                                   // raw config write (not a 84 A9 61 frame)
                if(raw.Count == 2 && b != 0xA9) { raw.Clear(); return; }
                if(raw.Count == 6)
                {
                    Log("MCU> raw " + Str(raw.ToArray()) + "  -> boot status 00");
                    raw.Clear();
                    BootStatus = 0;
                }
                return;
            }
            if(inbuf.Count == 0 && b != 0x84) { return; }
            inbuf.Add(b);
            if((inbuf.Count == 2 && b != 0xA9) || (inbuf.Count == 3 && b != 0x61)) { inbuf.Clear(); return; }
            if(inbuf.Count >= 6)
            {
                int n = (inbuf[3] << 8) | inbuf[4];
                if(inbuf.Count >= 6 + n + (n & 1))
                {
                    var f = inbuf.ToArray();
                    inbuf.Clear();
                    Log("MCU> " + Str(f));
                    if(f.Length > 5 && f[5] >= 0x80) { Vocoder(f[5], f.Skip(6).Take(n).ToArray()); return; }
                    int id = f.Length > 6 ? f[6] : 0;
                    byte[] data;
                    var reply = new List<byte> { f[5], (byte)id };
                    reply.AddRange(Rules.TryGetValue(id, out data) ? data : new byte[] { 0 });
                    Queue(reply.ToArray(), "reply");
                    if(Events.TryGetValue(id, out data))
                    {
                        pending.RemoveAll(e => e.Value == data);    // MCU repeats the command: restart the timer
                        pending.Add(new KeyValuePair<int, byte[]>(EventDelayMs, data));
                    }
                }
            }
        }

        public string VocoderProdId { get; set; } = "SCT3288-EMU";
        public string VocoderVersion { get; set; } = "EMU-FAKE-VOCODER-1";
        public int VocoderBits { get; private set; } = 72;

        // Fake AMBE vocoder. Packet = type + fields (AMBE-3000 layout, big endian).
        private void Vocoder(byte type, byte[] d)
        {
            var o = new List<byte> { type };
            if(type == 0x82 && d.Length >= 2 && d[0] == 0x00)               // SPCHD: encode
            {
                int n = Math.Min(d[1], (d.Length - 2) / 2);
                // Frequency from interpolated zero-crossing times: (crossings-1) half periods over first..last span.
                int peak = 0, cross = 0, prev = 0;
                double first = 0, last = 0;
                for(int i = 0; i < n; i++)
                {
                    int v = (short)((d[2 + 2 * i] << 8) | d[3 + 2 * i]);
                    peak = Math.Max(peak, Math.Abs(v));
                    if(i > 0 && (prev < 0) != (v < 0))
                    {
                        double t = i - 1 + (double)prev / (prev - v);
                        if(cross == 0) { first = t; }
                        last = t; cross++;
                    }
                    prev = v;
                }
                double hz = cross > 1 && last > first ? (cross - 1) * 4000.0 / (last - first) : 0;
                int q = (int)Math.Min(65535, Math.Round(hz * 4));                // 0.25 Hz units
                var c = new byte[(VocoderBits + 7) / 8];
                c[0] = (byte)'F'; c[1] = (byte)(q >> 8); c[2] = (byte)q; c[3] = (byte)Math.Min(255, peak / 128);
                o[0] = 0x81; o.Add(0x01); o.Add((byte)VocoderBits); o.AddRange(c);
            }
            else if(type == 0x81 && d.Length >= 2 && d[0] == 0x01)          // CHAND: decode
            {
                var c = d.Skip(2).ToArray();
                bool ok = c.Length > 3 && c[0] == 'F';
                double hz = ok ? ((c[1] << 8) | c[2]) / 4.0 : 0, amp = ok ? c[3] * 128 : 0;
                o[0] = 0x82; o.Add(0x00); o.Add(160);
                for(int i = 0; i < 160; i++)
                {
                    phase += 2 * Math.PI * hz / 8000;
                    int v = (int)(amp * Math.Sin(phase));
                    o.Add((byte)(v >> 8)); o.Add((byte)v);
                }
                phase %= 2 * Math.PI;
            }
            else                                                             // control fields
            {
                for(int i = 0; i < d.Length; )
                {
                    byte fid = d[i++];
                    o.Add(fid);
                    switch(fid)
                    {
                    case 0x30: o.AddRange(System.Text.Encoding.ASCII.GetBytes(VocoderProdId)); o.Add(0); break;
                    case 0x31: o.AddRange(System.Text.Encoding.ASCII.GetBytes(VocoderVersion)); o.Add(0); break;
                    case 0x09: if(i < d.Length) { VocoderBits = d[i] == 34 ? 49 : 72; i++; } o.Add(0); break;
                    case 0x0A: i += 12; VocoderBits = 72; o.Add(0); break;
                    case 0x34: i += 6; o.Add(0); break;
                    case 0x05: i += 2; o.Add(0); break;
                    case 0x0B: case 0x10: case 0x32: i += 1; o.Add(0); break;
                    case 0x36: o.AddRange(new byte[] { 0, 0, 0, 0, 0, 0 }); break;
                    default: o.Add(0); i = d.Length; break;               // unknown: ack, skip rest
                    }
                }
            }
            Queue(o.ToArray(), "vocoder");
        }

        private void Tick()
        {
            if(!started) { return; }
            for(int i = 0; i < pending.Count; i++) { pending[i] = new KeyValuePair<int, byte[]>(pending[i].Key - 1, pending[i].Value); }
            if(rx.Count > 0) { Ready(true); return; }       // keep lines asserted (port resets, pulls)
            if(inbuf.Count > 0 || raw.Count > 0) { return; }  // MCU mid-write: don't start a message
            int due = pending.FindIndex(e => e.Key <= 0);
            if(due >= 0) { var m = pending[due].Value; pending.RemoveAt(due); Queue(m, "event"); return; }
            if(++sinceSent >= PeriodMs)
            {
                foreach(var m in Periodic) { Queue(m, null); }
                Queue(new byte[] { 0, 0x17, (byte)BootStatus }, null);
            }
        }

        private void Queue(byte[] msg, string why)
        {
            int n = msg.Length - 1;
            var f = new List<byte> { 0x84, 0xA9, 0x61, (byte)(n >> 8), (byte)n };
            f.AddRange(msg);
            if((n & 1) != 0) { f.Add(0); }
            foreach(var b in f) { rx.Enqueue(b); }
            sinceSent = 0;
            if(why != null) { Log("BB < " + Str(f.ToArray()) + "  (" + why + ")"); }
            Ready(true);
        }

        private void Ready(bool level)
        {
            IPeripheral p;
            if(port == null && machine.TryGetByName("sysbus.gpioPortD", out p)) { port = p as IGPIOReceiver; }
            port?.OnGPIO(11, level);
            port?.OnGPIO(7, level);
        }

        private void Log(string s)
        {
            if(string.IsNullOrEmpty(logPath)) { return; }
            try { File.AppendAllText(logPath, s + "\n"); } catch(Exception) { }
        }

        private static byte[] Hex(string s) { return s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray(); }
        private static string Str(byte[] b) { return string.Join(" ", b.Select(x => x.ToString("X2"))); }

        private readonly IMachine machine;
        private readonly string logPath;
        private readonly Dictionary<long, uint> regs = new Dictionary<long, uint>();
        private readonly Queue<byte> rx = new Queue<byte>();
        private readonly List<byte> inbuf = new List<byte>();
        private readonly List<byte> raw = new List<byte>();
        private readonly List<KeyValuePair<int, byte[]>> pending = new List<KeyValuePair<int, byte[]>>();
        private IGPIOReceiver port;
        private bool started;
        private int sinceSent;
        private uint rxd;
        private byte lastTx;
        private double phase;
    }
}

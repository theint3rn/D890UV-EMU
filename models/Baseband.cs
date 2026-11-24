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
//   * voice path stand-in (VOCODER_IO_SET 3E, channel frames ID 01, PCM ID 00): see Voice() below.
//   * AMBE-3000-style packets to an external AMBE chip (ExtAmbe, default off) (B0 = packet type 0x80 control / 0x81 channel / 0x82 speech; stock V1.05 never
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
            loop = false; chanFmt = 0; micMuted = true; spkMuted = true; VoiceStop();
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
                    if(f.Length > 5 && f[5] >= 0x80) { if(ExtAmbe) { Vocoder(f[5], f.Skip(6).Take(n).ToArray()); } return; }
                    int id = f.Length > 6 ? f[6] : 0;
                    if(Voice(f[5], id, f.Skip(7).Take(n - 1).ToArray())) { return; }
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

        // ---- voice path stand-in (codec + vocoder on the chip), cfw/docs/SCT3288_VOCODER.md sections 2 and 4 ----
        // Follows what the stock firmware does: WORK_MODE 18 03 xx with loop 3 = vocoder loop, 3E = VOCODER_IO_SET,
        // ID 01 {24, 36 B} channel frames, ID 00 {00 A0, 320 B} PCM. It proves framing and flow control only: the
        // "codec" is a tone description ('F', freq * 4, amp) in 49 bits inside the real AMBE+2 72-bit FEC.
        public int VoiceAnswer { get; set; } = 1;          // 1 answer, 0 silent chip, 2 refuse (status 01)
        public bool ExtAmbe { get; set; }                  // external AMBE packets (TYPE 0x80..0x82) answered?
        public double MicHz { get; set; }                  // microphone stand-in: tone, 0 = silence
        public int MicAmp { get; set; } = 8192;
        public bool MicMuted { get { return micMuted; } }
        public string VoiceStatus()
        {
            return string.Format("io {0:X2} loop {1} fmt {2:X2} dec frames {3} bad {4} speaker {5} samples {6:F1} Hz peak {7} enc frames {8} pcm in {9} gaps {10} mic {11} spk {12}",
                ioSet, loop, chanFmt, decFrames, decBad, spkSamples, spkHz, spkPeak, encFrames, pcmIn, gaps, micMuted ? "off" : "on", spkMuted ? "off" : "on");
        }
        public void SpeakerFile(string path) { spkPath = path; File.WriteAllBytes(path, new byte[0]); }

        private bool Voice(byte b0, int id, byte[] d)     // true = handled here
        {
            if(b0 >= 0x40 || (b0 & 0x20) != 0)
            {
                if(b0 == 0x40 && d.Length == 1 && id == 0x0F) { micMuted = (d[0] & 0x20) != 0; }
                if(b0 == 0x40 && d.Length == 1 && id == 0x08) { spkMuted = (d[0] & 0x1F) == 0x1F; }
                return false;
            }
            if(VoiceAnswer == 0 && (id == 0x3E || id == 0x01 || id == 0x00)) { return true; }
            switch(id)
            {
            case 0x18:
                if(d.Length >= 2) { loop = d[0] == 3 && (d[1] & 0x0F) == 3; if(!loop) { VoiceStop(); } }
                return false;
            case 0x3E:
                if(d.Length == 1) { ioSet = VoiceAnswer == 2 ? (byte)0 : d[0]; VoiceStop(); ioSet = VoiceAnswer == 2 ? (byte)0 : d[0]; }
                if(VoiceAnswer == 2) { Queue(new byte[] { b0, 0x3E, 1 }, "refused"); return true; }
                return false;
            case 0x01:
                if(d.Length == 2 && d[0] == 0xFF) { chanFmt = d[1]; return false; }
                if(d.Length >= 37 && d[0] == 0x24 && loop && (ioSet & 0x08) != 0)
                {
                    for(int i = 0; i < 4; i++) { decQ.Enqueue(d.Skip(1 + 9 * i).Take(9).ToArray()); }
                    chanAck = true;
                    return true;
                }
                return false;
            case 0x00:
                if(d.Length >= 322 && d[0] == 0 && d[1] == 0xA0 && loop && (ioSet & 0x80) != 0)
                {
                    for(int i = 0; i < 160; i++) { pcmQ.Enqueue((short)((d[2 + 2 * i] << 8) | d[3 + 2 * i])); }
                    pcmIn++;
                    pcmAck = true;
                    return true;
                }
                return false;
            default: return false;
            }
        }

        private void VoiceStop() { ioSet = 0; decQ.Clear(); pcmQ.Clear(); encBuf.Clear(); outbox.Clear(); chanAck = pcmAck = false; primed = false; }

        private void VoiceTick()                           // every ms
        {
            if(!loop || ioSet == 0) { voiceMs = 0; return; }
            if(chanAck && decQ.Count <= 4) { chanAck = false; outbox.Add(new byte[] { 0, 0x01, 0 }); }
            if(pcmAck && pcmQ.Count <= 320) { pcmAck = false; outbox.Add(new byte[] { 0, 0x00, 0 }); }
            if(++voiceMs < 20) { return; }
            voiceMs = 0;
            var mic = new short[160];
            for(int i = 0; i < 160; i++)
            {
                micPhase += 2 * Math.PI * MicHz / 8000;
                mic[i] = micMuted ? (short)0 : (short)(MicAmp * Math.Sin(micPhase));
            }
            micPhase %= 2 * Math.PI;
            if((ioSet & 0x08) != 0)
            {
                var pcm = new short[160];
                bool have = decQ.Count > 0;
                if(have) { pcm = Decode49(decQ.Dequeue()); decFrames++; primed = true; }
                else if(primed) { gaps++; primed = false; }
                if(have) { Speaker(pcm); if((ioSet & 0x02) != 0) { outbox.Add(Pcm(pcm)); } }
            }
            short[] src = null;
            if((ioSet & 0x80) != 0)
            {
                src = new short[160];
                bool full = pcmQ.Count >= 160;
                if(full) { for(int i = 0; i < 160; i++) { src[i] = pcmQ.Dequeue(); } primed = true; }
                else if(primed) { gaps++; primed = false; }
                if((ioSet & 0x70) == 0 && full) { Speaker(src); }
            }
            if((ioSet & 0x10) != 0)
            {
                encBuf.AddRange(Encode72(src ?? mic));
                encFrames++;
                if(encBuf.Count >= 36)
                {
                    var m = new List<byte> { 0, 0x01, 0x24 };
                    m.AddRange(encBuf);
                    encBuf.Clear();
                    outbox.Add(m.ToArray());
                }
            }
            if((ioSet & 0x01) != 0) { outbox.Add(Pcm(mic)); }
        }

        private static byte[] Pcm(short[] s)
        {
            var m = new List<byte> { 0, 0x00, 0x00, 0xA0 };
            foreach(var v in s) { m.Add((byte)(v >> 8)); m.Add((byte)v); }
            return m.ToArray();
        }

        private void Speaker(short[] s)
        {
            if(spkMuted) { return; }
            spkSamples += s.Length;
            double hz; int peak;
            Measure(s, out hz, out peak);
            spkHz = hz; spkPeak = peak;
            if(!string.IsNullOrEmpty(spkPath))
            {
                var b = new byte[2 * s.Length];
                for(int i = 0; i < s.Length; i++) { b[2 * i] = (byte)s[i]; b[2 * i + 1] = (byte)(s[i] >> 8); }
                try { using(var f = new FileStream(spkPath, FileMode.Append)) { f.Write(b, 0, b.Length); } } catch(Exception) { }
            }
        }

        private static void Measure(short[] s, out double hz, out int peak)
        {
            int cross = 0, prev = 0;
            double first = 0, last = 0;
            peak = 0;
            for(int i = 0; i < s.Length; i++)
            {
                int v = s[i];
                peak = Math.Max(peak, Math.Abs(v));
                if(i > 0 && (prev < 0) != (v < 0))
                {
                    double t = i - 1 + (double)prev / (prev - v);
                    if(cross == 0) { first = t; }
                    last = t; cross++;
                }
                prev = v;
            }
            hz = cross > 1 && last > first ? (cross - 1) * 4000.0 / (last - first) : 0;
        }

        // 49-bit payload 'F' freq16 (0.25 Hz) amp8 in the AMBE+2 3600x2450 frame (cfw proto/dmr_nxdn/common/ambe_fec.c)
        private byte[] Encode72(short[] s)
        {
            double hz; int peak;
            Measure(s, out hz, out peak);
            int q = (int)Math.Min(65535, Math.Round(hz * 4));
            var d = new byte[] { (byte)'F', (byte)(q >> 8), (byte)q, (byte)Math.Min(255, peak / 128), 0, 0, 0 };
            if((chanFmt & 0x04) != 0) { return d.Concat(new byte[] { 0, 0 }).ToArray(); }
            uint u0 = Field(d, 0, 12), u1 = Field(d, 12, 12), u2 = Field(d, 24, 11), u3 = Field(d, 35, 14);
            uint c0 = Golay(u0);
            var fr = new uint[] { (c0 << 1) | (uint)(Pop(c0) & 1), Golay(u1) ^ Pn(u0), u2, u3 };
            var o = new byte[9];
            for(int i = 0; i < 36; i++)
            {
                if(((fr[W[i]] >> X[i]) & 1) != 0) { o[(2 * i) >> 3] |= (byte)(0x80 >> ((2 * i) & 7)); }
                if(((fr[Y[i]] >> Z[i]) & 1) != 0) { o[(2 * i + 1) >> 3] |= (byte)(0x80 >> ((2 * i + 1) & 7)); }
            }
            return o;
        }

        private short[] Decode49(byte[] f)
        {
            byte[] d = f;
            if((chanFmt & 0x04) == 0)
            {
                var fr = new uint[4];
                for(int i = 0; i < 36; i++)
                {
                    if((f[(2 * i) >> 3] & (0x80 >> ((2 * i) & 7))) != 0) { fr[W[i]] |= 1u << X[i]; }
                    if((f[(2 * i + 1) >> 3] & (0x80 >> ((2 * i + 1) & 7))) != 0) { fr[Y[i]] |= 1u << Z[i]; }
                }
                uint u0 = (fr[0] >> 12) & 0xFFF, c1 = fr[1] ^ Pn(u0), u1 = (c1 >> 11) & 0xFFF;
                if(Golay(u0) != fr[0] >> 1 || Golay(u1) != c1) { decBad++; }
                ulong v = ((ulong)u0 << 37) | ((ulong)u1 << 25) | ((ulong)(fr[2] & 0x7FF) << 14) | (fr[3] & 0x3FFF);
                v <<= 15;                                       // 49 bits, MSB first in 8 bytes
                d = new byte[7];
                for(int i = 0; i < 7; i++) { d[i] = (byte)(v >> (56 - 8 * i)); }
            }
            bool ok = d[0] == (byte)'F';
            double hz = ok ? ((d[1] << 8) | d[2]) / 4.0 : 0, amp = ok ? d[3] * 128 : 0;
            var pcm = new short[160];
            for(int i = 0; i < 160; i++) { spkPhase += 2 * Math.PI * hz / 8000; pcm[i] = (short)Math.Min(32767, amp * Math.Sin(spkPhase)); }
            spkPhase %= 2 * Math.PI;
            return pcm;
        }

        private static uint Field(byte[] d, int pos, int n)
        {
            uint v = 0;
            for(int i = pos; i < pos + n; i++) { v = (v << 1) | (uint)((d[i >> 3] >> (7 - (i & 7))) & 1); }
            return v;
        }
        private static int Pop(uint v) { int n = 0; while(v != 0) { v &= v - 1; n++; } return n; }
        private static uint Golay(uint d)
        {
            uint r = d << 11;
            for(int i = 22; i >= 11; i--) { if((r & (1u << i)) != 0) { r ^= 0xC75u << (i - 11); } }
            return (d << 11) | r;
        }
        private static uint Pn(uint u0)
        {
            uint pr = 16 * u0, m = 0;
            for(int k = 22; k >= 0; k--) { pr = (173 * pr + 13849) & 0xFFFF; if(pr >= 32768) { m |= 1u << k; } }
            return m;
        }
        private static readonly int[] W = { 0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,2,0,2,0,2,0,2,0,2,0,2,0,2 };
        private static readonly int[] X = { 23,10,22,9,21,8,20,7,19,6,18,5,17,4,16,3,15,2,14,1,13,0,12,10,11,9,10,8,9,7,8,6,7,5,6,4 };
        private static readonly int[] Y = { 0,2,0,2,0,2,0,2,0,3,0,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3 };
        private static readonly int[] Z = { 5,3,4,2,3,1,2,0,1,13,0,12,22,11,21,10,20,9,19,8,18,7,17,6,16,5,15,4,14,3,13,2,12,1,11,0 };

        private bool loop, chanAck, pcmAck, primed, micMuted, spkMuted;
        private byte ioSet, chanFmt;
        private int voiceMs, decFrames, decBad, encFrames, pcmIn, gaps, spkPeak;
        private long spkSamples;
        private double micPhase, spkPhase, spkHz;
        private string spkPath;
        private readonly Queue<byte[]> decQ = new Queue<byte[]>();
        private readonly Queue<short> pcmQ = new Queue<short>();
        private readonly List<byte> encBuf = new List<byte>();
        private readonly List<byte[]> outbox = new List<byte[]>();

        private void Tick()
        {
            if(!started) { return; }
            VoiceTick();
            for(int i = 0; i < pending.Count; i++) { pending[i] = new KeyValuePair<int, byte[]>(pending[i].Key - 1, pending[i].Value); }
            if(rx.Count > 0) { Ready(true); return; }       // keep lines asserted (port resets, pulls)
            if(inbuf.Count > 0 || raw.Count > 0) { return; }  // MCU mid-write: don't start a message
            if(outbox.Count > 0) { var m = outbox[0]; outbox.RemoveAt(0); Queue(m, "voice"); return; }
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

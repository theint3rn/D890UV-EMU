// Kioxia-style SPI-NAND (MID 0x98, DID 0xED: 4 Gbit, 4 KiB pages, 64 pages/block = 256 KiB),
// driven by GPIO bit-banging from the D890UV main MCU (see FUN_08037db8 / FUN_080377a4 / FUN_080379b4).
//
// GPIO inputs (connect MCU outputs here):  0 = CS# (PC5), 1 = SCK (PE15), 2 = MOSI / IO0 (PB10)
// GPIO outputs: IO1 -> PB8 (MISO), IO0 -> PB10 (only driven during x2 reads)
// SPI mode 0: commands sampled on SCK rising edge, data driven on SCK falling edge.
//
// Contents: sparse pages file (u32 address, 4096 bytes)*; unwritten = 0xFF. Writes/erases stay in
// memory; call SaveOverlay("path") from the monitor to persist them.
using System;
using System.Collections.Generic;
using System.IO;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890SpiNand : IDoubleWordPeripheral, IGPIOReceiver
    {
        public D890SpiNand(IMachine machine, string pagesFile = "", byte manufacturerId = 0x98, byte deviceId = 0xED)
        {
            this.machine = machine;
            mid = manufacturerId;
            did = deviceId;
            IO0 = new GPIO();
            IO1 = new GPIO();
            pages = new Dictionary<uint, byte[]>();
            if(!string.IsNullOrEmpty(pagesFile))
            {
                LoadPages(pagesFile);
            }
            Reset();
        }

        public bool Trace { get; set; }
        public GPIO IO0 { get; private set; }
        public GPIO IO1 { get; private set; }

        public long Size => 0x10;
        public long CommandCount { get; private set; }
        public long PageReads { get; private set; }
        public long PagePrograms { get; private set; }
        public long BlockErases { get; private set; }

        public void Reset()
        {
            cs = true; sck = false; mosi = false;
            cache = Blank();
            features = new byte[256];
            features[0xA0] = 0x00;   // block lock: all unlocked
            features[0xB0] = 0x10;   // ECC enabled
            features[0xC0] = 0x00;   // status: ready
            EndTransaction();
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case 0x0: return (uint)CommandCount;
                case 0x4: return (uint)PageReads;
                case 0x8: return (uint)PagePrograms;
                case 0xC: return (uint)BlockErases;
            }
            return 0;
        }

        public void WriteDoubleWord(long offset, uint value) { }

        public void OnGPIO(int number, bool value)
        {
            switch(number)
            {
            case 0:
                if(value && !cs) { EndTransaction(); }
                else if(!value && cs) { StartTransaction(); }
                cs = value;
                break;
            case 1:
                if(!cs)
                {
                    if(value && !sck) { Rising(); }
                    else if(!value && sck) { Falling(); }
                }
                sck = value;
                break;
            case 2:
                mosi = value;
                break;
            }
        }

        public string Peek(uint address, int count)
        {
            var sb = new System.Text.StringBuilder();
            for(int i = 0; i < count; i++)
            {
                var a = (uint)(address + i);
                byte[] p;
                sb.AppendFormat("{0:X2}", pages.TryGetValue(a & ~0xFFFu, out p) ? p[a & 0xFFF] : (byte)0xFF);
            }
            return sb.ToString();
        }

        public void SaveOverlay(string path)
        {
            using(var f = File.Create(path))
            {
                foreach(var kv in pages)
                {
                    f.Write(BitConverter.GetBytes(kv.Key), 0, 4);
                    f.Write(kv.Value, 0, PageSize);
                }
            }
        }

        public void LoadPages(string path)
        {
            var data = File.ReadAllBytes(path);
            for(int o = 0; o + 4 + PageSize <= data.Length; o += 4 + PageSize)
            {
                var addr = BitConverter.ToUInt32(data, o);
                var p = new byte[PageSize];
                Array.Copy(data, o + 4, p, 0, PageSize);
                pages[addr] = p;
            }
            this.Log(LogLevel.Info, "SPI-NAND: loaded {0} pages from {1}", pages.Count, path);
        }

        // ---- protocol ----
        private void StartTransaction()
        {
            bitCount = 0; shiftIn = 0; bytesIn.Clear(); outBits = null; dual = false;
        }

        private void EndTransaction()
        {
            if(Trace && outBits != null && outBits == cache && outIndex > outStart)
            {
                var n = Math.Min(outIndex - outStart, 8);
                var sb = new System.Text.StringBuilder();
                for(int i = 0; i < n; i++) { sb.AppendFormat("{0:X2}", OutByte(outStart + i)); }
                this.Log(LogLevel.Info, "NAND out col 0x{0:X} bytes {1} first {2} (partial bits {3})", outStart, outIndex - outStart, sb, outBit);
            }
            if(bytesIn.Count > 0)
            {
                Finish(bytesIn.ToArray());
            }
            bytesIn.Clear();
            outBits = null;
            dual = false;
            IO1.Unset();
        }

        private void Rising()
        {
            if(outBits != null && bytesIn.Count >= outStartsAfter)
            {
                return; // output phase: the MCU only clocks, nothing to sample
            }
            shiftIn = (byte)((shiftIn << 1) | (mosi ? 1 : 0));
            if(++bitCount == 8)
            {
                bitCount = 0;
                bytesIn.Add(shiftIn);
                shiftIn = 0;
                OnByte();
            }
        }

        private void Falling()
        {
            if(outBits == null || bytesIn.Count < outStartsAfter)
            {
                return;
            }
            byte b = OutByte(outIndex);
            if(dual)
            {
                int shift = 7 - 2 * outBit;          // IO1 carries bits 7,5,3,1; IO0 bits 6,4,2,0
                SetLine(1, ((b >> shift) & 1) != 0);
                SetLine(0, ((b >> (shift - 1)) & 1) != 0);
                if(++outBit == 4) { outBit = 0; outIndex++; }
            }
            else
            {
                SetLine(1, ((b >> (7 - outBit)) & 1) != 0);
                if(++outBit == 8) { outBit = 0; outIndex++; }
            }
        }

        // IO0 (PB10) is shared with the MCU's MOSI output; GPIO.Set() skips unchanged levels, so after
        // the MCU drives the pin our level would be lost. Drive the port input directly as well.
        private void SetLine(int io, bool level)
        {
            (io == 0 ? IO0 : IO1).Set(level);
            if(portB == null)
            {
                IPeripheral per;
                if(machine.TryGetByName("sysbus.gpioPortB", out per)) { portB = per as IGPIOReceiver; }
            }
            portB?.OnGPIO(io == 0 ? 10 : 8, level);
        }
        private IGPIOReceiver portB;

        private void OnByte()
        {
            CommandCount++;
            var c = bytesIn[0];
            int n = bytesIn.Count;
            switch(c)
            {
            case 0x9F: if(n == 2) { StartOutput(new[] { mid, did }, 2, false, 0); } break;  // cmd + dummy
            case 0x0F: if(n == 2) { StartOutput(new[] { features[bytesIn[1]] }, 2, false, 0); } break;
            case 0x03:
            case 0x0B: if(n == 4) { StartOutput(cache, 4, false, Col()); } break;
            case 0x3B: if(n == 4) { StartOutput(cache, 4, true, Col()); } break;
            }
        }

        private void Finish(byte[] b)
        {
            if(Trace)
            {
                var n = Math.Min(b.Length, 8);
                this.Log(LogLevel.Info, "NAND cmd {0} len {1}{2}", BitConverter.ToString(b, 0, n), b.Length,
                         (b[0] == 0x3B || b[0] == 0x03 || b[0] == 0x0B || b[0] == 0x0F || b[0] == 0x9F) ? " (read)" : "");
            }
            switch(b[0])
            {
            case 0x06: features[0xC0] |= 0x02; break;                 // WEL
            case 0x04: features[0xC0] &= 0xFD; break;
            case 0xFF: features[0xC0] = 0; cache = Blank(); break;
            case 0x1F: if(b.Length >= 3) { features[b[1]] = b[2]; } break;
            case 0x13:                                                 // page -> cache
                if(b.Length >= 4)
                {
                    var addr = Row(b) * PageSize;
                    cache = Blank();
                    byte[] p;
                    if(pages.TryGetValue(addr, out p)) { Array.Copy(p, cache, PageSize); }
                    PageReads++;
                }
                break;
            case 0x02:                                                 // program load (reset cache)
            case 0x84:                                                 // random program load
                if(b.Length >= 3)
                {
                    if(b[0] == 0x02) { cache = Blank(); }
                    int col = ((b[1] << 8) | b[2]) & 0x1FFF;
                    for(int i = 3; i < b.Length && col < cache.Length; i++, col++) { cache[col] = b[i]; }
                }
                break;
            case 0x10:                                                 // program execute
                if(b.Length >= 4)
                {
                    var addr = Row(b) * PageSize;
                    byte[] p;
                    if(!pages.TryGetValue(addr, out p)) { p = new byte[PageSize]; Fill(p); pages[addr] = p; }
                    for(int i = 0; i < PageSize; i++) { p[i] &= cache[i]; }  // NAND: program only clears bits
                    features[0xC0] &= 0xFD;
                    PagePrograms++;
                }
                break;
            case 0xD8:                                                 // block erase (64 pages)
                if(b.Length >= 4)
                {
                    var first = (Row(b) & ~63u) * PageSize;
                    for(uint i = 0; i < 64; i++) { pages.Remove(first + i * PageSize); }
                    features[0xC0] &= 0xFD;
                    BlockErases++;
                }
                break;
            }
        }

        private void StartOutput(byte[] src, int after, bool x2, int start)
        {
            outBits = src; outStartsAfter = after; dual = x2; outIndex = start; outStart = start; outBit = 0;
        }

        private byte OutByte(int i) { return outBits.Length == 0 ? (byte)0xFF : outBits[i % outBits.Length]; }
        private int Col() { return ((bytesIn[1] << 8) | bytesIn[2]) & 0x1FFF; }
        private static uint Row(byte[] b) { return (uint)(((b[1] & 1) << 16) | (b[2] << 8) | b[3]); }
        private static byte[] Blank() { var a = new byte[PageSize + SpareSize]; Fill(a); return a; }
        private static void Fill(byte[] a) { for(int i = 0; i < a.Length; i++) { a[i] = 0xFF; } }

        private const int PageSize = 4096;
        private const int SpareSize = 256;
        private readonly IMachine machine;
        private readonly byte mid, did;
        private readonly Dictionary<uint, byte[]> pages;
        private readonly List<byte> bytesIn = new List<byte>();
        private byte[] cache, features, outBits;
        private bool cs, sck, mosi, dual;
        private int bitCount, outStartsAfter, outIndex, outBit, outStart;
        private byte shiftIn;
    }
}

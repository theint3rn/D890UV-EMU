// Emulator-only reader of a firmware PCM ring (cfw hal/voice.c: int16_t g_emu_pcm[8192] at 8 kHz +
// volatile uint32_t g_emu_pcm_wr = total samples written). Every 10 ms of virtual time the samples written since
// the last poll are appended to a file (int16 LE mono), so a run keeps the whole decoded audio, not only the
// last second the ring holds. Not a device on the radio; it only reads RAM.
// Monitor: pcm Attach 0x<ring> 0x<wr> 8192 @out.raw   pcm Detach   pcm Status
// emu/run.py --pcm attaches it from the ELF symbols g_emu_pcm / g_emu_pcm_wr (file out/<run>/rx_pcm.raw).
using System;
using System.IO;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890PcmTap : IDoubleWordPeripheral
    {
        public D890PcmTap(IMachine machine)
        {
            this.machine = machine;
            machine.ClockSource.AddClockEntry(new ClockEntry(1, 100, Poll, this, "pcmtap"));
        }

        public long Size => 0x10;
        public void Reset() { }
        public uint ReadDoubleWord(long offset) { return 0; }
        public void WriteDoubleWord(long offset, uint value) { }

        public void Attach(ulong ring, ulong wr, int len, string path)
        {
            Detach();
            this.ring = ring; this.wrAddr = wr; this.len = Math.Max(1, len);
            file = new BinaryWriter(File.Open(path, FileMode.Create));
            name = path;
            last = machine.SystemBus.ReadDoubleWord(wrAddr);
            total = 0;
        }

        public void Detach()
        {
            if(file != null) { file.Close(); file = null; }
        }

        public string Status()
        {
            return string.Format("{0}: ring 0x{1:X8} x {2}, wr 0x{3:X8}, {4} samples saved, lost {5}",
                file != null ? name : "detached", ring, len, wrAddr, total, lost);
        }

        private void Poll()
        {
            if(file == null) { return; }
            var bus = machine.SystemBus;
            uint wr = bus.ReadDoubleWord(wrAddr);
            uint n = wr - last;
            if(n == 0) { return; }
            if(n > (uint)len) { lost += n - (uint)len; last = wr - (uint)len; n = (uint)len; }
            for(uint i = 0; i < n; i++)
            {
                uint k = (last + i) % (uint)len;
                file.Write((short)bus.ReadWord(ring + 2 * k));
            }
            total += n;
            last = wr;
            file.Flush();
        }

        private readonly IMachine machine;
        private BinaryWriter file;
        private string name;
        private ulong ring, wrAddr;
        private int len = 8192;
        private uint last;
        private long total, lost;
    }
}

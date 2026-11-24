// JieLi Bluetooth module of the D890UV on UART4 (behavioural).
// Frames both ways: A5 5A cmd len data[len] 06   (MCU parser FUN_080c92a8 / FUN_080c93a0, sender FUN_0810bbd8)
// MCU hello: cmd 02 "D890" every 100 ms, 25 tries (FUN_0801ee18). Module answers cmd 01 status:
//   status 02 + 4 version bytes ("1024" = BT firmware V1024) -> MCU marks the module present (DAT_20004c1c=1).
// Every other MCU frame cmd X is acked with cmd 01 data X (FUN_08025c98 clears bit X of the wait mask
// 0x20005030; the MCU resends while the bit stays set, FUN_08133cf4). Frames are logged (env D890_BT_LOG). Text commands "AT+BT=0/1" are ignored.
// Monitor: bt Send "01 01 03"  (cmd len... without A5 5A/06 framing: cmd data...),  bt Present false
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.UART;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890Bluetooth : IDoubleWordPeripheral
    {
        public D890Bluetooth(IMachine machine, string uart = "sysbus.uart4", string version = "1024")
        {
            this.machine = machine;
            uartName = uart;
            Version = version;
            Present = true;
            logPath = Environment.GetEnvironmentVariable("D890_BT_LOG");
            machine.ClockSource.AddClockEntry(new ClockEntry(1, 1000, Tick, this, "bt"));
        }

        public long Size => 0x10;
        public bool Present { get; set; }
        public string Version { get; set; }

        public void Reset() { rx.Clear(); }
        public uint ReadDoubleWord(long offset) { return 0; }
        public void WriteDoubleWord(long offset, uint value) { }

        // "cmd data..." hex -> framed and sent to the MCU
        public void Send(string hex)
        {
            var b = hex.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray();
            Frame(b[0], b.Skip(1).ToArray());
        }

        private void Tick()
        {
            if(uart != null) { return; }
            IPeripheral p;
            if(!machine.TryGetByName(uartName, out p)) { return; }
            uart = p as IUART;
            if(uart != null) { uart.CharReceived += OnByte; }
        }

        private void OnByte(byte b)
        {
            rx.Add(b);
            if(rx[0] != 0xA5) { rx.Clear(); return; }
            if(rx.Count == 2 && b != 0x5A) { rx.Clear(); return; }
            if(rx.Count < 4 || rx.Count < rx[3] + 5) { return; }
            var f = rx.ToArray();
            rx.Clear();
            Log("MCU> " + Str(f));
            if(!Present) { return; }
            if(f[2] == 0x02)                                         // hello "D890" -> status 02 + version
            {
                Frame(0x01, new byte[] { 0x02 }.Concat(System.Text.Encoding.ASCII.GetBytes(Version.PadRight(4).Substring(0, 4))).ToArray());
            }
            else
            {
                Frame(0x01, new byte[] { f[2] });                    // ack: clears the MCU's wait bit for cmd
            }
        }

        private void Frame(byte cmd, byte[] data)
        {
            var f = new List<byte> { 0xA5, 0x5A, cmd, (byte)data.Length };
            f.AddRange(data);
            f.Add(0x06);
            Log("BT < " + Str(f.ToArray()));
            foreach(var x in f) { uart?.WriteChar(x); }
        }

        private void Log(string s)
        {
            if(string.IsNullOrEmpty(logPath)) { return; }
            try { File.AppendAllText(logPath, s + "\n"); } catch(Exception) { }
        }

        private static string Str(byte[] b) { return string.Join(" ", b.Select(x => x.ToString("X2"))); }

        private readonly IMachine machine;
        private readonly string uartName;
        private readonly string logPath;
        private readonly List<byte> rx = new List<byte>();
        private IUART uart;
    }
}

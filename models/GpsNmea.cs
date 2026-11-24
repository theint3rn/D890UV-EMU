// GPS receiver for the D890UV: NMEA-0183 on USART2 (MCU RX), one fix per second of virtual time.
// Sends GNRMC, GPGGA, GNGSA, GPGSV with the position/time set below; time follows virtual time from
// StartUtc. Monitor: gps Position 32.7157 -117.1611 20   gps Fix false   gps Enabled false   gps Raw "$GNTXT,..."
using System;
using System.Globalization;
using System.Text;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.UART;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890Gps : IDoubleWordPeripheral
    {
        public D890Gps(IMachine machine, string uart = "sysbus.usart2")
        {
            this.machine = machine;
            uartName = uart;
            Enabled = true; Fix = true;
            Lat = 32.7157; Lon = -117.1611; Alt = 20;
            StartUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            machine.ClockSource.AddClockEntry(new ClockEntry(1, 1, Tick, this, "gps"));
        }

        public long Size => 0x10;
        public bool Enabled { get; set; }
        public bool Fix { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double Alt { get; set; }
        public DateTime StartUtc { get; set; }

        public void Reset() { seconds = 0; }
        public uint ReadDoubleWord(long offset) { return 0; }
        public void WriteDoubleWord(long offset, uint value) { }
        public void Position(double lat, double lon, double alt) { Lat = lat; Lon = lon; Alt = alt; }
        public void Raw(string sentence) { Send(sentence.StartsWith("$") ? Checksum(sentence.Substring(1)) : sentence); }

        private void Tick()
        {
            seconds++;
            if(!Enabled) { return; }
            var t = StartUtc.AddSeconds(seconds);
            string hms = t.ToString("HHmmss.00", Inv), dmy = t.ToString("ddMMyy", Inv);
            string lat = Deg(Math.Abs(Lat), 2) + "," + (Lat >= 0 ? "N" : "S");
            string lon = Deg(Math.Abs(Lon), 3) + "," + (Lon >= 0 ? "E" : "W");
            if(Fix)
            {
                Send(Checksum("GNRMC," + hms + ",A," + lat + "," + lon + ",0.00,0.00," + dmy + ",,,A"));
                Send(Checksum("GPGGA," + hms + "," + lat + "," + lon + ",1,08,1.0," + Alt.ToString("0.0", Inv) + ",M,0.0,M,,"));
                Send(Checksum("GNGSA,A,3,01,03,06,09,12,17,19,23,,,,,1.8,1.0,1.5"));
            }
            else
            {
                Send(Checksum("GNRMC," + hms + ",V,,,,,,," + dmy + ",,,N"));
                Send(Checksum("GPGGA," + hms + ",,,,,0,00,99.9,,,,,,"));
            }
            Send(Checksum("GPGSV,2,1,08,01,45,050,40,03,30,120,38,06,60,200,42,09,20,300,35"));
            Send(Checksum("GPGSV,2,2,08,12,15,030,33,17,70,090,44,19,25,250,36,23,10,330,30"));
        }

        private static string Deg(double v, int degDigits)
        {
            int d = (int)v; double m = (v - d) * 60;
            return d.ToString(new string('0', degDigits), Inv) + m.ToString("00.0000", Inv);
        }

        private static string Checksum(string body)
        {
            byte c = 0;
            foreach(var ch in body) { c ^= (byte)ch; }
            return "$" + body + "*" + c.ToString("X2");
        }

        private void Send(string s)
        {
            if(uart == null)
            {
                IPeripheral p;
                if(!machine.TryGetByName(uartName, out p)) { return; }
                uart = p as IUART;
            }
            foreach(var b in Encoding.ASCII.GetBytes(s + "\r\n")) { uart?.WriteChar(b); }
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly IMachine machine;
        private readonly string uartName;
        private IUART uart;
        private long seconds;
    }
}

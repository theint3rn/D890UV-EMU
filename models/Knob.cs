// D890UV channel knob: quadrature encoder on PC8 (A) / PC9 (B), read in the EXTI8/EXTI9 interrupts
// (IRQ19_Handler 0x08030a4c, IRQ20_Handler 0x08030b54, both edges). Each handler samples A and B; after two
// edges FUN_080c85cc decodes a detent (00 -> 11 or 11 -> 00) and posts key code 0x11 or 0x12
// (FUN_0811ca50 / FUN_0801c380). The platform's EXTI is a register stub, so this model raises the NVIC lines
// itself (ExtiA -> nvic@19, ExtiB -> nvic@20) and drives the pin levels.
// Monitor: knob Turn 1   (one detent; negative = other way)   knob Turn -3
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class D890Knob : IDoubleWordPeripheral
    {
        public D890Knob(IMachine machine, string port = "sysbus.gpioPortC", int pinA = 8, int pinB = 9)
        {
            this.machine = machine;
            portName = port; this.pinA = pinA; this.pinB = pinB;
            ExtiA = new GPIO(); ExtiB = new GPIO();
            machine.ClockSource.AddClockEntry(new ClockEntry(1, 1000, Tick, this, "knob"));
        }

        public GPIO ExtiA { get; }
        public GPIO ExtiB { get; }
        public long Size => 0x10;

        public void Reset() { steps.Clear(); a = false; b = false; }
        public uint ReadDoubleWord(long offset) { return 0; }
        public void WriteDoubleWord(long offset, uint value) { }

        // positive = clockwise (A leads), negative = counter-clockwise (B leads)
        public void Turn(int detents)
        {
            for(int i = 0; i < Math.Abs(detents); i++)
            {
                bool cur = steps.Count > 0 ? steps[steps.Count - 1].Item2 : a;
                bool level = !cur;                     // detent: both lines go to the other level
                bool aFirst = detents > 0;
                steps.Add(Tuple.Create(aFirst ? 0 : 1, level));
                steps.Add(Tuple.Create(aFirst ? 1 : 0, level));
            }
        }

        private void Tick()
        {
            if(steps.Count == 0) { return; }
            if(++wait < 3) { return; }                   // 3 ms between edges: the handler runs in between
            wait = 0;
            var s = steps[0];
            steps.RemoveAt(0);
            if(s.Item1 == 0) { a = s.Item2; } else { b = s.Item2; }
            Drive(s.Item1 == 0 ? pinA : pinB, s.Item2);
            (s.Item1 == 0 ? ExtiA : ExtiB).Blink();          // NVIC lines are level: one short pulse = one interrupt
        }

        private void Drive(int pin, bool level)
        {
            if(gpio == null)
            {
                IPeripheral p;
                if(!machine.TryGetByName(portName, out p)) { return; }
                gpio = p as IGPIOReceiver;
            }
            gpio?.OnGPIO(pin, level);
        }

        private readonly IMachine machine;
        private readonly string portName;
        private readonly int pinA, pinB;
        private readonly List<Tuple<int, bool>> steps = new List<Tuple<int, bool>>();
        private IGPIOReceiver gpio;
        private bool a, b;
        private int wait;
    }
}

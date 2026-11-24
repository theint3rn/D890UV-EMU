# D890UV emulator (Renode 1.17)

Runs the main MCU firmware on an emulated STM32U575 plus models of the radio's board chips: the original V1.05 image or the lifted build. Use it to test changes before they touch hardware.

## Run

```sh
python3 emu/run.py --orig --seconds 15                  # original V1.05 .CDD
python3 emu/run.py --elf build/d890uv.elf --seconds 15  # lifted build
python3 emu/compare.py --seconds 15                     # both, same inputs, screens compared
python3 emu/run.py --orig --seconds 8 --then "0|lcd Press 2 150" --then "0.5|shot"   # press key, screenshot
python3 emu/run.py --elf build/d890uv.elf --gdb 3333    # wait for GDB (arm-none-eabi-gdb / gdb-multiarch)
python3 emu/run.py --orig --samples 100                 # PC samples -> pc_samples.txt (emu/pcprof.py summarises)
python3 emu/run.py --orig --seconds 15 --then "0|rf0 Status" --then "0|sysbus ReadDoubleWord 0x2000A479"
```
`--uart-socket usart1:3470` (repeatable; also `webui.py --launch … --uart-socket …`) exposes a UART as a raw TCP
server on localhost, e.g. USART1 = programming cable for a PC CPS; the `usart*.txt` capture keeps working.
`--then` steps run in order after the main run; each command's monitor output is printed at the end (also in
`out/<name>/then_out.txt`, via `then_cmd.py`: headless Renode shows no monitor output). `shot` = screenshot.

### Front panel in a browser (try the radio by hand)

```sh
python3 emu/webui.py --launch orig      # boots V1.05 (~1.5 min), then open http://localhost:8890
python3 emu/webui.py --launch elf       # same with the lifted build/d890uv.elf
python3 emu/webui.py --launch cfw       # custom firmware (builds cfw/ first), see "Custom firmware" below
python3 emu/webui.py --attach 1235      # use a Renode already started with run.py --serve 1235
```
Laid out like the radio: PTT/PF1/PF2 on the left side, PF3 + channel knob + volume/power knob on top.
LCD, keypad, side keys, the strings the firmware draws, Pause/Reset/Save shot, and a box for Renode
monitor commands (`gps Fix false`, `analog Battery 6.0`, `rtc SetTime "2026-05-01 10:00:00"`, `bt Present false`).
Hold a button for a long press. PC keys: 0-9 `*` `#`, Enter = Menu, Esc/Backspace = Exit, arrows = Up/Down,
F1/F2 = P1/P2. Runs in 50 ms slices, paced to real time. In the desktop app: `.claude/launch.json`
(`d890uv-orig` :8890, `d890uv-lifted` :8891, `d890uv-cfw` :8892). Each launch has its own out dir (`out/ui`,
`out/ui_elf`, `out/ui_cfw`), so panels can run side by side. The text pane hooks the firmware's string renderer:
`--text-fn ADDR|SYMBOL|none`, `--text-abi utf16sp|cstr` (defaults by `--launch`).

### Menu crawl

`python3 emu/crawl_all.py --items 0-12 --jobs 3` crawls each main-menu item in a fresh emulator
(`out/crawl2/item_NN/`: `crawl.log`, `tree_N.json`, `shots/`, `text.log`). One long crawl drifts: the crawler presses
Select on option lists, which changes settings and even the main-menu order. In a fresh V1.05 radio with the
K6RRR codeplug the menu order is: 0 Talk Group, 1 Messages, 2 Call Log, 3 Zone, 4 Scan, 5 Roaming, 6 Settings,
7 Record, 8 NOAA, 9 GPS, 10 Satellite, 11 Digi Moni, 12 APRS. Screens with a running clock (GPS info) are compared by
their text with digits masked, not by frame hash.

Expected (not emulator bugs): "List Empty!"/"Inbox Empty!" (fresh radio); "Satellite Data Error!" = no satellite
records in NAND (`FUN_0813dbf4` reads 12 × 0x78-byte entries via `FUN_080dfa5c` from NAND 0x04A80000, B copy
0x04AC0000; the test codeplug has none); "GPS Not Turn On" = GPS is off in the codeplug.
GPS verified: Menu > GPS > GPS On/Off = On, after 5 valid RMC sentences (5 s; `FUN_08045838`, fix flag
`DAT_20005b28`) GPS Info shows the `gps` model position (32.715700N 117.161100W, DM12KR).

### Speedups (default; `run.py --exact` turns them off)

- The microsecond busy-wait `FUN_0811ece0(us)` polls SysTick VAL. Each poll is a slow MMIO exit in Renode
  (99% of peripheral accesses). A CPU hook at its entry calls `SkipTime(us)` and returns.
- The OS idle task `FUN_080c18d4` spins. Its call to `FUN_080c0318` (at 0x080C18FA) is redirected to a cave
  in free flash, 0x081E3F00: `wfi; b.w FUN_080c0318` (`images/idle_cave.bin`, `idle_call.bin`). Original image only.
- Lifted build: the lifter inserts `wfi` before the idle hook call in `FUN_080c18d4` (`BODY_PATCHES` in
  `tools/lift.py`), so the lifted image sleeps in idle too. Before, the lifted panel ran ~1.8x slower than orig
  (idle task spinning was 100% of PC samples). `-Og` was tried and breaks the firmware (UART4 IRQ storm, RTOS never
  starts: non-volatile hardware accesses get optimised), so the build stays `-O0`; its code is ~1.5x more
  instructions than Keil's, which only shows during boot.
- Result: idle main screen ~3x real time (was ~0.2x); boot to main screen ~90 host s. Screens unchanged.

### Interactive session (boot once, then drive it)

```sh
python3 emu/run.py --orig --seconds 15 --name serve --serve 1235   # Renode in background + broker on 1236
python3 emu/ctl.py ks 13,16 shot1          # press Menu, Down; screenshot -> out/serve/shot1.png
python3 emu/ctl.py run 30                  # run 30 virtual seconds
python3 emu/ctl.py cmd "sysbus ReadByte 0x20007cd0" "analog Battery 6.0"
python3 emu/ctl.py state                   # PC/fault/OS state
python3 emu/hooktrace.py out/serve/h.log 0x08016978 --mem 0x20002a88 -- "lcd Press 13 150" 'emulation RunFor "00:00:01"'
python3 emu/taskdump.py                    # each uC/OS task's saved stack (return addresses)
python3 emu/crawl.py --depth 4             # walk every menu -> out/crawl/{tree.json,crawl.log,shots/}
python3 emu/montage.py out.png a=x.png b=y.png   # tile screenshots
python3 emu/ctl.py quit
```
Renode's telnet monitor only serves its first client reliably, so `ctl.py broker` (started by `--serve`)
holds that connection and every `ctl.py` call goes through it.
`crawl.py` reads screen text from a CPU hook on the firmware's string renderer `FUN_0808d894`
(r0 = x, r1 = y, [sp+4] = UTF-16 string).

Each run writes `emu/out/<name>/`:

| File | What |
|---|---|
| `lcd.png` (+ `lcd_NN.png` per `shot`) | Screen, 160×128, as on the radio |
| `state.txt` | PC/SP/LR, fault registers, uC/OS markers, `g_lift_trap_address` |
| `renode.log` | Warnings incl. accesses to unmodelled addresses; NAND/RF traces when enabled |
| `usart*.txt` | Bytes the firmware sent on each UART |
| `pc_samples.txt` | With `--samples`: time, PC, LR per sample |
| `nand.pages` | With `--save-nand`: NAND contents at the end |
| `run.resc` | The exact Renode script, for replaying by hand |

## Images

`run.py` picks the NAND image: `--nand FILE`, else `images/extflash.pages` if present, else `images/synth.pages`
(generated by `make_nand.py` when missing). Only our own files are needed for custom firmware (cfw) runs.

| File | Content | Source | Needed for |
|---|---|---|---|
| `images/synth.pages` | synthetic NAND: block-0 bad-block table + `0x1FF00` "CHECK 4G BLOCK FOR VALID.", `0x7B000` 10-byte check value (0x55..0x5E, else "UPDATE MODE"), `0x04EC0000` "CHECKING DATA FOR D890UV", default RF calibration (`0x04F00000`, from `cfw/drivers/cal_default.c`), 8 made-up channels "TEST 1".."TEST 8" (FM + DMR, bank 0 at `0x01000000` in the stock CPS layout) | `make_nand.py` (ours) | default when `extflash.pages` is absent |
| `images/rfcal_synthetic.pages` | default RF calibration block (`cfw/drivers/cal_default.c`, made by `cfw/tools/make_cal_default.py`), laid over any NAND image for cfw runs | `make_rfcal.py` | cfw TX |
| `images/bootstub*`, `images/idle_*.bin` | bootloader stand-in, idle WFI patch | ours | all / stock speedup |
| `images/extflash.pages` | stock first-boot format + ICON fonts/logo + a codeplug | `build_extflash.py` from **your own** AnyTone package (ICON .CDI/.CDD) and a CPS capture; `factory_formatted.pages` = stock firmware's own first-boot format (see "Boot path" below) | stock runs with text, fonts and codeplug |
| `images/activation_0800A000.bin` | 16-byte activation key at `0x0800A000` (derived from the emulated chip UID) | extract from an emulated stock run (see "Boot path") | stock and lifted runs only |
| `../D890UV V1.05 DMR official release 260521/.../D890UV_V1.05_20260521.CDD` | stock firmware | **your own** AnyTone package | `--orig` |

AnyTone files (stock firmware, ICON package, images built from them, real codeplugs) are not part of the published
repo. Stock runs (`--orig`, `--image`, lifted ELF) stop with an error naming the missing files; without
`extflash.pages` the stock firmware runs on `synth.pages` but is not expected to reach a usable main screen (no ICON fonts, no stock settings/codeplug; not tested). cfw runs (an ELF with the
symbol `channels_init`) skip the activation key and need only the files marked "ours".

```sh
python3 emu/make_nand.py                                   # -> images/synth.pages
python3 emu/run.py --elf cfw/build/cfw.elf --nand emu/images/synth.pages --seconds 3
```

## What is modelled

| Block | Model |
|---|---|
| Cortex-M33 + FPU, NVIC, SysTick | Renode built-in |
| Flash 2 MB (also aliased at 0x0), SRAM 768 KB, SRAM4, backup SRAM | memory; `images/bootstub.bin` stands in for the bootloader |
| FLASH controller | `models/flash_u5.py`: 2 banks, page erase, unlock |
| GPIO A–I, TIM1–8/15–17, USART1–3, UART4–5, LPUART1, IWDG | Renode STM32 models |
| RCC, PWR, ICACHE/DCACHE, RTC, TAMP, ADC1/4, DWT, DBGMCU, UID, USB OTG core | `models/*.py` |
| SPI1–3, GPDMA1, EXTI, SYSCFG, CRS, DAC, CRC, LPTIM1, WWDG, LPGPIO1 | register storage only |
| **SPI-NAND** (Kioxia 4 Gbit) on PC5/PE15/PB10/PB8 | `models/SpiNandBitbang.cs` |
| **TFT** (8080 bus PE7–14, WR PD15, CS PD14, D/C PD12) + **keypad matrix** (rows PE7–14, cols PC4/PB1/PB2) | `models/LcdKeypad.cs` |
| **Two RF transceivers** (3-wire SCK PB3, SDA PB4, CS PB5/PD3) | `models/RfTransceiver.cs` + read hook (see quirks) |
| Power switch PA0, key/detect lines | `models/BoardInputs.cs` |
| Channel knob (encoder PC8/PC9 + EXTI8/9 interrupts) | `models/Knob.cs` |
| ADC1 scan via GPDMA (battery ch2, temperature) + ch1 audio stream | `models/Analog.cs` writes the 5-value DMA buffer `0x20001d34` (`analog Battery 7.6`, `analog Set 1 2600`); `analog Stream @f 48000` plays discriminator audio into ch1 (see "ADC sample stream") |
| RTC calendar + 1 s wakeup interrupt (IRQ 2: clock display and many UI timers) | `models/Rtc.cs` (`rtc SetTime "2026-03-14 15:09:26"`) |
| GPS receiver (NMEA on USART2) | `models/GpsNmea.cs` (`gps Position 32.7 -117.1 20`, `gps Fix false`) |
| JieLi Bluetooth module (UART4, `A5 5A cmd len data 06`) | `models/Bluetooth.cs`: answers the "D890" hello with V1024, acks each command (log: `bluetooth.log`) |
| Bit-banged I2C (SDA PB6, SCL PB7): LIS2DW12 accelerometer 0x19, volume pot 0x2E | `models/I2cBus.cs` (`i2c Fall` / `i2c Upright` = man down, `i2c Pot` / `i2c PotLog` = last pot values); VOX voice from the module: `bt Send "03 00 00 00 01 00 00 00 00"` (byte 3 = voice) |
| **SCT3288 DMR baseband** on SPI2 (CS PB12, ready PD11/PD7, reset PD9) | `models/Baseband.cs` (`spi2 Send "00 17 00"`, `spi2 SetRule 0x33 "…"`, `spi2 SetEvent 0x43 "00 17 0A"`, `spi2 EventDelayMs 500`; frame log: env `D890_BB_LOG=<file>`) |
| Received RF signal (carrier, RSSI, noise) | `models/RfTransceiver.cs`: `rf0 Signal 146520000 -80`, `rf0 SignalHere -80`, `rf0 SignalOff`, `rf0 Status` (see "RF signal") |
| DAC1 (APC PA4, UHF tune PA5) | `models/Dac.cs` (`dac1`): registers, timer-triggered DMA ring, optional capture to `.adc16` (AutoCapture off) |
| TIM4 = MCU TX audio PWM (PB9) + capture | `models/Pwm.cs` (`pwm`, replaces `timer4`): one PWM period per clock tick, CCR4 from the GPDMA channel whose DAR is CCR4 (UDE/CC4DE), duty → 12-bit `.adc16` (see "TX capture") |
| Decoded-audio tap (emulator only, `0x5FFF0600`) | `models/PcmTap.cs` (`pcm`): copies cfw `g_emu_pcm` (8 kHz ring) to `out/<run>/rx_pcm.raw`; `run.py` attaches it by ELF symbol |
| Symbol FIFO (emulator only, `0x5FFF0500`) | `models/SymbolSource.cs`: `syms Load @file.dibits 4800`, `syms Loop true`, `syms Gate true`, `syms Status`; read by cfw `make EMU=1` |

### Side keys, knobs, power

| Control | Hardware | Firmware | Emulator |
|---|---|---|---|
| PTT | PA10, active low | `FUN_0811d9cc` bit 0 → key 0x18/0x19 | `board Set "A10" false/true` |
| PF1 / PF2 (side, below PTT) | PE2 / PA14, active low | bits 2 / 3 → key codes 21 / 22 | `board Set "E2" …`, `"A14"` |
| PF3 (top, left of channel knob) | PA8, active low | bit 1 → key code 23 | `board Set "A8" …` |
| Channel knob (top left) | quadrature encoder PC8 (A) / PC9 (B), EXTI8/9 both edges | `IRQ19_Handler`/`IRQ20_Handler` → `FUN_080c85cc` → key 0x11 / 0x12 | `knob Turn N` (`models/Knob.cs`, raises IRQ 19/20 itself; EXTI is a stub) |
| Volume/power knob (top right) | volume is analog (not seen by the MCU); the switch drives PA0 | PA0 high = power-off request: `FUN_080d6954` saves, drops PC3 latch, resets | `board PowerOff`; on = `machine Reset` + `board PowerOn` |

The RTC keeps its calendar across resets (battery-backed).

### Keypad codes (`lcd Press <code> <ms>`)

| Code | Key | Code | Key |
|---|---|---|---|
| 1–10 | digits 0–9 | 13 | Menu / Select (left soft key) |
| 11 | `*` (long press = key lock; Menu then `*` = unlock) | 14 | Exit / Back / List (right soft key) |
| 12 | `#` | 15 / 16 | Up / Down |
| 19 / 20 | P1 / P2 (P2 on main screen = VFO/channel toggle) | | |

Side keys / PTT are GPIO inputs driven by `board` (A8, A10, A14, E2; active low). Key events: key task
`FUN_080ca6c4` → queue → `FUN_081223e0` → dispatcher `FUN_08016978`. Auto key lock: `DAT_20014932`.

### ADC sample stream (both firmwares)

`analog Stream @file <rate>` plays a file into ADC1 ch1 (the RF chip's discriminator / audio). `analog StreamLoop true` (or `analog Loop true`),
`analog StreamStop`, `analog StreamGain 0.5`, `analog StreamStatus`. Files: `.raw` (int16 LE mono, rate given),
`.wav` (PCM16, rate from the header) → value 2048 + s/16; `.adc16` (uint16 LE 12-bit counts, as cfw `make -C cfw/dsp
waves` writes them) → used as is. Linear resampling to the firmware's rate. The mode follows the firmware's registers
(re-read every 1 ms):

| Firmware | ADC set-up | What the model does |
|---|---|---|
| Stock V1.05 | continuous 5-ch scan, DMA circular into `0x20001d34` (CFGR1 0x3007, EXTEN 0); ch1 is read by TIM6 IRQ49 at 4.8 kHz (`FUN_080d7d70`) for the MDC1200 decoder `FUN_0813f3d8` (sync 0x07092A446F) | writes slot 0 at 48 kHz with the stream sample for TIM6's exact tick time (from TIM6 CNT; `ReaderTimer`) |
| cfw (`drivers/adc.c`) | EXTEN rising, EXTSEL 14 = TIM15 TRGO 48 kHz, 5-ch scan, GPDMA1 ch0 linked list to itself over a 960-frame ring, write position = C0BR1.BNDT, no interrupt | one scan frame per trigger at the timer rate into the ring (from the LLI), BNDT kept up to date, stream played by frame count; HT/TC flags + GPDMA IRQ (29 + ch) when the firmware enables them |

Tests:
- Stock: `python3 emu/streams/test_stock_mdc.py` (≈2 min) → stock decodes an MDC1200 PTT ID from
  `streams/gen_mdc1200.py` audio: op 0x01, arg 0x80, unit 0x1234, popup "Launch ID: 1234"
  (`streams/stock_mdc1200_launch_id.png`). The K6RRR codeplug has no channel with MDC decode and the NAND image has no
  squelch calibration, so the test pokes: knob ×6 to the analog channel at 449.440, `DAT_200042e2` = 0 (main
  side A), channel A +0x1a = 0x40 (decode type 4), `rf0 SignalHere -80` + threshold `DAT_2000d762` = 0x60, and starts
  TIM6 as `FUN_08063048(249, 99)` does. `--debug` logs the 112 received bits per packet. (The stock ADC audio decoder
  is MDC1200, not APRS AFSK.)
- cfw: `emu/streams/test_cfw_adc.sh [first builtin channel] [modes]` plays `cfw/build_streams/*.adc16` per mode into
  the normal build (MCU-DSP backend) → `out/cfw_a_<mode>/montage.png`.
- Front panel: type `analog Stream @/abs/path/file.adc16 48000` in the monitor box.

### TX capture (both firmwares)

**TX audio = TIM4_CH4 PWM on PB9** (stock MCU TX audio pin, `cfw/docs/AUDIO_PATHS.md`). `models/Pwm.cs` (`pwm`) replaces
the TIM4 timer model: while CR1.CEN is set it runs one PWM period per tick at 120 MHz / ((PSC+1)(ARR+1)); with
DIER.UDE / CC4DE it loads CCR4 from the GPDMA1 channel whose CxDAR = 0x40000840 (ring from SAR/BR1 or the linked-list
item, BNDT kept moving, HTF/TCF + IRQ 29+n), and records CCR4 · 4096 / (ARR+1) as one 12-bit sample (the RC-filtered
level). Above 200 kHz (stock idle ARR 255) it records one sample per CCR4 write instead. The cfw runs it at 48 kHz
(ARR 2499, GPDMA1 ch1, request 71).
- Automatic: while rf0 transmits → `out/<run>/tx_<n>.adc16` + `tx_<n>.json` (samples, rate, duration, RF TX freq,
  APC levels seen on DAC1 ch1). `pwm AutoCapture false` turns it off.
- Manual: `pwm Capture @f.adc16`, `pwm CaptureStop`, `pwm Status`.

`models/Dac.cs` (DAC1: ch1 PA4 = APC, ch2 PA5 = UHF RX tune; the cfw sets both like stock) keeps its own capture
(AutoCapture off by default; `dac1 CaptureChannel 1|2`, `dac1 Capture @f.adc16`) for timer-triggered DAC streams. Per trigger (TSELx 8 = TIM15 TRGO etc., rate = 120 MHz /
((PSC+1)(ARR+1))) with DMAENx it loads the next halfword of the GPDMA channel whose CxDAR is its DHR (ring from the
linked-list item: UB1 → BNDT, USA → SAR), keeps CxBR1.BNDT moving, and appends DOR to the capture as `.adc16`
(same scale as the ADC injector, so a capture plays into another radio with `analog Stream`). Without a trigger each
DHR write is one sample.
- DAC capture: `dac1 AutoCapture true` → while rf0 transmits (`rf0 Transmitting`: reg 0x52 & 0x7000 = 0x3000 as stock
  keys TX) `out/<run>/tx_<n>.adc16` + `.json` (clashes with the pwm capture names: turn `pwm AutoCapture false` first).

**Stock V1.05 FM TX** (an analog channel at 449.440, PTT 2 s; `out/stock_tx_fm`, `stock_tx_cap`): there is no MCU baseband in FM; the
mic audio goes to the RF chip in analog. What the MCU does:

| When | What |
|---|---|
| PTT +0…70 ms | TIM3 CH3 CCR3 = 0xFFF (PB0 = LCD backlight), TIM4 CH4 PWM re-init (PB9 = MCU TX audio, idle here) |
| RF chip 0 | reg 0x52 = 0x0040, 0x56 = 0, freq 0x69:0x68, TX set-up regs (as cfw `k_tx`) + 0x5C 0x1000, 0x10 0xA3A3, 0x2E/0x2D/0x2F, 0x31; then 0x56 = 0x0002 and **0x52 = 0x3041 = TX on**. Reg 0x30 is NOT written for TX. Release: 0x52 = 0x0041 → 0x4041, RX regs, 0x30 = 0x5F77 |
| DAC1 | right after TX on: **DHR12R1 (PA4) = 0x0FF0**; after release ch1 = 0, DHR12R2 (PA5) = 0xFF0. = APC (stock `FUN_0811a75c`, 0xFF with the erased calibration block) and UHF RX tune (`FUN_0811c8f0`) |
| GPIO | +134 ms PE5 high, +145 ms PD2 low, +146 ms PE1 + PA15 high; release +315 ms: PD2 high, PA15/PE1 low, PE5 low |
| Frequency | regs hold f × 1.0013807 here (reference correction `FUN_08047e10` with no NAND calibration): RX 449.440 → 450.0606 MHz, TX 444.440 → 445.0537 MHz |

DMR TX in stock goes through the SCT3288 baseband (SPI2). The MCU's own TX audio (MDC-style data, APRS AFSK) is TIM4_CH4
PWM on PB9 (`cfw/docs/AUDIO_PATHS.md`).

### Two-radio loopback (cfw digital TX/RX)

`python3 emu/txrx_loop.py --mode dmr|p25|p25p2|dstar|nxdn|ysf|all [--snr 20] [--offset 300] [--ptt 3]`.
Radio A: built-in channel of the mode, test tone (writes the firmware's `s_tone_hz`, as Menu > Test tone), PTT;
`dac1 Capture` records the TX baseband. The capture (+ idle padding, AWGN, offset) is played into radio B with
`analog Stream`; radio B's decoded audio comes from `pcm` (`rx_pcm.raw` / `.wav`). The tool measures the tone (FFT
peak, SNR in 200–3800 Hz), reads B's screen text (IDs, firmware "AF … Hz … dB" meter) and writes `out/txrx_loop.md`
+ `out/montage_<mode>.png`. cfw's vocoder is its own test codec (`voc_sine`, not interoperable).
`--tag X` keeps parallel runs apart. `--snr` is AWGN in the discriminator domain over the full 24 kHz band, relative
to the TX RMS deviation (harsher than C/N in 12.5 kHz as cfw/dsp's tables use).

Results 2026-11-21 (cfw/build/cfw.elf, PTT 3 s, ~3 host min per mode; `streams/txrx_dmr.png` is the 2026-11-23 simplex rerun, DMR A → B):

| mode | TX freq reg | TX dev rms | clean: tone / SNR | 20 dB + 300 Hz offset: tone / SNR | IDs on radio B |
|---|---|---|---|---|---|
| DMR | 445.5125 MHz | 1504 Hz | 439.9 Hz / 76.7 dB | 439.9 / 84.2 dB | TG91 < 9990201 TS1 |
| P25 | 442.000 | 1347 | – | 439.9 / 80.5 | TG1 < 9990201 |
| P25P2 | 465.550 | 2051 | – | 439.9 / 83.4 (fw meter 58 dB) | TG1 < 9990201 S1 (after cfw fix) |
| D-STAR | 145.670 | 1070 | – | 439.9 / 77.1 | N0CALL/CFW > CQCQCQ |
| NXDN | 441.250 | 829 | 439.9 / 58.9 | 439.9 / 78.0 | TG1 < 28729 RAN1 |
| YSF | 145.375 | 2200 | 439.9 / 77.4 | 439.9 / 83.9 | N0CALL > ALL |

The first NXDN/YSF run showed garbled voice frames and NXDN IDs 0: cfw's 20 ms DAC ring underran (stale 20 ms
repeats in the TX capture). Fixed in cfw (80 ms rings, NXDN IDs from SACCH); numbers above are after the fix.

Simplex matrix (2026-11-21): `txrx_loop.py --simplex --mode all --neg dmr=1409:1408`. cfw simplex channels (offset
from the first built-in): fm 0, dmr 4, p25 7, p25p2 13, dstar 10, nxdn 11, ysf 12 (tx == rx, direct-mode framing).
Identities are written before PTT (`g_dbg_my_id`, `g_dbg_my_call`): A = 9990201 / XX0AAA, B = 9990202 / XX0BBB. A
direction passes when the tone comes back within 5 Hz and the TX radio's ID is on the RX screen. The tool copies the
ELF to `out/txrx_elf<tag>.elf` first (a rebuild of cfw/build mid-run moved the symbols once). `--voc N` sets
`g_voc_select` (0 auto, 1 sine, 2 imbe, 3 SCT3288) on both radios.

Results 2026-11-21 (second run), ELF snapshot `out/cfw_matrix2.elf` (cfw build 01:19: DMR direct timing fix, DMR
CC voice gate, SCT encode queue 24, IMBE), Baseband.cs with the fixed SCT3288 stand-in (frequency in 0.25 Hz units),
RF gate on, normal 5 Hz tolerance everywhere. Reports `out/txrx_loop{_v1b,_f2,_imbe2,_sct2}.md`, montages
`out/montage_<case>.png`. **39/39 PASS.** Both directions identical; IDs = what the receiver showed.

| mode (simplex ch) | sine `--voc 1`: tone / SNR / fw meter | IDs A→B / B→A | IMBE `--voc 2` | SCT3288 stand-in `--voc 3` |
|---|---|---|---|---|
| FM 146.520 | 440.0 Hz / 71.2 dB / AF 440.0 66 dB | – | – | – |
| DMR 441.000 TS1 | 439.9 / 86.9 / 146 dB | TG99 < 9990201 / < 9990202 | – | 440.0 / 67.4 PASS |
| P25 442.000 | 439.9 / 79.2 | TG1 < 9990201 / < 9990202 | 441.4 / 27.2 PASS | – |
| P25P2 445.500 | 439.9 / 53.7 | TG1 < 9990201 S1 / < 9990202 S1 | – | 440.0 / 54.9 PASS |
| D-STAR 145.670 | 439.9 / 77.4 | XX0AAA/CFW / XX0BBB/CFW > CQCQCQ | – | 440.0 / 69.6 PASS |
| NXDN 441.250 | 439.9 / 78.0 | TG1 < 28729 / < 28730 | – | 440.0 / 68.5 PASS |
| YSF 145.375 | 439.9 / 77.4 | XX0AAA / XX0BBB > ALL | 439.9 / 77.4 PASS (VW) | 440.0 / 61.8 PASS |

Filters and talkaround (`--filters --talkaround --rf-gate --voc 1`):

| case | expected | got | result |
|---|---|---|---|
| DMR CC 1 → RX CC 2 | no decode | nothing | PASS (first run, old ELF: voice leaked; fixed by the CC voice gate) |
| DMR CC 1 → CC 1 | decode | TG99 < 9990201 | PASS |
| P25 NAC 293 → 123 | no decode | nothing | PASS |
| P25 NAC 293 → F7E (wildcard) | decode | TG1 < 9990201 | PASS |
| NXDN RAN 1 → 2 | voice muted | no audio (IDs shown) | PASS |
| NXDN RAN 1 → 0 (wildcard) | decode | TG1 < 28729 | PASS |
| YSF DG-ID 20 → 10 | muted | nothing | PASS |
| YSF DG-ID 20 → 0 (wildcard) | decode | XX0AAA > ALL | PASS |
| DMR TG91 talkaround both (TX 440.5125, RX 440.5125) | decode | TG91 < 9990201 | PASS |
| talkaround TX only (TX on output 440.5125) | decode | TG91 < 9990201 | PASS |
| talkaround RX only (TX 445.5125, RX 440.5125) | no decode | nothing (RF gate) | PASS |

Rerun 2026-11-22 on the stock TX-audio path (cfw `dac.c`: TIM4_CH4 PWM on PB9 fed by GPDMA1, captured by
`models/Pwm.cs`; APC on DAC ch1 PA4, UHF tune on ch2 PA5, see `cfw/docs/AUDIO_PATHS.md`). Same four sets, reports
`out/txrx_loop_pwm_{sine,f,imbe,sct}.md`: **40/40 PASS** (sine 15 incl. the DMR negative, filters+talkaround 11,
IMBE 4, SCT3288 10). Tones/SNR as above except FM 440.0 Hz / 63.4 dB (PWM 48 kHz instead of the old DAC path) and
DMR sine 88.5 dB.

First run (old ELF `out/cfw_matrix.elf`, old stand-in): 38/39, DMR CC voice leak, and P25P2 + SCT3288 at 438.9 Hz /
8.5 dB (the stand-in coded frequency per frame by zero-crossing count; fixed in Baseband.cs). DMR RX hears BS, MS
and direct syncs on every channel, so there is no BS-vs-MS negative case; the negative cases are the filters
(`--filters`: DMR CC, P25 NAC, NXDN RAN, YSF DG-ID) and talkaround with the RF gate (`--talkaround`, `--rf-gate`: the
signal sits on the TX frequency, `analog StreamGate` lets a receiver hear it only when tuned there). Without
`--rf-gate` a repeater-TX → simplex-RX pair decodes by design.



### RF signal (both firmwares)

The air is shared by both chips. A chip sees the signal when its frequency (regs 0x69:0x68, Hz) is within
`SignalWindow` (6250 Hz) and its RX is on (reg 0x30 low byte != 0; 0x5F00 = idle); else the noise floor
(`Floor`, -130 dBm). Registers the stock V1.05 reads (trace: `rf0 Trace true`; it polls 0x01/0x02/0x0E about
100 times a second):

| Reg | Stock use | Model |
|---|---|---|
| 0x02 [9:0] | RSSI: `FUN_08049e3c`; squelch `FUN_080d22f0` opens at RSSI >= threshold (written to reg 0x49 by `FUN_080e36f4`), closes below threshold - 12 | `2 * (dBm + 160)` (scale assumed: -80 dBm = 0xA0, -130 dBm = 0x3C) |
| 0x01 | [15:8] noise (> 0x50 closes), [3:0] glitch count | carrier 0x1000, none 0x7F0F |
| 0x0E bit 14 | carrier / squelch detect (`DAT_2000406c`, `FUN_080df38c`) | set with carrier |
| 0x0A, 0x0F, 0x0B–0x0E, 0x54/0x55 | tone frequency, DTMF/tone code, CTCSS/DCS scan, frequency counter | register file only |

In the emulated stock radio the squelch threshold in reg 0x49 is 0xFFFF (calibration data is not in the NAND
image), so stock squelch never opens; the start channel of the NAND codeplug is digital anyway. The cfw main screen shows the RSSI
(dBm) and a green RX badge with carrier.

## Custom firmware (cfw)

`D890UV_src/cfw` (own README) runs on the same emulator. `run.py` finds its delay and RF-read hooks by the alias
symbols `FUN_0811ece0` / `FUN_080e2da4`; the stock idle WFI patch is not applied (cfw sleeps in WFI itself).

```sh
python3 emu/webui.py --launch cfw          # make -C cfw, front panel (desktop app: d890uv-cfw, :8892)
python3 emu/webui.py --launch cfw-emu      # test build (make EMU=1 -> cfw/build_emu/cfw.elf)
python3 emu/run.py --elf cfw/build/cfw.elf --seconds 3 --then "0|knob Turn 1" --then "0.5|shot"
```

- Text pane: hooks `gfx_text(x, y, const char *s, …)` (C string in r2). cfw redraws every 100 ms, so the pane
  keeps each string once (a redrawn one moves to the end).
- Power: cfw holds the PC3 latch like stock, so the panel's off detection (PC3 low) works. Off = `board PowerOff`
  (~0.1 s PA0 high → settings saved, PC3 dropped, reset); on = `machine Reset` + `board PowerOn`; the saved
  channel/VFO comes back.
- Controls checked in the panel (2026-11-20): keypad (Up/Down/digits/Menu/P2), knob ±1, PTT (PA10: RX→TX→RX),
  PF3 (PA8, VFO mode cycle), PF1/PF2 (PE2/PA14: key codes 21/22 reach `ui_key`; no UI action yet), power off/on.
- Speed: ~0.3–0.4x real time on a loaded host (8 cores busy); stock idles faster thanks to its WFI cave.
- RF: `rf0 Signal <Hz> <dBm>` → cfw main screen RSSI and RX badge (`rf_rssi`/`rf_carrier`, same regs as stock).
- Symbol injection (MCU-DSP receive stand-in): `make EMU=1` adds the `emu-syms` modem backend
  (`cfw/hal/emu_syms.c`) that reads `syms` at the symbol rate and, while `proto/dmr_nxdn` has no cfw glue yet,
  a receive-only DMR test glue (`cfw/emu_test/dmr_emu.c`). Test stream: `emu/streams/make_streams.sh` builds
  `gen_dmr` from the proto library and writes `streams/dmr_call.dibits` (BS voice call TG 91 from 9990201, CC 1,
  90 voice frames, 3.2 s). Result: the cfw decodes the call and all 90 frames:
  ```sh
  python3 emu/run.py --elf cfw/build_emu/cfw.elf --seconds 3 --name cfw_dmr --then "0|lcd Press 8 150" \
    --then "0|syms Load @$PWD/emu/streams/dmr_call.dibits 4800" --then "4|shot"     # key 7 = CH07 DMR TG91
  ```
  Screen: `TG91<9990201 TS1 end V90`, `emu-syms sym 15456` (`cfw/screens/emu_dmr_rx.png`).
  `syms Gate true` delivers symbols only while rf0 has a carrier. Other modes: write a generator like
  `gen_dmr.c` (one symbol per byte; dibits 0..3 with +3→01, +1→00, −1→10, −3→11; D-STAR bits 0/1).
- ADC sample streams: see "ADC sample stream" (`analog Stream`); the symbol FIFO stays for symbol-level tests. The SCT3288 vocoder/packet API (AMBE) is a pending request for `Baseband.cs` (cfw README).

### cfw pre-flash test support (2026-11-23)

- Synthetic RF calibration block: cfw ELFs that have the pre-flash TX gate (symbol `safety_tx_block`) get
  `images/rfcal_synthetic.pages` (`make_rfcal.py`) loaded on top of the NAND image; the factory-formatted image has no
  calibration and the cfw refuses TX without one. Values = the default RF calibration (`cfw/drivers/cal_default.c`). `run.py --no-rfcal` leaves it out; the stock and
  lifted images never get it.
- `run.py --bootstub FILE`: another binary at 0x08000000 (cfw `tools/preflash_test.py` f4: a "dirty" bootloader exit).
- `models/rcc.py`: write 1 to 0x46020FFC = HSE never ready (clock-failure test).
- `models/BoardInputs.cs`: levels set with `board Set` (a held key) survive a soft reset (AIRCR / `machine Reset`).

## Boot path (original V1.05) and what each step needed

| Step | Firmware | Emulator piece |
|---|---|---|
| Reset → app | bootloader jumps to `0x0800C000` | boot stub at `0x08000000` |
| USB init | waits `OTG_FS GRSTCTL` | `usb_otg.py` |
| Power key | PA0 high = pressed; still pressed later = power-off (`FUN_080d6954`: PC3 latch off + AIRCR reset) | `board` holds PA0 high 1 s (`powerKeyMs`), then releases it for good |
| Activation | 16-byte key at `0x0800A000` derived from chip UID (`FUN_080215c0`) | `images/activation_0800A000.bin` |
| External flash ID | `98 ED`/`98 CD` | NAND model |
| NAND format | `0x1FF00` "CHECK 4G BLOCK FOR VALID." + bad-block table at `0x0` | `build_extflash.py` |
| Data format | `0x04EC0000` "CHECKING DATA FOR D890UV" | `build_extflash.py` |
| Data areas | every A/B pair must carry a valid marker (contacts: 500 pairs) | firmware factory-formats a blank NAND in the emulator (`images/factory_formatted.pages`), then CPS data is laid on top |
| RTC | `TAMP_BKP0R = 0x5050` (clock set) | `tamp.py` |
| Keys | active-low, idle high | `board`, `lcd` |
| Power-on password | from codeplug `0x3500940` (off in the K6RRR codeplug) | |
| RF self-test | write/read reg `0x57 = 0xA888` on both chips | RF model |
| Boot text | "SAN DIEGO DMR" (from codeplug) | |
| DMR baseband init | SPI2 `84 A9 61` frames: 0x17=0x0E → raw config `74 A9 01 00 01 00` → 0x17=00, cmds 0x2d/0x79/0x33/0x10/0x32 | `models/Baseband.cs` (`spi2`) |
| Repeater search (roam step, PTT on repeater channel) | MCU cmd 0x43 (repeated ~3x) → baseband status 0x17=0x0A ("done, nothing found") 500 ms later clears busy `DAT_200212fc` → One time Roam ends "Search Fail!", PTT shows "Repeater not Found!". Repeater-present reply not modelled | `models/Baseband.cs` (`Events`) |
| Vocoder packets (cfw) | B0 0x80 control / 0x81 channel / 0x82 speech (AMBE-3000 layout): fake vocoder, PRODID/VERSTRING/RATET acks, encode → `'F' freq16(0.25 Hz) amp/128`, decode → sine. Stock never sends these. See `cfw/docs/SCT3288_VOCODER.md` | `models/Baseband.cs` (`spi2 FromMcu "84 A9 61 00 01 80 30"`) |
| RTOS start | `FUN_0811dd64`, OSRunning=1 | |
| Battery | ADC1 DMA buffer, V×100 = (raw·0x528>>12) + cal − 100; < 6.01 V = "Battery Volt Too Low!" | `analog` (7.6 V) |
| Bluetooth | 25 × hello, then commands acked one by one | `bt` |
| **Now shows** | main screen, clock running, menus work | |

Rebuild the NAND image after changing the codeplug:
```sh
python3 emu/fake_radio_cps.py <wineprefix> emu/out/codeplug_capture   # CPS: Program > Write To Radio (COM1)
python3 emu/build_extflash.py --base images/factory_formatted.pages --codeplug emu/out/codeplug_capture/writes.bin
```
Recreate the factory-formatted base (only if the firmware's data layout changes):
```sh
python3 emu/build_extflash.py --name base_unformatted.pages
python3 emu/run.py --orig --seconds 20 --nand emu/images/base_unformatted.pages --save-nand --name fmt
cp emu/out/fmt/nand.pages emu/images/factory_formatted.pages
```

Monitor commands (Renode monitor, or `run.py --extra` / `--then`):
```
lcd Press 5 150          # key code 5 for 150 ms
lcd Screenshot @f.ppm    # emu/ppm2png.py converts
board Set "A0" false     # power switch off
nand Trace true          # log every NAND command
nand Peek 0x3500940 8    # NAND contents
rf0 Trace true           # log RF register traffic
```

## Renode quirks found (1.17.0)

- A C# model that fails to compile makes the headless run hang silently right after "Including script(s)". Check a new model with a short `--timeout` run.
- Timers: the kernel clock is 120 MHz (was 160 MHz in the .repl). Renode's `STM32_Timer` period is ARR counts, not
  ARR + 1 (TIM6 PSC 99 ARR 249 → 4819 Hz, not 4800). The stream injector maps the reader's ticks onto the ideal
  period; firmware timers themselves still run (ARR+1)/ARR fast.
- Model clock entries and timer interrupts are served at Renode's 100 µs quantum boundaries; a model that samples
  "now" sees stair-stepped time. Use the timer's CNT or a frame counter for sample timing (Analog.cs does).
- Compile-check a C# model before loading it (a failing model hangs Renode): `mcs -nostdlib -noconfig
  -target:library` with `-r:` System.Runtime, System.Private.CoreLib, netstandard, System.Linq, System.Collections,
  System.IO, System.Runtime.Extensions, Infrastructure.dll, Renode.dll from the Renode folder. Models that use
  `ReadOnlySpan`-heavy APIs crash mcs; those still need a short `--timeout` run.
- Renode's SysTick ignores CLKSOURCE. The firmware runs SysTick from HCLK/8 (120 MHz / 8, `FUN_08062654`), so `systickFrequency` is 15 MHz. At 160 MHz the OS ran 10.7× fast (popups vanished in 0.2 s, auto key-lock after seconds).
- Renode's `GetRegisterUnsafe` in hooks logs a warning per call (tens of thousands); use `GetRegister`.
- Relative `filename:` in `Python.PythonPeripheral` makes platform load hang. `run.py` writes `out/stm32u575.abs.repl` with absolute paths.
- `STM32SPI` with `series: H7` hangs on load, so SPI uses the storage model.
- `STM32WBA_FlashController` ignores the U5 bank bit. It erased live code in bank 1 when the firmware erased its bank-2 cache.
- `echo`/`print` from monitor scripts hangs headless runs (`-P -1`).
- Paths with spaces break the monitor tokenizer. `run.py` copies images.
- `GPIO.Set()` does not re-notify an unchanged level, and the firmware's soft reset clears GPIO ports. Board models therefore drive port inputs directly (`OnGPIO`) and re-drive every 1 ms.
- Renode's monitor `Parse` output is reachable from Python monitor commands through `CommandInteractionEater`
  (`then_cmd.py`); headless runs otherwise discard command output.
- When a chip's data pin shares a GPIO port with its clock (RF chips: PB3/PB4), the port re-applies its output register to the data pin after each clock write. Bit-level reads cannot reach the CPU, so `run.py` hooks the firmware's 16-bit read routine `FUN_080e2da4` (found by symbol in lifted ELFs) and returns the RF model's value.

## Repository layout

This is one of six repositories: D890UV-CFW, D168UV-CFW, D890UV-CFW-CPS, D168UV-CFW-CPS, D890UV-EMU, D168UV-EMU.
The build and test scripts expect them side by side in this layout:

```
work/D890UV/D890UV_src/cfw       <- D890UV-CFW
work/D890UV/D890UV_src/cfw_cps   <- D890UV-CFW-CPS (or D168UV-CFW-CPS)
work/D890UV/D890UV_src/emu       <- D890UV-EMU
work/D168UV/cfw                  <- D168UV-CFW
work/D168UV/emu                  <- D168UV-EMU
```

No firmware, flash images or programming files from the radio maker are included. The emulators build their own
flash images (`emu/make_nand.py` -> `images/synth.pages`); running the original radio firmware in the emulator needs
files made from your own radio package (see the emulator README). Flashing a real radio (CFW repositories): `docs/HARDWARE.md`,
`docs/CALIBRATION.md` and `docs/PREFLASH_AUDIT.md`.

# DMXENTIX SoundNode

**A BLE-connected sound-level monitoring node built with an ESP32-C3 and MAX9814 microphone amplifier.**

SoundNode is a standalone PlatformIO firmware project in the DMXENTIX Hub repository. It samples the analogue output of a MAX9814 microphone module, calculates relative audio RMS and peak levels, and publishes the readings to the DMXENTIX Hub mobile application over Bluetooth Low Energy.

## Project status

**Initial firmware structure and prototype implementation.** The code is intended for the ESP32-C3 Super Mini and a MAX9814 breakout with a 3.3 V supply. Confirm the exact breakout pinout and measure its OUT voltage before connecting it to the ESP32-C3 ADC. Build and hardware validation are still required on the target board.

## Repository location

```text
DMXENTIX-Hub/
└── DEVICES/
    ├── EMOTIX360-S3/
    └── DMXENTIX-SOUND-C3/
```

## Hardware

| Component | Purpose |
|---|---|
| ESP32-C3 Super Mini | Main controller and BLE GATT server |
| MAX9814 module | Electret microphone preamplifier with AGC |

### Wiring

| MAX9814 pin | ESP32-C3 Super Mini |
|---|---|
| VDD | 3V3 |
| GND | GND |
| OUT | GPIO0 (ADC1_CH0) |
| GAIN | 3V3 for 40 dB maximum gain on the common Adafruit-style breakout |
| A/R | Leave unconnected for the breakout's default attack/release ratio |

The module and ESP32-C3 must share ground. The MAX9814 IC accepts a 2.7–5.5 V supply. This project powers the module from 3.3 V.

**ADC voltage check:** the ESP32-C3's 11 dB ADC attenuation range is approximately 0–2.5 V. The common Adafruit-style MAX9814 breakout produces about 2 V peak-to-peak around a 1.25 V DC bias (approximately 0.25–2.25 V), but clones and other board layouts may differ. Measure OUT relative to GND before connecting it. Keep the complete waveform within the ESP32-C3 ADC input range; do not apply 5 V to GPIO0. [ESP32-C3 ADC range](https://docs.espressif.com/projects/arduino-esp32/en/latest/api/adc.html) · [MAX9814 breakout output and gain settings](https://learn.adafruit.com/adafruit-agc-electret-microphone-amplifier-max9814/overview)

Keep the MAX9814 output's DC bias connected to the ADC. Do not add an AC-coupling capacitor between OUT and GPIO0 for this measurement method.

### MAX9814 gain configuration

On the common Adafruit-style MAX9814 board, connecting GAIN to VDD selects 40 dB, connecting GAIN to GND selects 50 dB, and leaving GAIN floating selects 60 dB. The firmware does not control the GAIN or A/R pins. Verify the silkscreen and schematic of your own module before wiring. [MAX9814 data sheet](https://www.analog.com/media/en/technical-documentation/data-sheets/MAX9814.pdf)

## Firmware architecture

```text
DMXENTIX-SOUND-C3/
├── platformio.ini
├── README.md
├── include/
│   ├── config/
│   │   ├── audio_config.h
│   │   ├── ble_config.h
│   │   ├── device_config.h
│   │   └── pin_config.h
│   ├── core/
│   │   └── sound_node.h
│   ├── drivers/
│   │   └── max9814_driver.h
│   └── services/
│       ├── audio_processing_service.h
│       └── bluetooth_manager.h
└── src/
    ├── main.cpp
    ├── core/
    │   └── sound_node.cpp
    ├── drivers/
    │   └── max9814_driver.cpp
    └── services/
        ├── audio_processing_service.cpp
        └── bluetooth_manager.cpp
```

| Layer | Responsibility |
|---|---|
| `config` | Board pins, device identity, BLE UUIDs, and sample settings |
| `drivers` | Direct ADC acquisition from the MAX9814 output |
| `services` | RMS/peak calculation and BLE GATT communication |
| `core` | Startup and main application coordination |
| `main.cpp` | Arduino entry point |

The microphone driver only acquires raw samples. Audio processing removes the frame's DC offset and calculates RMS and peak amplitude. The Bluetooth manager publishes the processed values. This keeps the hardware driver, signal processing, and BLE interface separate.

## Audio processing

- ADC input: GPIO0 / ADC1_CH0.
- Resolution: 12 bits (nominal raw range 0–4095).
- ADC attenuation: 11 dB.
- Sample rate target: 8 kHz.
- Frame size: 256 samples (32 ms target frame duration).
- BLE status publish interval: 1 second.

The MAX9814 has automatic gain control (AGC). Therefore, the transmitted RMS and peak values are **relative ADC-count measurements**, useful for comparing sound activity over time. They are not calibrated dB SPL or dBA readings. The AGC changes amplifier gain as sound levels change, so these values should not be treated as a calibrated sound-pressure meter.

The firmware subtracts each frame's mean ADC value before calculating RMS and peak. This removes the MAX9814 output's DC bias from the amplitude calculation.

## BLE interface

| BLE item | Value |
|---|---|
| Advertised name | `DMXENTIX-SOUND` |
| Service UUID | `d3601000-5a3b-4b3f-9c21-8f5a2d7c0101` |
| Command characteristic | `d3601001-5a3b-4b3f-9c21-8f5a2d7c0101` |
| Status characteristic | `d3601002-5a3b-4b3f-9c21-8f5a2d7c0101` |
| Command properties | Read, Write |
| Status properties | Read, Notify |

### Status payload

The status characteristic publishes a UTF-8 text payload approximately once per second:

```text
RMS=123,PK=456
```

| Field | Meaning |
|---|---|
| `RMS` | Frame RMS amplitude after DC-offset removal, in ADC counts |
| `PK` | Maximum absolute sample deviation from the frame mean, in ADC counts |

The `PING` command is supported for connection diagnostics. Write `PING` to the Command characteristic; the readable characteristic value changes to `PONG`. Other commands return `UNSUPPORTED` for this prototype.

Keep the service and characteristic UUIDs identical in firmware and the DMXENTIX Hub app's SoundNode profile/protocol.

## Software requirements

- Visual Studio Code with the PlatformIO IDE extension.
- ESP32 platform for PlatformIO.
- ESP32-C3 Super Mini.
- MAX9814 microphone breakout.
- USB data cable.

The BLE stack dependency is [NimBLE-Arduino](https://registry.platformio.org/libraries/h2zero/NimBLE-Arduino), pinned to version `2.5.2` in `platformio.ini`.

## Build and upload

Open the `DEVICES/DMXENTIX-SOUND-C3/` directory in VS Code (the folder containing `platformio.ini`). Then use PlatformIO Build and Upload, or run:

```bash
pio run
pio run --target upload
pio device monitor --baud 115200
```

If more than one serial port is connected, select the ESP32-C3 port in PlatformIO before uploading or opening the monitor.

## First validation sequence

1. Verify the MAX9814 pin labels and supply voltage requirements.
2. With the microphone disconnected from GPIO0, power the MAX9814 from 3.3 V and common GND.
3. Measure MAX9814 OUT relative to GND and confirm its minimum and maximum values remain inside the ESP32-C3 ADC input range.
4. Power off, then connect OUT to GPIO0.
5. Build and upload the firmware.
6. Open the serial monitor at 115200 baud. Confirm that startup reaches `Initialization complete` and BLE advertising starts as `DMXENTIX-SOUND`.
7. Observe `RMS`, `PK`, and `DC` values. Make quiet and loud sounds near the microphone and check that the relative readings respond.
8. Use nRF Connect to find `DMXENTIX-SOUND`, connect, discover the SoundNode service, and enable notifications on the Status characteristic.
9. Write `PING` to the Command characteristic and read it back; it should contain `PONG`.
10. Confirm the status notifications contain `RMS=...,PK=...` approximately once per second.

## Limitations

- RMS and peak are relative ADC counts, not calibrated sound-pressure units.
- MAX9814 AGC affects the relationship between acoustic pressure and measured output amplitude.
- Sampling uses paced Arduino ADC reads. The configured sample rate is a target; verify actual capture timing and signal quality on the C3 board.
- The prototype publishes telemetry but does not store audio, stream raw audio samples, or record speech.
- BLE security/pairing is not enabled in this initial diagnostic firmware.

# DMXENTIX Hub — EMOTIX360

**A multi-device mobile hub for connected DMXENTIX products.**

DMXENTIX Hub is the mobile application for discovering, connecting to, and managing supported DMXENTIX devices. It is built with .NET MAUI and targets Android and iOS from a shared application project.

The first integrated hardware product is **EMOTIX360**: an ESP32-S3-based interactive display that combines two MAX7219 8×8 LED matrices, an MPU6050 motion sensor, Wi-Fi connectivity, and Bluetooth Low Energy (BLE). The firmware provides motion-aware display animations, device telemetry, and remote configuration through the mobile application.

This repository contains both parts of the system:

- **DMXENTIX Hub** — the .NET MAUI mobile application and multi-device connection platform.
- **EMOTIX360 firmware** — the ESP32-S3 firmware, display and sensor drivers, animation system, Wi-Fi services, and BLE interface.

> **Product naming:** DMXENTIX is the brand, DMXENTIX Hub is the mobile application, and EMOTIX360 is the name of the existing hardware product. The EMOTIX360 BLE advertised name, GATT identifiers, and device protocol remain unchanged for compatibility.

> **Development status:** The Android application and EMOTIX360 BLE integration are under active development and have been tested on physical Android hardware. The project includes an iOS target, but physical iOS build, BLE, and lifecycle validation remain pending.

---

## Repository structure

The repository separates the EMOTIX360 firmware from the DMXENTIX Hub mobile application:

```text
DMXENTIX-Hub/
├── EMOTIX360 - S3/                 # ESP32-S3 PlatformIO firmware
│   ├── platformio.ini
│   ├── README.md                   # Firmware and hardware guide
│   ├── include/
│   ├── src/
│   └── ...
│
├── EMOTIX360_MAUI_APP/             # DMXENTIX Hub .NET MAUI solution
│   ├── DMXENTIX.Hub.sln
│   ├── DMXENTIX.Hub/               # Application project directory
│   │   ├── DMXENTIX.Hub.csproj
│   │   ├── App.xaml
│   │   ├── AppShell.xaml
│   │   ├── Models/
│   │   ├── Services/
│   │   ├── ViewModels/
│   │   ├── Views/
│   │   ├── Platforms/
│   │   └── Resources/
│   └── README.md                   # Mobile application guide
│
├── .gitignore
└── README.md                       # This repository overview
```

The mobile project directory and namespace migration should be completed before using this structure as the final repository layout. The firmware directory remains named `EMOTIX360 - S3` because it belongs to the EMOTIX360 hardware product.

### Project documentation

- [EMOTIX360 firmware and hardware guide](<EMOTIX360 - S3/README.md>)
- [DMXENTIX Hub mobile application guide](EMOTIX360_MAUI_APP/README.md)

---

## System overview

```text
┌─────────────────────────────────────────┐
│              DMXENTIX Hub               │
│               .NET MAUI                 │
│                                         │
│  Device discovery and saved devices     │
│  Multi-session BLE connection manager   │
│  Per-device workspaces                  │
│  Animation and brightness controls      │
│  Live status and Wi-Fi provisioning     │
└────────────────────┬────────────────────┘
                     │ Bluetooth Low Energy
                     │ GATT services and characteristics
┌────────────────────▼────────────────────┐
│                EMOTIX360                │
│                ESP32-S3                 │
│                                         │
│  BluetoothManager     WiFiManager       │
│  AnimationManager     MPU6050 task      │
│  ButtonManager        MAX7219 driver    │
└─────────────┬──────────────────┬────────┘
              │                  │
         MPU6050 sensor      2 × MAX7219
                           8×8 matrices
```

DMXENTIX Hub communicates directly with each supported device over BLE. A Wi-Fi connection is not required for local Bluetooth control. On EMOTIX360, Wi-Fi is a separate firmware service used for network connectivity, time synchronization, and the Wi-Fi provisioning workflow.

The application is designed around product profiles and device-specific sessions. This allows the hub to support additional DMXENTIX products without making the app's shared device-management layer depend on one product's BLE protocol.

---

## DMXENTIX Hub mobile application

DMXENTIX Hub is a multi-device management application, rather than an application dedicated only to EMOTIX360. The currently integrated product profile is EMOTIX360.

### Device Hub and discovery

- Scans for nearby supported BLE devices.
- Identifies devices using registered product profiles rather than accepting every nearby BLE peripheral.
- Displays discovery information such as product name, advertised Bluetooth name, device type, and signal strength.
- Reports Bluetooth adapter state and discovery progress.
- Supports the Android Bluetooth activation request when the user starts discovery while Bluetooth is disabled.
- Displays the currently connected device sessions and allows the user to select the active session for shared device controls.

### Multiple device sessions

- Maintains an independent BLE session for each connected device.
- Keeps each session's connection state, GATT references, status notifications, and write operations associated with the correct device.
- Supports more than one connected device session at a time, subject to the phone's Bluetooth capabilities and the connected peripherals.
- Uses a shared session-selection service so dashboard commands and provisioning operations are sent to the selected device rather than an unrelated connection.

### My Devices collection

- Saves successfully connected devices in a persistent local device registry.
- Restores saved device records when the application starts again.
- Shows connection state, product/device information, advertised Bluetooth name, and last-connected information.
- Supports device favourites and persists favourite state.
- Keeps the app's registry identifier separate from the Bluetooth transport identifier.
- Provides a dedicated workspace entry for each saved device.

### Automatic reconnection

- Includes a startup reconnection workflow for the remembered EMOTIX360 device.
- Reuses the remembered-device identity and BLE connection manager to reconnect to EMOTIX360.
- Keeps connection handling separate for each device session.
- Reconnection depends on Bluetooth being available, required platform permissions being granted, and the device being powered on and advertising.

### Individual device workspaces

- Opens a workspace for a selected saved device.
- Resolves the saved registry record to its matching Bluetooth device/session.
- Displays the selected device's connection state and identifying information.
- Tracks connection and reconnection changes while the workspace remains open.
- Does not silently substitute another connected device when the requested device is offline.

### EMOTIX360 dashboard and controls

For a connected EMOTIX360 device, the dashboard provides:

- Animation-mode selection for `IDLE`, `SAND_CLOCK`, `WATER`, `SAND_GRAVITY`, `HEART`, `RAIN`, and `WAVES`.
- MAX7219 matrix brightness control, using the firmware's 0–15 brightness range.
- Connection-state presentation and device status information.
- Live telemetry including pitch, roll, Wi-Fi connection state, time validity, active animation mode, and display brightness.
- Session-aware command and status handling so the dashboard follows the currently selected EMOTIX360 session.

### Wi-Fi provisioning

- Reads the phone's currently connected Wi-Fi network name when platform permissions and APIs permit it.
- Allows the user to refresh the phone's Wi-Fi information or use the detected SSID.
- Supports manual network-name and password entry.
- Sends Wi-Fi provisioning data to the selected device over its BLE provisioning characteristic.
- Displays provisioning operation status and the device's provisioning responses.
- Provides controls to view the count of app-sent network entries and clear those entries on the device.
- The EMOTIX360 firmware supports up to eight app-sent network entries.

The application does not read the phone's saved Wi-Fi password. The user must enter the password when it is required.

### Platform support

| Platform | Status |
|---|---|
| Android | Primary development and physical-device BLE test target. |
| iOS | .NET MAUI target included; physical-device BLE and lifecycle validation pending. |

The Android application includes Bluetooth permission handling and a foreground-service integration for maintaining BLE connection behaviour. iOS builds and deployment require a compatible Mac with Xcode paired to Visual Studio.

---

## EMOTIX360 ESP32-S3 firmware

The EMOTIX360 firmware runs on an ESP32-S3 DevKitC-1 and provides the hardware-side display, sensing, animation, wireless, and BLE functionality.

### Main firmware features

- ESP32-S3 DevKitC-1 as the main controller.
- PlatformIO project using the Arduino framework.
- Configuration for 16 MB flash and 8 MB OPI PSRAM.
- Two daisy-chained MAX7219 8×8 matrix modules, providing 128 LEDs.
- Logical 16×8 framebuffer for the combined display.
- MPU6050 accelerometer and gyroscope acquisition.
- Motion data used by the display animation system.
- Debounced tactile button for selecting display modes.
- Central AnimationManager responsible for display updates.
- Wi-Fi connection management and NTP time synchronization when credentials are configured.
- BLE GATT server advertising with the device name `EMOTIX360`.
- BLE command handling for animation-mode and brightness changes.
- Device status notifications containing motion, Wi-Fi, time, animation, and brightness information.
- BLE Wi-Fi provisioning support.

### Display modes

| Mode | Description |
|---|---|
| `IDLE` | Clears the matrix. |
| `SAND_CLOCK` | Hourglass grain-transfer animation, including orientation handling. |
| `WATER` | Particle-based liquid simulation using display geometry and motion input. |
| `SAND_GRAVITY` | Granular-particle simulation responding to gravity and movement. |
| `HEART` | Two 8×8 heart icons with a pulsing brightness effect. |
| `RAIN` | Gravity-oriented rain-drop animation. |
| `WAVES` | Travelling wave effect based on display coordinates and gravity direction. |

The button-controlled sequence is:

`IDLE → SAND_CLOCK → WATER → SAND_GRAVITY → HEART → RAIN → WAVES → IDLE`

The startup animation is a separate boot effect and is not part of the normal button-controlled sequence.

---

## Hardware overview

| Component | Purpose |
|---|---|
| ESP32-S3 DevKitC-1 | Main controller |
| 2 × MAX7219 8×8 modules | 128-LED display |
| MPU6050 | Accelerometer and gyroscope |
| Tactile push button | Animation-mode selection |
| Logic-level buffer | 3.3 V ESP32-S3 signals to 5 V MAX7219 logic |
| Regulated 5 V supply | MAX7219 power |

### ESP32-S3 pin assignment

| Device | Signal | ESP32-S3 GPIO |
|---|---|---:|
| MPU6050 | SDA | GPIO6 |
| MPU6050 | SCL | GPIO7 |
| MPU6050 | INT | GPIO4 |
| MAX7219 chain | CLK | GPIO12 |
| MAX7219 chain | DIN / MOSI | GPIO11 |
| MAX7219 chain | CS / LOAD | GPIO10 |
| Animation button | Input | GPIO14 |

Check the firmware's `pin_config.h` before wiring. The firmware configuration is the authority if these assignments change.

### Power and electrical notes

- Power the MAX7219 modules from a stable, regulated 5 V supply sized for the selected brightness and display load.
- Use a suitable 5 V logic buffer for MAX7219 DIN, CLK, and CS/LOAD signals.
- Do not apply 5 V logic signals directly to ESP32-S3 GPIO pins.
- Connect grounds between the ESP32-S3, sensor, level shifter, matrix modules, and external matrix supply.
- Avoid back-feeding the development board or USB supply from an external 5 V matrix supply.
- Check the MPU6050 module's voltage requirements before connecting it to 3.3 V.

For assembly details, see the [EMOTIX360 firmware and hardware guide](<EMOTIX360 - S3/README.md>).

---

## BLE interface

The EMOTIX360 BLE identity remains product-specific. Renaming the mobile application to DMXENTIX Hub does not change the advertised device name, GATT UUIDs, or the command/status protocol.

| BLE item | Value |
|---|---|
| Advertised device name | `EMOTIX360` |
| GATT service UUID | `4fafc201-1fb5-459e-8fcc-c5c9c331914b` |
| Command characteristic UUID | `beb5483e-36e1-4688-b7f5-ea07361b26a8` |
| Status characteristic UUID | `e3223119-9445-4e96-a4a1-85358c4046a2` |
| Wi-Fi provisioning characteristic UUID | `d3600001-5a3b-4b3f-9c21-8f5a2d7c0101` |
| Command properties | Read, Write |
| Status properties | Read, Notify |

### Device status payload

The status characteristic publishes a text payload in this format:

```text
P=12.3,R=-4.5,WIFI=1,TIME=1,MODE=HEART,BRIGHT=5
```

| Field | Meaning |
|---|---|
| `P` | Pitch in degrees. |
| `R` | Roll in degrees. |
| `WIFI` | Wi-Fi connected (`1`) or disconnected (`0`). |
| `TIME` | Time valid (`1`) or not valid (`0`). |
| `MODE` | Active animation name. |
| `BRIGHT` | MAX7219 brightness value, from 0 to 15. |

The application parses the status payload and associates received updates with the device session that produced them.

### App-to-device commands

DMXENTIX Hub sends validated command strings to the EMOTIX360 command characteristic.

Animation mode:

```text
MODE=IDLE
MODE=SAND_CLOCK
MODE=WATER
MODE=SAND_GRAVITY
MODE=HEART
MODE=RAIN
MODE=WAVES
```

Matrix brightness:

```text
BRIGHT=0
BRIGHT=5
BRIGHT=10
BRIGHT=15
```

The valid brightness range is 0–15. The firmware validates command values before applying them.

---

## Development environment

### Mobile application

The mobile app uses:

- Visual Studio 2026.
- .NET MAUI and the .NET 10 SDK.
- Target frameworks `net10.0-android` and `net10.0-ios`.
- `Plugin.BLE` for cross-platform Bluetooth Low Energy support.
- Android SDK and Android build tools installed through the MAUI workload.

Open `DMXENTIX.Hub.sln` from the mobile solution directory in Visual Studio 2026.

Build the Android target from PowerShell in the directory containing the solution:

```powershell
dotnet restore .\DMXENTIX.Hub.sln
dotnet build .\DMXENTIX.Hub\DMXENTIX.Hub.csproj -f net10.0-android
```

For iOS, pair Visual Studio with a compatible Mac that has Xcode installed, then select an iOS simulator or device as the deployment target.

### Current application identity

| Setting | Value |
|---|---|
| Application display name | `DMXENTIX Hub` |
| Assembly name | `DMXENTIX.Hub` |
| Root namespace | `DMXENTIX.Hub` after namespace migration |
| Android/iOS application ID | `com.dmxddrx.emotix360` |
| Target frameworks | `net10.0-android;net10.0-ios` |

The current application ID is intentionally retained during the rebrand so the existing Android installation identity is not changed as part of the project/namespace rename. Review the Android package and iOS bundle identifier separately before public distribution.

### ESP32-S3 firmware

The firmware is developed using:

- Visual Studio Code.
- PlatformIO IDE extension.
- Espressif ESP32 platform for PlatformIO.
- ESP32-S3 DevKitC-1 and a USB data cable.

The firmware configuration targets:

- Board: `esp32-s3-devkitc-1`.
- CPU: 240 MHz.
- Flash: 16 MB.
- PSRAM: 8 MB OPI.
- Partition table: `default_16MB.csv`.
- Serial monitor: 115200 baud.

Open the firmware directory containing `platformio.ini` in VS Code. Build, upload, and monitor using PlatformIO:

```bash
pio run
pio run --target upload
pio device monitor --baud 115200
```

Keep local Wi-Fi credentials and other secrets in the private firmware configuration. Do not commit real SSIDs, passwords, certificates, or other credentials.

---

## Repository setup

Clone the repository:

```bash
git clone https://github.com/Dmxddrx/DMXENTIX-Hub.git
cd DMXENTIX-Hub
```

Then open the relevant project:

- **Firmware:** open `EMOTIX360 - S3/` in VS Code with PlatformIO.
- **Mobile app:** open `EMOTIX360_MAUI_APP/DMXENTIX.Hub.sln` in Visual Studio 2026.

Build the firmware and mobile application independently. They use separate toolchains and dependency sets.

---

## Development roadmap

1. Complete the DMXENTIX Hub project-directory and namespace migration.
2. Finish the shared dashboard extraction and validate it in both the Home tab and the EMOTIX360 device workspace.
3. Finalise the DMXENTIX Hub icon, splash screen, and application visual identity.
4. Validate multi-device connection and recovery behaviour across a broader range of Android devices.
5. Pair with a Mac and validate iOS builds, BLE permissions, connections, notifications, and app lifecycle behaviour.
6. Add additional DMXENTIX product profiles and protocol adapters as new hardware products become available.

---

## Limitations and validation notes

- Android is the primary tested mobile platform. iOS support is included in the project targets but still requires physical-device validation.
- BLE behaviour depends on phone hardware, operating-system permissions, Bluetooth adapter state, and the peripheral's advertising/connection state.
- Multiple concurrent BLE sessions are subject to platform and hardware connection limits.
- The Wi-Fi SSID visibility workflow is subject to Android/iOS privacy permissions and platform API behaviour. The app cannot read a saved Wi-Fi password from the phone.
- Water and SandGravity are particle simulations, not full computational-fluid-dynamics models.
- MPU6050 tilt estimates can be affected by vibration and linear acceleration. The MPU6050 has no magnetometer and does not provide absolute compass heading.
- Physical LED-neighbour geometry depends on the matrix placement and may require calibration for the final enclosure.
- Verify hardware, electrical levels, sensor axes, BLE behaviour, Wi-Fi provisioning, and mobile-app operation on real devices before treating the system as production-ready.

---

## Repository

[github.com/Dmxddrx/DMXENTIX-Hub](https://github.com/Dmxddrx/DMXENTIX-Hub)

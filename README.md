# DMXENTIX Hub

**A multi-device companion platform for connected DMXENTIX products.**

DMXENTIX Hub is the mobile application and device-integration project within the DMXENTIX ecosystem. It provides a central place to discover, connect to, manage, and interact with supported hardware products.

The repository currently contains the **DMXENTIX Hub** .NET MAUI mobile application and the firmware for **EMOTIX360**, an ESP32-S3-based interactive display. EMOTIX360 is a hardware product name; DMXENTIX is the brand, and DMXENTIX Hub is the application name.

| Identity | Name | Purpose |
|---|---|---|
| Brand | **DMXENTIX** | Engineering and technology brand |
| Mobile application | **DMXENTIX Hub** | Multi-device companion application |
| Hardware product | **EMOTIX360** | Motion-responsive ESP32-S3 display |

> **Project status:** The Android application and EMOTIX360 firmware are under active development. The application includes BLE discovery and connection workflows, session-based device management, a persistent device collection, device workspaces, live status handling, animation and brightness commands, automatic reconnection support, and Wi-Fi provisioning. Android is the primary development and test platform. The iOS target is included in the MAUI project, but full iOS behaviour still requires validation on Apple hardware.

---

## Repository structure

The repository is organised into the mobile application and hardware device projects:

```text
DMXENTIX-Hub/
├── APP/
│   ├── DMXENTIX.Hub.sln
│   ├── DMXENTIX.Hub/
│   │   ├── DMXENTIX.Hub.csproj
│   │   ├── App.xaml
│   │   ├── AppShell.xaml
│   │   ├── Models/
│   │   ├── Platforms/
│   │   ├── Resources/
│   │   ├── Services/
│   │   ├── ViewModels/
│   │   └── Views/
│   └── README.md                    # Mobile application development guide
│
├── DEVICES/
│   └── EMOTIX360 - S3/
│       ├── platformio.ini
│       ├── README.md                # Firmware and hardware guide
│       ├── include/
│       ├── src/
│       └── ...
│
├── .gitignore
└── README.md                        # This repository overview
```

The structure above describes the intended repository layout after the local folder reorganisation. Keep each project's own README alongside that project.

### Project documentation

- [EMOTIX360 ESP32-S3 firmware and hardware guide](<DEVICES/EMOTIX360 - S3/README.md>)
- [DMXENTIX Hub mobile application guide](APP/README.md)

---

## System overview

```text
┌──────────────────────────────────────────────────────┐
│                   DMXENTIX Hub                       │
│                  .NET MAUI app                       │
│                                                      │
│  Device discovery and connection                     │
│  Multi-device session management                     │
│  Saved devices, favourites and workspaces            │
│  Animation, brightness and Wi-Fi provisioning        │
│  Live device status                                  │
└──────────────────────────┬───────────────────────────┘
                           │
                 Bluetooth Low Energy (BLE)
                           │
┌──────────────────────────▼───────────────────────────┐
│                     EMOTIX360                        │
│                    ESP32-S3                           │
│                                                      │
│  BluetoothManager       WiFiManager                  │
│  AnimationManager       MPU6050 sensor task          │
│  ButtonManager          MAX7219 matrix driver        │
└────────────────────┬─────────────────┬───────────────┘
                     │                 │
               MPU6050 sensor     2 × MAX7219
                                 8×8 LED matrices
```

DMXENTIX Hub communicates directly with EMOTIX360 over BLE. A Wi-Fi connection is not required for local device discovery, connection, animation control, or status monitoring. Wi-Fi is configured separately on the ESP32-S3 through the app's BLE Wi-Fi provisioning workflow and is used by the firmware for network connectivity and time synchronisation when configured.

The application architecture is designed to support multiple product profiles and multiple device sessions. EMOTIX360 is the currently integrated device profile; additional products can be added through their own profiles and protocols without changing the central app identity.

---

## Main features

### DMXENTIX Hub mobile application

The application is built with **C# and .NET MAUI** and targets Android and iOS from a shared project.

#### Device Hub and discovery

- Central Device Hub landing page for discovering and connecting to supported products.
- BLE scanning and discovery through registered device profiles.
- Product-profile matching so unsupported peripherals are not presented as supported devices.
- Bluetooth state presentation and an explicit Android Bluetooth activation workflow.
- Connection progress, discovery status, and connected-session information.

#### Multi-device connection architecture

- Separate BLE session objects for individual connected devices.
- Connection manager that tracks sessions and their connection-state changes.
- Session-specific GATT operations, status notifications, and command writes.
- Shared selected-device context for pages that operate on the currently selected session.
- Architecture intended to prevent a command or status update for one device from being applied to another device's session.

#### My Devices collection

- Persistent local device registry.
- Saved device records with product identity and Bluetooth transport identity stored separately.
- Connection status and last-connected information.
- Device favourites.
- Device metadata such as advertised name, supported capabilities, and available firmware/protocol information.
- Device records remain available when the device is offline or the app is restarted.

#### Individual device workspaces

- A dedicated workspace for each saved device.
- Workspace resolves the selected saved record to its matching BLE session.
- Connection state is refreshed when the target session disconnects or reconnects.
- Offline devices remain associated with their own workspace; the workspace does not silently switch to a different connected device.
- EMOTIX360's dashboard can be presented within its device workspace.

#### EMOTIX360 dashboard and controls

- Animation-mode selection for the supported display modes.
- MAX7219 matrix brightness control.
- Live device status parsing, including pitch, roll, Wi-Fi state, time validity, active mode, and brightness.
- Connection status, dashboard messages, and device overview information.
- Commands are sent through the selected device session rather than through a single global BLE connection.

#### Automatic connection support

- Startup connection manager for a previously remembered EMOTIX360 device.
- Attempts to reconnect to the remembered Bluetooth identity.
- Discovery fallback when the remembered device is not immediately available.
- Android foreground-service integration to support continued connection management.
- Manual disconnect is respected by the compatibility connection workflow.

> The current remembered-device automatic connection workflow is distinct from a future per-device auto-reconnect preference system for every saved product. Do not assume every saved device has independent automatic reconnection enabled.

#### Wi-Fi provisioning

- Reads the phone's currently connected Wi-Fi network name where the platform permits it.
- Allows the user to enter an SSID and password manually.
- Can send Wi-Fi provisioning data to the selected compatible device over BLE.
- Receives and displays provisioning status from the device session.
- Provides controls for managing the app-sent network entries supported by the current EMOTIX360 workflow.

Android is the primary platform for current BLE development and testing. The project includes an iOS target and Bluetooth usage-description configuration, but iOS BLE scanning, connection, background behaviour, and provisioning require validation on a Mac and physical iOS device.

### ESP32-S3 firmware

The EMOTIX360 firmware is a PlatformIO project using the Arduino framework.

- ESP32-S3 DevKitC-1 as the main controller.
- Firmware configuration for 16 MB flash and 8 MB OPI PSRAM.
- Two daisy-chained MAX7219 8×8 LED matrix modules, providing 128 LEDs.
- Logical 16×8 display framebuffer.
- MPU6050 accelerometer and gyroscope acquisition through a dedicated sensor task.
- Thread-safe motion-data snapshots for animation modules.
- Debounced tactile button for selecting display modes.
- Central AnimationManager task responsible for matrix updates.
- Wi-Fi connection management and NTP time synchronisation when credentials are configured.
- BLE GATT server advertising with the device name `EMOTIX360`.
- BLE command processing for animation-mode and brightness updates.
- BLE Wi-Fi provisioning support.

### Display modes

| Mode | Description |
|---|---|
| `IDLE` | Clears the matrix. |
| `SAND_CLOCK` | Hourglass grain-transfer animation with orientation handling. |
| `WATER` | Particle-based liquid simulation using display geometry and motion input. |
| `SAND_GRAVITY` | Granular-particle simulation responding to gravity and movement. |
| `HEART` | Two 8×8 heart icons with a pulsing brightness effect. |
| `RAIN` | Gravity-oriented rain-drop animation. |
| `WAVES` | Travelling wave effect based on display coordinates and gravity direction. |

The button-controlled sequence is:

```text
IDLE → SAND_CLOCK → WATER → SAND_GRAVITY → HEART → RAIN → WAVES → IDLE
```

The startup animation is a separate boot effect and is not part of the button-controlled sequence. A selectable mode remains active until another mode is selected through the button or a supported BLE command.

---

## Hardware overview

| Component | Purpose |
|---|---|
| ESP32-S3 DevKitC-1 | Main controller |
| 2 × MAX7219 8×8 modules | Combined 128-LED display |
| MPU6050 | Accelerometer and gyroscope |
| Tactile push button | Display-mode selection |
| Logic-level buffer | Interfaces 3.3 V ESP32-S3 signals with 5 V MAX7219 logic |
| Regulated 5 V supply | MAX7219 module power |

### Documented ESP32-S3 pin assignment

| Device | Signal | ESP32-S3 GPIO |
|---|---|---:|
| MPU6050 | SDA | GPIO6 |
| MPU6050 | SCL | GPIO7 |
| MPU6050 | INT | GPIO4 |
| MAX7219 chain | CLK | GPIO12 |
| MAX7219 chain | DIN / MOSI | GPIO11 |
| MAX7219 chain | CS / LOAD | GPIO10 |
| Animation button | Input | GPIO14 |

Check the firmware's pin configuration header before wiring. The pin configuration in the firmware is authoritative if assignments change.

### Power and electrical notes

- Power the MAX7219 modules from a stable, regulated 5 V supply sized for the selected brightness and display load.
- Use a suitable 5 V logic buffer for MAX7219 DIN, CLK, and CS/LOAD signals.
- Do not apply 5 V logic signals directly to ESP32-S3 GPIO pins.
- Connect grounds between the ESP32-S3, sensor, logic-level buffer, matrix modules, and external matrix supply.
- Avoid back-feeding the development board or USB supply from an external 5 V matrix supply.
- Check the MPU6050 module's voltage requirements before connecting it to 3.3 V.

For assembly and firmware-specific details, see the [EMOTIX360 firmware and hardware guide](<DEVICES/EMOTIX360 - S3/README.md>).

---

## BLE interface

The EMOTIX360 firmware and DMXENTIX Hub device profile use the following BLE identifiers. Keep these values stable when updating the application or firmware together.

| BLE item | Value |
|---|---|
| Advertised device name | `EMOTIX360` |
| GATT service UUID | `4fafc201-1fb5-459e-8fcc-c5c9c331914b` |
| Command characteristic UUID | `beb5483e-36e1-4688-b7f5-ea07361b26a8` |
| Status characteristic UUID | `e3223119-9445-4e96-a4a1-85358c4046a2` |
| Wi-Fi provisioning characteristic UUID | `d3600001-5a3b-4b3f-9c21-8f5a2d7c0101` |

The command characteristic is used for device commands. The status characteristic supports reading and notifications. The Wi-Fi provisioning characteristic is used by the app's provisioning workflow.

### Device status payload

The status characteristic publishes a text payload in this format:

```text
P=12.3,R=-4.5,WIFI=1,TIME=1,MODE=HEART,BRIGHT=5
```

| Field | Meaning |
|---|---|
| `P` | Pitch in degrees |
| `R` | Roll in degrees |
| `WIFI` | Wi-Fi connected (`1`) or disconnected (`0`) |
| `TIME` | Time valid (`1`) or not valid (`0`) |
| `MODE` | Active animation mode |
| `BRIGHT` | MAX7219 brightness value, from 0 to 15 |

The mobile app parses the incoming status payload and updates the dashboard for the associated device session.

### Supported command format

The app sends the following command strings through the selected device session:

```text
MODE=IDLE
MODE=SAND_CLOCK
MODE=WATER
MODE=SAND_GRAVITY
MODE=HEART
MODE=RAIN
MODE=WAVES

BRIGHT=0
BRIGHT=5
BRIGHT=10
BRIGHT=15
```

The firmware validates animation names and brightness values before applying them. The supported brightness range is `0` to `15`.

### Wi-Fi provisioning

Wi-Fi provisioning uses the dedicated BLE characteristic and the EMOTIX360 provisioning frame protocol. The app sends framed provisioning data to the selected device session, and the firmware processes received frames through its Wi-Fi manager. Keep the app's provisioning encoder and firmware frame parser compatible when changing this interface.

---

## Development environment

### ESP32-S3 firmware

Required tools:

- Visual Studio Code.
- PlatformIO IDE extension.
- Espressif ESP32 platform for PlatformIO.
- ESP32-S3 DevKitC-1 and a USB data cable.

The documented firmware configuration targets:

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

Provide local Wi-Fi credentials through the firmware's private secrets/configuration file. Do not commit real SSIDs, passwords, certificates, or other credentials. The repository `.gitignore` excludes common local secrets and build outputs.

### DMXENTIX Hub mobile application

The application is located in `APP/` and uses:

- Visual Studio 2026.
- .NET MAUI workload.
- .NET 10 target frameworks: `net10.0-android` and `net10.0-ios`.
- Android SDK and Android build tools supplied/configured through Visual Studio.
- A physical Android phone for final BLE integration testing.

Open `DMXENTIX.Hub.sln` from the `APP/` directory in Visual Studio 2026.

Restore and build from PowerShell at the repository root:

```powershell
dotnet restore .\APP\DMXENTIX.Hub.sln
dotnet build .\APP\DMXENTIX.Hub\DMXENTIX.Hub.csproj -f net10.0-android
```

Android can be built and tested from Windows. iOS compilation and deployment require a compatible Mac build host with Xcode, paired to Visual Studio. The iOS target is included in the project, but it should not be considered validated until built and tested on Apple hardware.

---

## Repository setup

Clone the repository:

```bash
git clone https://github.com/Dmxddrx/DMXENTIX-Hub.git
cd DMXENTIX-Hub
```

Then open the relevant project:

- Firmware: open `DEVICES/EMOTIX360 - S3/` in VS Code with PlatformIO.
- Mobile application: open `APP/DMXENTIX.Hub.sln` in Visual Studio 2026.

Build the firmware and mobile application independently. They use separate toolchains and dependency sets, and communicate at runtime over BLE.

---

## Development roadmap

The core application and device integration are in place and are being refined. Planned development work includes:

1. Complete the project-wide namespace and source-level rebrand to `DMXENTIX.Hub` while retaining EMOTIX360 device identifiers.
2. Finalise the DMXENTIX Hub app icon, splash screen, and remaining application-facing branding.
3. Complete and maintain the mobile-app-specific documentation in `APP/README.md`.
4. Expand the device-profile and protocol architecture as additional DMXENTIX hardware products are developed.
5. Validate the complete BLE, workspace, and Wi-Fi provisioning workflows on additional Android devices.
6. Pair with a Mac and validate the iOS target, including BLE permissions, discovery, connection, notifications, and lifecycle behaviour.

---

## Limitations and validation notes

- Water and SandGravity are discrete particle simulations, not full computational-fluid-dynamics models.
- Physical LED-neighbour geometry depends on the actual matrix placement and may require calibration for the final enclosure.
- MPU6050 tilt estimates can be affected by vibration and linear acceleration. The MPU6050 has no magnetometer and does not provide absolute compass heading.
- SandClock and SandGravity are separate animations; changes to one should not unintentionally alter the other.
- BLE and Wi-Fi behaviour depends on the firmware, mobile operating system, permissions, radio conditions, and connected hardware.
- The iOS target has not yet been confirmed on a physical iOS device.
- Verify hardware wiring, electrical levels, sensor axes, BLE behaviour, and provisioning on the assembled device before treating the system as production-ready.

---

## Repository

- GitHub: [Dmxddrx/DMXENTIX-Hub](https://github.com/Dmxddrx/DMXENTIX-Hub)
- Mobile application: **DMXENTIX Hub**
- Integrated hardware product: **EMOTIX360**

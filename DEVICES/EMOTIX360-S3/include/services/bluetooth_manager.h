
#ifndef BLUETOOTH_MANAGER_H
#define BLUETOOTH_MANAGER_H

#include <Arduino.h>
#include <stddef.h>

namespace BluetoothManager {

// Initialize BLE GATT server and start advertising.
bool begin();

// Start the Bluetooth maintenance task.
bool startTask();

// Task status.
bool isTaskRunning();

// Check BLE connection status.
bool isConnected();

// Periodic BLE maintenance.
void update();

// Publish device status to the mobile application.
void setStatus(
    float pitchDeg,
    float rollDeg,
    bool wifiConnected,
    bool timeValid,
    const char *modeName,
    uint8_t brightness
);

// Retrieve the latest command received from the mobile app.
bool takeCommand(char *out, size_t outSize);

// ------------------------------------------------------------
// WIFI PROVISIONING STATUS
// ------------------------------------------------------------

// Publish Wi-Fi provisioning status through the dedicated
// Wi-Fi provisioning characteristic.
void setWifiProvisioningStatus(
    const char *status,
    uint8_t storedCount
);

}

#endif

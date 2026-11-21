#ifndef WIFI_MANAGER_H
#define WIFI_MANAGER_H

#include <Arduino.h>
#include <time.h>

namespace WifiManager {

bool begin();
bool startTask();

bool isTaskRunning();
bool isConnected();

bool isTimeValid();
bool isTimeSynced();

bool getLocalTime(struct tm &out);

bool getIPAddress(char *buffer, size_t bufferSize);
bool getSSID(char *buffer, size_t bufferSize);

// ------------------------------------------------------------
// WIFI PROVISIONING
// ------------------------------------------------------------

// Queue a Wi-Fi provisioning frame received through BLE.
// Frames are processed by the Wi-Fi manager task.
bool enqueueProvisioningFrame(
    const char *frame,
    size_t length
);

// Return the number of app-sent networks stored in NVS.
uint8_t getStoredNetworkCount();

}

#endif
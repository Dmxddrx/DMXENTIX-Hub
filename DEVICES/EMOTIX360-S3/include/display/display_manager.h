#ifndef DISPLAY_MANAGER_H
#define DISPLAY_MANAGER_H

#include <Arduino.h>
#include <time.h>

namespace DisplayManager {

struct Status {

    bool timeValid = false;
    struct tm localTime = {};

    bool mpuReady = false;
    float pitchDeg = 0.0f;
    float rollDeg = 0.0f;

    bool wifiConnected = false;
    bool ntpSynced = false;
    bool bleConnected = false;

    char ipAddress[16] = "-";
    char modeName[24] = "EMOTIX360";

    uint8_t page = 0;
    uint8_t brightness = 5;

    // Manual eye-demo state.
    bool eyeDemoActive = false;
    uint8_t eyeDemoStep = 0;

    // Changes each time a new eye-demo session starts.
    // Prevents an old test state from being reused.
    uint32_t eyeDemoSession = 0;

    bool externalPowerPresent = false;
    int batteryPercent = -1;
};

bool begin();
bool startTask();
bool isReady();

void setNetworkStatus(
    bool wifiConnected,
    bool ntpSynced,
    const char *ipAddress
);

void setPowerStatus(
    bool externalPower,
    int batteryPercent
);

void setClockStatus(
    const struct tm &time,
    bool valid
);

void setMPUStatus(
    bool ready,
    float pitchDeg,
    float rollDeg
);

void setApplicationStatus(
    uint8_t page,
    const char *modeName
);

void setBluetoothStatus(bool connected);
void setAnimationMode(const char *modeName);
bool nextPage();
// Advance or exit the manually controlled eye behaviour demo.
bool nextEyeDemoStep();
bool exitEyeDemo();

void requestUpdate();

void showStartupMessage(const char *message);
void logMessage(const char *format, ...);

}

#endif
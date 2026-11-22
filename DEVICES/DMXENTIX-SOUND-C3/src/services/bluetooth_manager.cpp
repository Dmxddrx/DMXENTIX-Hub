#include "bluetooth_manager.h"

#include "ble_config.h"
#include "device_config.h"

#include <Arduino.h>
#include <NimBLEDevice.h>
#include <string>

namespace BluetoothManager {
namespace {

NimBLEServer *bleServer = nullptr;
NimBLECharacteristic *commandCharacteristic = nullptr;
NimBLECharacteristic *statusCharacteristic = nullptr;

// NimBLE-Arduino 2.5.2 callback instances are registered by pointer.
// Keep these objects alive for the lifetime of the BLE server.
class SoundNodeServerCallbacks final : public NimBLEServerCallbacks {
public:
    void onConnect(
        NimBLEServer *server,
        NimBLEConnInfo &connInfo
    ) override
    {
        (void)server;

        Serial.printf(
            "[BLE] Client connected: %s\n",
            connInfo.getAddress().toString().c_str()
        );
    }
};

class SoundNodeCommandCallbacks final : public NimBLECharacteristicCallbacks {
public:
    // NimBLE-Arduino 2.5.2 uses this two-argument, void callback.
    // The written data is available through characteristic->getValue().
    void onWrite(
        NimBLECharacteristic *characteristic,
        NimBLEConnInfo &connInfo
    ) override
    {
        (void)connInfo;

        if (characteristic == nullptr) {
            return;
        }

        // Work with a local copy so the value remains valid while parsing.
        const NimBLEAttValue receivedValue = characteristic->getValue();

        if (receivedValue.length() == 0) {
            return;
        }

        const std::string command(
            reinterpret_cast<const char *>(receivedValue.data()),
            receivedValue.length()
        );

        // PING is a small protocol check used by BLE diagnostic tools
        // and by the DMXENTIX Hub protocol during initial integration.
        // The response is stored as the readable Command characteristic
        // value. A BLE client can read the characteristic to obtain it.
        if (command == "PING") {
            characteristic->setValue("PONG");
            Serial.println("[BLE] PING received; Command value updated to PONG");
        } else {
            characteristic->setValue("UNSUPPORTED");
            Serial.println("[BLE] Unsupported command received");
        }
    }
};

SoundNodeServerCallbacks serverCallbacks;
SoundNodeCommandCallbacks commandCallbacks;

} // namespace

bool begin()
{
    if (bleServer != nullptr) {
        return true;
    }

    Serial.println("[BLE] Initializing SoundNode BLE GATT server");

    NimBLEDevice::init(DEVICE_NAME);
    NimBLEDevice::setMTU(BLE_PREFERRED_MTU);

    bleServer = NimBLEDevice::createServer();
    if (bleServer == nullptr) {
        Serial.println("[BLE] Failed to create GATT server");
        return false;
    }

    // The callback instance is static, so do not let NimBLE delete it.
    bleServer->setCallbacks(&serverCallbacks, false);
    bleServer->advertiseOnDisconnect(true);

    NimBLEService *service =
        bleServer->createService(SOUNDNODE_SERVICE_UUID);

    if (service == nullptr) {
        Serial.println("[BLE] Failed to create SoundNode service");
        bleServer = nullptr;
        return false;
    }

    commandCharacteristic = service->createCharacteristic(
        SOUNDNODE_COMMAND_UUID,
        NIMBLE_PROPERTY::READ | NIMBLE_PROPERTY::WRITE
    );

    if (commandCharacteristic == nullptr) {
        Serial.println("[BLE] Failed to create Command characteristic");
        return false;
    }

    commandCharacteristic->setValue("READY");
    commandCharacteristic->setCallbacks(&commandCallbacks);

    statusCharacteristic = service->createCharacteristic(
        SOUNDNODE_STATUS_UUID,
        NIMBLE_PROPERTY::READ | NIMBLE_PROPERTY::NOTIFY
    );

    if (statusCharacteristic == nullptr) {
        Serial.println("[BLE] Failed to create Status characteristic");
        return false;
    }

    statusCharacteristic->setValue("RMS=0,PK=0,TEMP=NA");

    // In NimBLE-Arduino 2.x services are registered when the server starts.
    // Calling service->start() is deprecated and has no effect.
    if (!bleServer->start()) {
        Serial.println("[BLE] Failed to start GATT server");
        return false;
    }

    NimBLEAdvertising *advertising = NimBLEDevice::getAdvertising();

    if (advertising == nullptr) {
        Serial.println("[BLE] Failed to obtain advertising instance");
        return false;
    }

    // Enable scan response BEFORE setting the advertised name.
    // The 128-bit service UUID stays in the main advertisement,
    // while the full device name is placed in the scan response.
    advertising->enableScanResponse(true);

    if (!advertising->addServiceUUID(SOUNDNODE_SERVICE_UUID)) {
        Serial.println("[BLE] Failed to add SoundNode service UUID");
        return false;
    }

    if (!advertising->setName(DEVICE_NAME)) {
        Serial.println("[BLE] Failed to set SoundNode advertised name");
        return false;
    }

    if (!advertising->start()) {
        Serial.println("[BLE] Failed to start advertising");
        return false;
    }

    Serial.printf("[BLE] Advertising as %s\n", DEVICE_NAME);
    Serial.printf("[BLE] Service UUID: %s\n", SOUNDNODE_SERVICE_UUID);

    return true;
}

bool isConnected()
{
    return (
        bleServer != nullptr &&
        bleServer->getConnectedCount() > 0
    );
}

void publishTelemetry(
    uint16_t rms,
    uint16_t peak,
    float chipTemperatureC,
    bool chipTemperatureValid
)
{
    if (statusCharacteristic == nullptr) {
        return;
    }

    char payload[64];

    if (chipTemperatureValid) {

        // Temperature is transmitted in degrees Celsius.
        snprintf(
            payload,
            sizeof(payload),
            "RMS=%u,PK=%u,TEMP=%.1f",
            static_cast<unsigned int>(rms),
            static_cast<unsigned int>(peak),
            static_cast<double>(chipTemperatureC)
        );

    } else {

        snprintf(
            payload,
            sizeof(payload),
            "RMS=%u,PK=%u,TEMP=NA",
            static_cast<unsigned int>(rms),
            static_cast<unsigned int>(peak)
        );
    }

    statusCharacteristic->setValue(payload);

    if (isConnected()) {
        statusCharacteristic->notify();
    }
}

} // namespace BluetoothManager

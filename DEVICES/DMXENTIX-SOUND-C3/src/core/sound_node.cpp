#include "sound_node.h"
#include "chip_temperature_service.h"

#include "audio_config.h"
#include "audio_processing_service.h"
#include "bluetooth_manager.h"

#include <Arduino.h>

namespace SoundNode {
namespace {

uint32_t lastPublishTime = 0;

constexpr uint32_t CHIP_TEMP_READ_INTERVAL_MS = 2000;

uint32_t lastTemperatureReadTime = 0;

float chipTemperatureC = 0.0f;
bool chipTemperatureValid = false;

}

bool begin()
{
    Serial.println();
    Serial.println("========================================");
    Serial.println("       DMXENTIX SoundNode");
    Serial.println("       ESP32-C3 + MAX9814");
    Serial.println("========================================");

    if (!AudioProcessingService::begin()) {
        Serial.println("[SOUND] MAX9814 ADC initialization failed");
        return false;
    }

    Serial.println("[SOUND] MAX9814 ADC input initialized");

    // ----------------------------------------------------
    // INITIALIZE INTERNAL CHIP TEMPERATURE SENSOR
    // ----------------------------------------------------

    if (ChipTemperatureService::begin()) {

        chipTemperatureValid =
            ChipTemperatureService::readCelsius(chipTemperatureC);

        if (chipTemperatureValid) {
            Serial.printf(
                "[TEMP] Initial chip temperature: %.1f C\n",
                chipTemperatureC
            );
        } else {
            Serial.println(
                "[TEMP] Initial temperature reading unavailable"
            );
        }

    } else {
        // Temperature monitoring is optional.
        // SoundNode should continue operating if initialization fails.
        Serial.println(
            "[TEMP] Temperature monitoring unavailable"
        );
    }

    if (!BluetoothManager::begin()) {
        Serial.println("[SOUND] BLE initialization failed");
        return false;
    }

    lastPublishTime = millis();
    Serial.println("[SOUND] Initialization complete");
    return true;
}

void update()
{
    AudioProcessingService::AudioMetrics metrics = {};

    if (!AudioProcessingService::capture(metrics)) {
        return;
    }

    const uint32_t now = millis();

    // ----------------------------------------------------
    // READ ESP32-C3 INTERNAL TEMPERATURE
    // ----------------------------------------------------

    if (now - lastTemperatureReadTime >= CHIP_TEMP_READ_INTERVAL_MS) {

        lastTemperatureReadTime = now;

        float newTemperatureC = 0.0f;

        if (ChipTemperatureService::readCelsius(newTemperatureC)) {
            chipTemperatureC = newTemperatureC;
            chipTemperatureValid = true;
        } else {
            chipTemperatureValid = false;

            Serial.println(
                "[TEMP] Temperature reading failed"
            );
        }
    }

    if (now - lastPublishTime >= AUDIO_PUBLISH_INTERVAL_MS) {
        lastPublishTime = now;

        BluetoothManager::publishTelemetry(
            metrics.rms,
            metrics.peak,
            chipTemperatureC,
            chipTemperatureValid
        );

        if (chipTemperatureValid) {

            Serial.printf(
                "[SOUND] RMS=%u, PK=%u, DC=%u, CHIP_TEMP=%.1f C\n",
                static_cast<unsigned int>(metrics.rms),
                static_cast<unsigned int>(metrics.peak),
                static_cast<unsigned int>(metrics.dcOffset),
                static_cast<double>(chipTemperatureC)
            );

        } else {

            Serial.printf(
                "[SOUND] RMS=%u, PK=%u, DC=%u, CHIP_TEMP=UNAVAILABLE\n",
                static_cast<unsigned int>(metrics.rms),
                static_cast<unsigned int>(metrics.peak),
                static_cast<unsigned int>(metrics.dcOffset)
            );
        }
    }
}

}

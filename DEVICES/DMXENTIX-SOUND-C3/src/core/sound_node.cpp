#include "sound_node.h"

#include "audio_config.h"
#include "audio_processing_service.h"
#include "bluetooth_manager.h"

#include <Arduino.h>

namespace SoundNode {
namespace {

uint32_t lastPublishTime = 0;

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

    if (now - lastPublishTime >= AUDIO_PUBLISH_INTERVAL_MS) {
        lastPublishTime = now;

        BluetoothManager::publishAudioMetrics(
            metrics.rms,
            metrics.peak
        );

        Serial.printf(
            "[SOUND] RMS=%u, PK=%u, DC=%u\n",
            static_cast<unsigned int>(metrics.rms),
            static_cast<unsigned int>(metrics.peak),
            static_cast<unsigned int>(metrics.dcOffset)
        );
    }
}

}

#include "max9814_driver.h"

#include "audio_config.h"
#include "pin_config.h"

#include <Arduino.h>
#include <esp_timer.h>

namespace Max9814Driver {
namespace {

constexpr int64_t SAMPLE_PERIOD_US =
    1000000LL / static_cast<int64_t>(AUDIO_SAMPLE_RATE_HZ);

bool initialized = false;

}

bool begin()
{
    if (initialized) {
        return true;
    }

    pinMode(MICROPHONE_OUT_PIN, INPUT);
    analogReadResolution(AUDIO_ADC_RESOLUTION_BITS);
    analogSetPinAttenuation(MICROPHONE_OUT_PIN, ADC_11db);

    // Discard the first conversions after ADC configuration.
    (void)analogRead(MICROPHONE_OUT_PIN);
    (void)analogRead(MICROPHONE_OUT_PIN);

    initialized = true;
    return true;
}

bool captureSamples(uint16_t *samples, size_t sampleCount)
{
    if (!initialized || samples == nullptr || sampleCount == 0) {
        return false;
    }

    int64_t nextSampleTime = esp_timer_get_time();

    for (size_t i = 0; i < sampleCount; ++i) {
        // Pace ADC reads to the configured sample interval.
        while (true) {
            const int64_t now = esp_timer_get_time();
            const int64_t remaining = nextSampleTime - now;

            if (remaining <= 0) {
                break;
            }

            if (remaining > 20) {
                delayMicroseconds(
                    static_cast<uint32_t>(remaining - 10)
                );
            }
        }

        samples[i] = analogRead(MICROPHONE_OUT_PIN);
        nextSampleTime += SAMPLE_PERIOD_US;

        // If an ADC conversion ever overruns the target interval,
        // resume from the current time instead of busy-catching up.
        const int64_t afterRead = esp_timer_get_time();
        if (nextSampleTime < afterRead) {
            nextSampleTime = afterRead + SAMPLE_PERIOD_US;
        }
    }

    return true;
}

}

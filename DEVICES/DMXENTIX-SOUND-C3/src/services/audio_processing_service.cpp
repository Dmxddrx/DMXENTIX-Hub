#include "audio_processing_service.h"

#include "audio_config.h"
#include "max9814_driver.h"

#include <math.h>

namespace AudioProcessingService {
namespace {

// Static storage avoids placing the complete frame on the loop task stack.
uint16_t sampleBuffer[AUDIO_FRAME_SAMPLES] = {};

}

bool begin()
{
    return Max9814Driver::begin();
}

bool capture(AudioMetrics &metrics)
{
    if (!Max9814Driver::captureSamples(
            sampleBuffer,
            AUDIO_FRAME_SAMPLES)) {
        return false;
    }

    uint64_t sum = 0;

    for (size_t i = 0; i < AUDIO_FRAME_SAMPLES; ++i) {
        sum += sampleBuffer[i];
    }

    const uint16_t dcOffset = static_cast<uint16_t>(
        sum / AUDIO_FRAME_SAMPLES
    );

    uint64_t sumSquares = 0;
    uint16_t peak = 0;

    // Remove the MAX9814's DC bias before measuring audio amplitude.
    for (size_t i = 0; i < AUDIO_FRAME_SAMPLES; ++i) {
        const int32_t centered =
            static_cast<int32_t>(sampleBuffer[i]) - dcOffset;

        const uint16_t magnitude = static_cast<uint16_t>(
            (centered < 0) ? -centered : centered
        );

        sumSquares += static_cast<uint64_t>(centered * centered);

        if (magnitude > peak) {
            peak = magnitude;
        }
    }

    const double meanSquare =
        static_cast<double>(sumSquares) / AUDIO_FRAME_SAMPLES;

    const double rmsValue = sqrt(meanSquare);

    metrics.rms = static_cast<uint16_t>(rmsValue + 0.5);
    metrics.peak = peak;
    metrics.dcOffset = dcOffset;

    return true;
}

}

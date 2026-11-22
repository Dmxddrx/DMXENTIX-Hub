#ifndef AUDIO_PROCESSING_SERVICE_H
#define AUDIO_PROCESSING_SERVICE_H

#include <stdint.h>

namespace AudioProcessingService {

struct AudioMetrics {
    uint16_t rms;
    uint16_t peak;
    uint16_t dcOffset;
};

bool begin();

// Capture a microphone frame and calculate its AC RMS and peak levels.
bool capture(AudioMetrics &metrics);

}

#endif

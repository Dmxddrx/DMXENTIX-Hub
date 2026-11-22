#ifndef AUDIO_CONFIG_H
#define AUDIO_CONFIG_H

#include <stddef.h>
#include <stdint.h>

// A 256-sample frame at 8 kHz covers 32 ms of microphone output.
constexpr uint32_t AUDIO_SAMPLE_RATE_HZ = 8000;
constexpr size_t AUDIO_FRAME_SAMPLES = 256;
constexpr uint32_t AUDIO_PUBLISH_INTERVAL_MS = 1000;
constexpr uint8_t AUDIO_ADC_RESOLUTION_BITS = 12;

#endif

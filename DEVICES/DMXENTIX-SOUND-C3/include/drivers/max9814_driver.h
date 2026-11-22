#ifndef MAX9814_DRIVER_H
#define MAX9814_DRIVER_H

#include <stddef.h>
#include <stdint.h>

namespace Max9814Driver {

// Configure the ESP32-C3 ADC input connected to MAX9814 OUT.
bool begin();

// Capture one raw ADC frame. Samples are 12-bit readings (0..4095).
bool captureSamples(uint16_t *samples, size_t sampleCount);

}

#endif

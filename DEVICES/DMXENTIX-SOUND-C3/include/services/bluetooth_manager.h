#ifndef BLUETOOTH_MANAGER_H
#define BLUETOOTH_MANAGER_H

#include <stdint.h>

namespace BluetoothManager {

bool begin();
bool isConnected();

void publishTelemetry(
    uint16_t rms,
    uint16_t peak,
    float chipTemperatureC,
    bool chipTemperatureValid
);

}

#endif

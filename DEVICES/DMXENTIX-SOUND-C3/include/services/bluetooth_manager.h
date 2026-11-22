#ifndef BLUETOOTH_MANAGER_H
#define BLUETOOTH_MANAGER_H

#include <stdint.h>

namespace BluetoothManager {

bool begin();
bool isConnected();

// Publish relative microphone metrics through the Status characteristic.
void publishAudioMetrics(uint16_t rms, uint16_t peak);

}

#endif

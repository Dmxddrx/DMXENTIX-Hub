#ifndef BLE_CONFIG_H
#define BLE_CONFIG_H

#include <stdint.h>

// SoundNode UUID namespace. Keep these values in sync with the
// SoundNodeDeviceProfile and SoundNodeProtocol in DMXENTIX Hub.
#define SOUNDNODE_SERVICE_UUID \
    "d3601000-5a3b-4b3f-9c21-8f5a2d7c0101"

#define SOUNDNODE_COMMAND_UUID \
    "d3601001-5a3b-4b3f-9c21-8f5a2d7c0101"

#define SOUNDNODE_STATUS_UUID \
    "d3601002-5a3b-4b3f-9c21-8f5a2d7c0101"

constexpr uint16_t BLE_PREFERRED_MTU = 100;

#endif

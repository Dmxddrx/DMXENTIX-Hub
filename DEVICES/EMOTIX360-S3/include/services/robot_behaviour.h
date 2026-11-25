#ifndef ROBOT_BEHAVIOUR_H
#define ROBOT_BEHAVIOUR_H

#include <Arduino.h>

namespace RobotBehaviour {

// Update the automatic robot behaviour.
// Called from the OLED task using the latest MPU6050 snapshot.
// Update sensor-driven and matrix-mode-driven robot behaviour.
// matrixMode is supplied by DisplayManager's current status.
void update(
    uint32_t nowMs,
    const char *matrixMode
);

// Reset internal behaviour detection state.
void reset();

}

#endif
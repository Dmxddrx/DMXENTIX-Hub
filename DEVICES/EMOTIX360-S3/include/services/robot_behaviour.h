#ifndef ROBOT_BEHAVIOUR_H
#define ROBOT_BEHAVIOUR_H

#include <Arduino.h>

namespace RobotBehaviour {

// Update the automatic robot behaviour.
// Called from the OLED task using the latest MPU6050 snapshot.
void update(uint32_t nowMs);

// Reset internal behaviour detection state.
void reset();

}

#endif
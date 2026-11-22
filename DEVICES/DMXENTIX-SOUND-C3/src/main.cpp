#include <Arduino.h>

#include "sound_node.h"

void setup()
{
    Serial.begin(115200);
    delay(500);

    if (!SoundNode::begin()) {
        Serial.println("[SYSTEM] SoundNode startup failed");

        // Keep diagnostics available rather than restarting in a loop.
        while (true) {
            delay(1000);
        }
    }
}

void loop()
{
    SoundNode::update();
}

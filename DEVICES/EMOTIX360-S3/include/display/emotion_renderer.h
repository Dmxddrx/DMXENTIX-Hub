
#ifndef EMOTION_RENDERER_H
#define EMOTION_RENDERER_H

#include <Arduino.h>
#include <Adafruit_GFX.h>

namespace EmotionRenderer {

// Available robot eye expressions.
enum class Mood : uint8_t
{
    NEUTRAL,
    HAPPY,
    TIRED,
    ANGRY,
    SURPRISED
};

// Draw and update the robot eyes.
//
// Landscape: animated face.
// Portrait: existing static eye design.
//
// DisplayManager retains ownership of the OLED.
void drawRobotEyes(
    Adafruit_GFX &canvas,
    uint32_t nowMs
);

// Draw the existing static rounded-square eyes.
void drawNeutralEyes(
    Adafruit_GFX &canvas,
    uint16_t color = 1
);

// Change the requested eye expression.
void setMood(Mood mood);

// Get the requested eye expression.
Mood getMood();

// Reset animation state.
void reset();

}

#endif

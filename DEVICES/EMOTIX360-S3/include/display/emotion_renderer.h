
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

// Available gaze directions.
enum class GazeDirection : uint8_t
{
    CENTER,

    UP,
    UP_RIGHT,
    RIGHT,
    DOWN_RIGHT,

    DOWN,
    DOWN_LEFT,
    LEFT,
    UP_LEFT
};

// Select which eye receives an animation or expression.
enum class Eye : uint8_t
{
    LEFT,
    RIGHT,
    BOTH
};

// Available eye geometry configurations.
enum class GeometryPreset : uint8_t
{
    NORMAL,
    WIDE,
    NARROW,
    TALL,
    SHORT,
    CLOSE_SPACING,
    WIDE_SPACING,
    SMALL_RADIUS,
    LARGE_RADIUS,
    INDEPENDENT,
    REVERSE_INDEPENDENT,
    CYCLOPS,
    RESTORE_EYE_PAIR
};

// Available one-shot eye animations.
enum class OneShotAnimation : uint8_t
{
    NONE,
    FLICKER_HORIZONTAL,
    FLICKER_VERTICAL,
    CONFUSED,
    LAUGH,
    SHAKE_HORIZONTAL,
    SHAKE_VERTICAL,
    SWEAT_LEFT,
    SWEAT_RIGHT,
    SWEAT_BOTH
};

// Draw and update the robot eyes.
//
// Landscape and portrait: animated robot face.
// Both orientations share the same animation state and behaviours.
// Only eye dimensions and movement distances are adapted.
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

// Set a manual eye gaze direction.
// Manual gaze disables automatic idle gaze.
void setGaze(GazeDirection direction);

// Enable or disable automatic random idle gaze.
void setIdleGaze(bool enabled);

// ------------------------------------------------------------
// INDEPENDENT EYE CONTROL
// ------------------------------------------------------------

// Smoothly open or close the selected eye(s).
void setEyeOpen(Eye eye, bool open);

// Play a one-shot blink on the selected eye(s).
void blink(Eye eye = Eye::BOTH);

// Convenience function for blinking only one eye.
// Use Eye::LEFT or Eye::RIGHT.
void wink(Eye eye);

// Apply a mood to one eye without changing the other eye.
void setEyeMood(Eye eye, Mood mood);

// ------------------------------------------------------------
// EYE GEOMETRY CONTROL
// ------------------------------------------------------------

// Apply a predefined eye geometry configuration.
// Geometry changes are smoothly interpolated.
void setGeometryPreset(GeometryPreset preset);

// Enable or disable curiosity-driven eye enlargement.
void setCuriosity(bool enabled);

// Enable or disable automatic periodic blinking.
void setAutomaticBlink(bool enabled);

// Play a non-blocking one-shot eye animation.
void playOneShot(OneShotAnimation animation);

// Reset animation state.
void reset();

}

#endif


#include "button.h"

#include "pin_config.h"
#include "animation_manager.h"
#include "display_manager.h"
#include "emotion_renderer.h"

#include <Arduino.h>

#include <freertos/FreeRTOS.h>
#include <freertos/task.h>

namespace ButtonManager {
namespace {

// ------------------------------------------------------------
// BUTTON TASK CONFIGURATION
// ------------------------------------------------------------

constexpr uint32_t BUTTON_TASK_STACK_SIZE = 3072;
constexpr UBaseType_t BUTTON_TASK_PRIORITY = 1;

// Button sampling interval.
constexpr uint32_t BUTTON_POLL_INTERVAL_MS = 10;

// Required stable time before accepting a state change.
constexpr uint32_t BUTTON_DEBOUNCE_MS = 35;

// Long press threshold for OLED page navigation.
constexpr uint32_t BUTTON_LONG_PRESS_MS = 800;

// ------------------------------------------------------------
// INTERNAL STATE
// ------------------------------------------------------------

TaskHandle_t buttonTaskHandle = nullptr;

bool initialized = false;

// Current animation tracked by the button manager.
// This must match AnimationManager's initial mode.
AnimationManager::Mode currentMode =
    AnimationManager::Mode::IDLE;

// ------------------------------------------------------------
// ANIMATION MODE SWITCHING
// ------------------------------------------------------------

/*
 * BUTTON ANIMATION SEQUENCE
 *
 *     IDLE
 *       |
 *       v
 *   SAND_CLOCK
 *       |
 *       v
 *     WATER
 *       |
 *       v
 *  SAND_GRAVITY
 *       |
 *       v
 * SMALL_ANIMATIONS
 *       |
 *       v
 *     HEART
 *       |
 *       v
 *      RAIN
 *       |
 *       v
 *     WAVES
 *       |
 *       v
 *      IDLE
 *
 * All modes are controlled by AnimationManager.
 */

// Return the next animation mode in button sequence.
AnimationManager::Mode getNextMode(
    AnimationManager::Mode currentMode)
{
    switch (currentMode) {

        case AnimationManager::Mode::IDLE:
            return AnimationManager::Mode::SAND_CLOCK;

        case AnimationManager::Mode::SAND_CLOCK:
            return AnimationManager::Mode::WATER;

        case AnimationManager::Mode::WATER:
            return AnimationManager::Mode::SAND_GRAVITY;

        case AnimationManager::Mode::SAND_GRAVITY:
            return AnimationManager::Mode::HEART;

        case AnimationManager::Mode::HEART:
            return AnimationManager::Mode::RAIN;

        case AnimationManager::Mode::RAIN:
            return AnimationManager::Mode::WAVES;

        case AnimationManager::Mode::WAVES:
            return AnimationManager::Mode::IDLE;

        default:
            return AnimationManager::Mode::IDLE;
    }
}


// Request the next animation from AnimationManager.
void toggleAnimation()
{
    // Determine the next mode.
    const AnimationManager::Mode nextMode =
        getNextMode(currentMode);

    // Send the mode request to AnimationManager.
    if (!AnimationManager::setMode(nextMode))
    {
        Serial.println("[BUTTON] Animation mode request failed");
        return;
    }

    // Update the local mode only after the request succeeds.
    currentMode = nextMode;

    Serial.printf(
        "[BUTTON] Mode changed: %u\n",
        static_cast<unsigned int>(currentMode)
    );
}

// ------------------------------------------------------------
// BUTTON MONITORING TASK
// ------------------------------------------------------------

/*
 * BUTTON TASK
 *
 * Active LOW button:
 *
 *     HIGH = Released.
 *     LOW  = Pressed.
 *
 * Button actions:
 *
 *     SHORT PRESS:
 *         Advance MAX7219 animation.
 *
 *     LONG PRESS:
 *         Advance OLED page.
 *
 * The action is selected when the button is released,
 * after the press duration has been determined.
 *
 * Software debouncing:
 *
 *     BUTTON_DEBOUNCE_MS
 *
 * Long press threshold:
 *
 *     BUTTON_LONG_PRESS_MS
 */
void buttonTask(void *parameter)
{
    (void)parameter;

    // Read initial state to avoid unwanted actions
    // if the button is held during startup.
    bool lastRawPressed =
        (digitalRead(PinConfig::BUTTON_ANIMATION) == LOW);

    bool stablePressed = lastRawPressed;

    uint32_t lastRawChangeMs = millis();

    // Press timing becomes active only after a
    // newly detected and debounced press.
    bool pressTimingActive = false;

    uint32_t pressStartedMs = 0;

    // ------------------------------------------------------------
    // EYE-DEMO BUTTON STATE
    // ------------------------------------------------------------

    // Independent debounce state for GPIO21.
    bool eyeTestLastRawPressed =
        (digitalRead(PinConfig::BUTTON_EYE_TEST) == LOW);

    bool eyeTestStablePressed = eyeTestLastRawPressed;

    uint32_t eyeTestLastRawChangeMs = millis();

    bool eyeTestPressTimingActive = false;
    uint32_t eyeTestPressStartedMs = 0;



    TickType_t lastWakeTime = xTaskGetTickCount();

    TickType_t pollInterval =
        pdMS_TO_TICKS(BUTTON_POLL_INTERVAL_MS);

    if (pollInterval == 0) {
        pollInterval = 1;
    }

    while (true)
    {
        const bool rawPressed =
            (digitalRead(PinConfig::BUTTON_ANIMATION) == LOW);

        const uint32_t nowMs = millis();

        // Detect raw electrical state changes.
        if (rawPressed != lastRawPressed)
        {
            lastRawPressed = rawPressed;
            lastRawChangeMs = nowMs;
        }

        // Accept a state change after the debounce interval.
        if ((uint32_t)(nowMs - lastRawChangeMs) >=
            BUTTON_DEBOUNCE_MS)
        {
            if (stablePressed != lastRawPressed)
            {
                stablePressed = lastRawPressed;

                // --------------------------------------------
                // BUTTON PRESSED
                // --------------------------------------------

                if (stablePressed)
                {
                    pressStartedMs = lastRawChangeMs;
                    pressTimingActive = true;
                }

                // --------------------------------------------
                // BUTTON RELEASED
                // --------------------------------------------

                else if (pressTimingActive)
                {
                    // Measure physical press duration.
                    const uint32_t pressDurationMs =
                        lastRawChangeMs - pressStartedMs;

                    pressTimingActive = false;

                    // ----------------------------------------
                    // LONG PRESS: CHANGE OLED PAGE
                    // ----------------------------------------

                    if (pressDurationMs >= BUTTON_LONG_PRESS_MS)
                    {
                        if (DisplayManager::isReady() &&
                            DisplayManager::nextPage())
                        {
                            Serial.println(
                                "[BUTTON] OLED page changed"
                            );
                        }
                    }

                    // ----------------------------------------
                    // SHORT PRESS: CHANGE MATRIX ANIMATION
                    // ----------------------------------------

                    else
                    {
                        toggleAnimation();
                    }
                }
            }
        }

        // ----------------------------------------------------
        // EYE-DEMO BUTTON — GPIO21
        // ----------------------------------------------------

        const bool eyeTestRawPressed =
            (digitalRead(PinConfig::BUTTON_EYE_TEST) == LOW);

        // Detect raw electrical state changes.
        if (eyeTestRawPressed != eyeTestLastRawPressed)
        {
            eyeTestLastRawPressed = eyeTestRawPressed;
            eyeTestLastRawChangeMs = nowMs;
        }

        // Accept a state change after the debounce interval.
        if ((uint32_t)(nowMs - eyeTestLastRawChangeMs) >=
            BUTTON_DEBOUNCE_MS)
        {
            if (eyeTestStablePressed != eyeTestLastRawPressed)
            {
                eyeTestStablePressed = eyeTestLastRawPressed;

                // --------------------------------------------
                // EYE-TEST BUTTON PRESSED
                // --------------------------------------------

                if (eyeTestStablePressed)
                {
                    eyeTestPressStartedMs =
                        eyeTestLastRawChangeMs;

                    eyeTestPressTimingActive = true;
                }

                // --------------------------------------------
                // EYE-TEST BUTTON RELEASED
                // --------------------------------------------

                else if (eyeTestPressTimingActive)
                {
                    const uint32_t pressDurationMs =
                        eyeTestLastRawChangeMs -
                        eyeTestPressStartedMs;

                    eyeTestPressTimingActive = false;

                    // LONG PRESS: EXIT EYE DEMO.
                    if (pressDurationMs >= BUTTON_LONG_PRESS_MS)
                    {
                        if (DisplayManager::exitEyeDemo())
                        {
                            Serial.println(
                                "[BUTTON] Eye demo exited"
                            );
                        }
                    }

                    // SHORT PRESS: NEXT EYE BEHAVIOUR.
                    else
                    {
                        if (DisplayManager::nextEyeDemoStep())
                        {
                            Serial.println(
                                "[BUTTON] Eye demo advanced"
                            );
                        }
                    }
                }
            }
        }

        // Schedule the next sample.
        vTaskDelayUntil(
            &lastWakeTime,
            pollInterval
        );
    }
}

} // namespace

// ------------------------------------------------------------
// PUBLIC API
// ------------------------------------------------------------

bool begin(AnimationManager::Mode initialMode)
{
    if (initialized) {
        return true;
    }

    // Configure both tactile buttons with internal pull-ups.
    pinMode(PinConfig::BUTTON_ANIMATION, INPUT_PULLUP);
    pinMode(PinConfig::BUTTON_EYE_TEST, INPUT_PULLUP);

    // Synchronize the button manager with AnimationManager.
    currentMode = initialMode;

    initialized = true;

    Serial.printf(
        "[BUTTON] Animation button initialized on GPIO%u\n",
        PinConfig::BUTTON_ANIMATION
    );

    return true;
}


bool startTask()
{
    if (!initialized) {
        Serial.println("[BUTTON] Initialize button before starting task");
        return false;
    }

    if (buttonTaskHandle != nullptr) {
        return true;
    }

    const BaseType_t result = xTaskCreate(
        buttonTask,
        "ButtonTask",
        BUTTON_TASK_STACK_SIZE,
        nullptr,
        BUTTON_TASK_PRIORITY,
        &buttonTaskHandle
    );

    if (result != pdPASS)
    {
        buttonTaskHandle = nullptr;

        Serial.println("[BUTTON] Failed to create button task");

        return false;
    }

    Serial.println("[BUTTON] Button task started");

    return true;
}


bool isTaskRunning()
{
    return buttonTaskHandle != nullptr;
}

} // namespace ButtonManager

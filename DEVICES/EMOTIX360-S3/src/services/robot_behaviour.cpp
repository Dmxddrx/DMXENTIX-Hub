#include "robot_behaviour.h"

#include "mpu6050.h"
#include "emotion_renderer.h"

#include <Arduino.h>
#include <math.h>

namespace RobotBehaviour {
namespace {

// ------------------------------------------------------------
// BEHAVIOUR CONFIGURATION
// ------------------------------------------------------------

// Tilt activation threshold.
constexpr float TILT_ENTER_DEG = 18.0f;

// Tilt release threshold.
// Lower than the activation threshold to provide hysteresis.
constexpr float TILT_EXIT_DEG = 12.0f;

// Gyroscope magnitude required to detect a shake.
constexpr float SHAKE_TRIGGER_DPS = 125.0f;

// Gyroscope magnitude below which another shake can be detected.
constexpr float SHAKE_RELEASE_DPS = 55.0f;

// Minimum time between detected shake events.
constexpr uint32_t SHAKE_COOLDOWN_MS = 1200;

// Keep the surprised expression visible slightly longer
// than the renderer's 700 ms shake animation.
constexpr uint32_t SHAKE_VISUAL_HOLD_MS = 800;

// Ignore sensor readings that have become too old.
constexpr uint32_t SENSOR_STALE_MS = 300;


// ------------------------------------------------------------
// BEHAVIOUR STATES
// ------------------------------------------------------------

enum class BehaviourState : uint8_t
{
    UNINITIALIZED,

    IDLE,

    TILT_LEFT,
    TILT_RIGHT,

    TILT_FORWARD,
    TILT_BACKWARD
};


// ------------------------------------------------------------
// INTERNAL STATE
// ------------------------------------------------------------

BehaviourState currentState =
    BehaviourState::UNINITIALIZED;

// Shake detection.
bool shakeArmed = true;

bool shakeTriggeredBefore = false;
uint32_t lastShakeMs = 0;

// Temporary shake expression.
bool shakeVisualActive = false;
uint32_t shakeVisualUntilMs = 0;


// ------------------------------------------------------------
// TIME HELPER
// ------------------------------------------------------------

bool timeReached(
    uint32_t nowMs,
    uint32_t deadlineMs
)
{
    return static_cast<int32_t>(
        nowMs - deadlineMs
    ) >= 0;
}


// ------------------------------------------------------------
// BEHAVIOUR NAME
// ------------------------------------------------------------

const char *getBehaviourName(BehaviourState behaviour)
{
    switch (behaviour)
    {
        case BehaviourState::IDLE:
            return "IDLE";

        case BehaviourState::TILT_LEFT:
            return "TILT_LEFT";

        case BehaviourState::TILT_RIGHT:
            return "TILT_RIGHT";

        case BehaviourState::TILT_FORWARD:
            return "TILT_FORWARD";

        case BehaviourState::TILT_BACKWARD:
            return "TILT_BACKWARD";

        case BehaviourState::UNINITIALIZED:
        default:
            return "UNINITIALIZED";
    }
}


// ------------------------------------------------------------
// DETECT DEVICE TILT
// ------------------------------------------------------------

BehaviourState detectTilt(
    float pitchDeg,
    float rollDeg
)
{
    // --------------------------------------------------------
    // HYSTERESIS — KEEP THE CURRENT TILT UNTIL RELEASE
    // --------------------------------------------------------

    switch (currentState)
    {
        case BehaviourState::TILT_LEFT:

            if (rollDeg <= -TILT_EXIT_DEG)
            {
                return BehaviourState::TILT_LEFT;
            }

            break;

        case BehaviourState::TILT_RIGHT:

            if (rollDeg >= TILT_EXIT_DEG)
            {
                return BehaviourState::TILT_RIGHT;
            }

            break;

        case BehaviourState::TILT_FORWARD:

            if (pitchDeg >= TILT_EXIT_DEG)
            {
                return BehaviourState::TILT_FORWARD;
            }

            break;

        case BehaviourState::TILT_BACKWARD:

            if (pitchDeg <= -TILT_EXIT_DEG)
            {
                return BehaviourState::TILT_BACKWARD;
            }

            break;

        default:
            break;
    }


    // --------------------------------------------------------
    // CHECK NEW TILT
    // --------------------------------------------------------

    const float absRoll =
        fabsf(rollDeg);

    const float absPitch =
        fabsf(pitchDeg);

    const bool rollTilted =
        absRoll >= TILT_ENTER_DEG;

    const bool pitchTilted =
        absPitch >= TILT_ENTER_DEG;

    // Return to the neutral state when neither axis
    // exceeds the activation threshold.
    if (!rollTilted && !pitchTilted)
    {
        return BehaviourState::IDLE;
    }

    // Select whichever axis has the stronger tilt.
    if (rollTilted && absRoll >= absPitch)
    {
        return (rollDeg < 0.0f)
            ? BehaviourState::TILT_LEFT
            : BehaviourState::TILT_RIGHT;
    }

    if (pitchDeg > 0.0f)
    {
        return BehaviourState::TILT_FORWARD;
    }

    return BehaviourState::TILT_BACKWARD;
}


// ------------------------------------------------------------
// APPLY BEHAVIOUR TO ROBOT EYES
// ------------------------------------------------------------

void applyBehaviourState(
    BehaviourState newState,
    bool forceApply = false
)
{
    // Avoid repeatedly sending identical renderer commands.
    if (!forceApply && newState == currentState)
    {
        return;
    }

    currentState = newState;

    // Automatic blinking remains enabled in every behaviour.
    EmotionRenderer::setAutomaticBlink(true);


    switch (newState)
    {
        // ----------------------------------------------------
        // IDLE
        // ----------------------------------------------------

        case BehaviourState::IDLE:

            EmotionRenderer::setMood(
                EmotionRenderer::Mood::NEUTRAL
            );

            EmotionRenderer::setCuriosity(false);

            // Centre the eyes before resuming random idle gaze.
            EmotionRenderer::setGaze(
                EmotionRenderer::GazeDirection::CENTER
            );

            EmotionRenderer::setIdleGaze(true);

            break;


        // ----------------------------------------------------
        // TILT LEFT
        // ----------------------------------------------------

        case BehaviourState::TILT_LEFT:

            EmotionRenderer::setIdleGaze(false);

            EmotionRenderer::setMood(
                EmotionRenderer::Mood::NEUTRAL
            );

            EmotionRenderer::setCuriosity(true);

            EmotionRenderer::setGaze(
                EmotionRenderer::GazeDirection::LEFT
            );

            break;


        // ----------------------------------------------------
        // TILT RIGHT
        // ----------------------------------------------------

        case BehaviourState::TILT_RIGHT:

            EmotionRenderer::setIdleGaze(false);

            EmotionRenderer::setMood(
                EmotionRenderer::Mood::NEUTRAL
            );

            EmotionRenderer::setCuriosity(true);

            EmotionRenderer::setGaze(
                EmotionRenderer::GazeDirection::RIGHT
            );

            break;


        // ----------------------------------------------------
        // POSITIVE PITCH
        // ----------------------------------------------------

        case BehaviourState::TILT_FORWARD:

            EmotionRenderer::setIdleGaze(false);

            EmotionRenderer::setCuriosity(false);

            EmotionRenderer::setMood(
                EmotionRenderer::Mood::TIRED
            );

            EmotionRenderer::setGaze(
                EmotionRenderer::GazeDirection::DOWN
            );

            break;


        // ----------------------------------------------------
        // NEGATIVE PITCH
        // ----------------------------------------------------

        case BehaviourState::TILT_BACKWARD:

            EmotionRenderer::setIdleGaze(false);

            EmotionRenderer::setCuriosity(false);

            EmotionRenderer::setMood(
                EmotionRenderer::Mood::SURPRISED
            );

            EmotionRenderer::setGaze(
                EmotionRenderer::GazeDirection::UP
            );

            break;


        case BehaviourState::UNINITIALIZED:
        default:
            break;
    }

    Serial.printf(
        "[ROBOT BEHAVIOUR] State: %s\n",
        getBehaviourName(newState)
    );
}


// ------------------------------------------------------------
// START SHAKE REACTION
// ------------------------------------------------------------

void startShakeReaction(
    bool horizontal,
    float gyroMagnitude,
    uint32_t nowMs
)
{
    // The shake temporarily overrides the normal expression.
    EmotionRenderer::setIdleGaze(false);

    EmotionRenderer::setCuriosity(false);

    EmotionRenderer::setMood(
        EmotionRenderer::Mood::SURPRISED
    );

    EmotionRenderer::setGaze(
        EmotionRenderer::GazeDirection::CENTER
    );


    // Select the appropriate non-blocking eye animation.
    EmotionRenderer::playOneShot(
        horizontal
        ? EmotionRenderer::OneShotAnimation::SHAKE_HORIZONTAL
        : EmotionRenderer::OneShotAnimation::SHAKE_VERTICAL
    );

    shakeVisualActive = true;

    shakeVisualUntilMs =
        nowMs + SHAKE_VISUAL_HOLD_MS;

    lastShakeMs = nowMs;

    shakeTriggeredBefore = true;

    Serial.printf(
        "[ROBOT BEHAVIOUR] Shake detected: %s (%.1f DPS)\n",
        horizontal ? "HORIZONTAL" : "VERTICAL",
        gyroMagnitude
    );
}

} // namespace


// ============================================================
// PUBLIC API
// ============================================================


// ------------------------------------------------------------
// UPDATE ROBOT BEHAVIOUR
// ------------------------------------------------------------

void update(uint32_t nowMs)
{
    MPU6050::Data imu{};

    // --------------------------------------------------------
    // GET THE LATEST SENSOR SNAPSHOT
    // --------------------------------------------------------

    if (!MPU6050::getData(imu) ||
        !imu.valid ||
        (uint32_t)(nowMs - imu.timestampMs) > SENSOR_STALE_MS)
    {
        // Do not leave an old shake animation running
        // when sensor data becomes unavailable.
        if (shakeVisualActive)
        {
            EmotionRenderer::playOneShot(
                EmotionRenderer::OneShotAnimation::NONE
            );
        }

        shakeVisualActive = false;
        shakeArmed = true;

        applyBehaviourState(
            BehaviourState::IDLE
        );

        return;
    }


    // --------------------------------------------------------
    // CALCULATE GYROSCOPE MAGNITUDE
    // --------------------------------------------------------

    const float gx = imu.gxDps;
    const float gy = imu.gyDps;
    const float gz = imu.gzDps;

    const float gyroMagnitude =
        sqrtf(
            gx * gx +
            gy * gy +
            gz * gz
        );


    // A sufficiently quiet reading rearms shake detection.
    if (gyroMagnitude <= SHAKE_RELEASE_DPS)
    {
        shakeArmed = true;
    }


    // --------------------------------------------------------
    // DETECT CURRENT TILT
    // --------------------------------------------------------

    const BehaviourState detectedState =
        detectTilt(
            imu.pitchDeg,
            imu.rollDeg
        );


    // --------------------------------------------------------
    // FINISH PREVIOUS SHAKE REACTION
    // --------------------------------------------------------

    if (shakeVisualActive)
    {
        if (!timeReached(
                nowMs,
                shakeVisualUntilMs))
        {
            // Keep the temporary shake expression active.
            return;
        }

        shakeVisualActive = false;

        // Restore the current physical tilt behaviour,
        // even when the detected state has not changed.
        applyBehaviourState(
            detectedState,
            true
        );
    }
    else
    {
        applyBehaviourState(
            detectedState
        );
    }


    // --------------------------------------------------------
    // SHAKE DETECTION
    // --------------------------------------------------------

    if (!shakeArmed ||
        gyroMagnitude < SHAKE_TRIGGER_DPS)
    {
        return;
    }

    // Consume this shake event.
    // The sensor must return below the release threshold
    // before another event can be detected.
    shakeArmed = false;


    // Prevent repeated reactions from continuous movement.
    const bool cooldownReady =
        !shakeTriggeredBefore ||
        (uint32_t)(nowMs - lastShakeMs) >=
        SHAKE_COOLDOWN_MS;

    if (!cooldownReady)
    {
        return;
    }


    // --------------------------------------------------------
    // CLASSIFY SHAKE DIRECTION
    // --------------------------------------------------------

    // MPU6050 orientation:
    // gx contributes to the filtered roll angle.
    // gy contributes to the filtered pitch angle.
    //
    // For this initial mapping:
    // Roll/yaw-dominant movement -> horizontal eye shake.
    // Pitch-dominant movement    -> vertical eye shake.

    const float horizontalRate =
        fmaxf(
            fabsf(gx),
            fabsf(gz)
        );

    const float verticalRate =
        fabsf(gy);

    const bool horizontalShake =
        horizontalRate >= verticalRate;


    // --------------------------------------------------------
    // START THE VISUAL REACTION
    // --------------------------------------------------------

    startShakeReaction(
        horizontalShake,
        gyroMagnitude,
        nowMs
    );
}


// ------------------------------------------------------------
// RESET BEHAVIOUR DETECTION
// ------------------------------------------------------------

void reset()
{
    currentState =
        BehaviourState::UNINITIALIZED;

    shakeArmed = true;

    shakeTriggeredBefore = false;
    lastShakeMs = 0;

    shakeVisualActive = false;
    shakeVisualUntilMs = 0;
}

} // namespace RobotBehaviour
#include "robot_behaviour.h"

#include "mpu6050.h"
#include "emotion_renderer.h"

#include <Arduino.h>
#include <math.h>
#include <string.h>

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
// MATRIX ANIMATION MODES
// ------------------------------------------------------------

enum class MatrixMode : uint8_t
{
    UNKNOWN,

    IDLE,
    STARTUP,

    SAND_CLOCK,
    WATER,
    SAND_GRAVITY,

    HEART,
    RAIN,
    WAVES
};

// ------------------------------------------------------------
// INTERNAL STATE
// ------------------------------------------------------------

BehaviourState currentState =
    BehaviourState::UNINITIALIZED;

// ------------------------------------------------------------
// MATRIX MODE AWARENESS
// ------------------------------------------------------------

// Current matrix animation reported by AnimationManager.
MatrixMode currentMatrixMode = MatrixMode::UNKNOWN;

bool matrixModeInitialized = false;

// Apply the matrix personality when a mode changes,
// a tilt ends, or a temporary shake reaction finishes.
bool matrixProfileNeedsApply = true;

// Play an entry animation only when a matrix mode changes.
// Do not replay it when returning from a sensor reaction.
bool matrixEntryEffectPending = false;

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
// PARSE MATRIX ANIMATION MODE
// ------------------------------------------------------------

MatrixMode parseMatrixMode(const char *name)
{
    if (name == nullptr)
    {
        return MatrixMode::UNKNOWN;
    }

    if (strcmp(name, "IDLE") == 0)
        return MatrixMode::IDLE;

    if (strcmp(name, "STARTUP") == 0)
        return MatrixMode::STARTUP;

    if (strcmp(name, "SAND_CLOCK") == 0)
        return MatrixMode::SAND_CLOCK;

    if (strcmp(name, "WATER") == 0)
        return MatrixMode::WATER;

    if (strcmp(name, "SAND_GRAVITY") == 0)
        return MatrixMode::SAND_GRAVITY;

    if (strcmp(name, "HEART") == 0)
        return MatrixMode::HEART;

    if (strcmp(name, "RAIN") == 0)
        return MatrixMode::RAIN;

    if (strcmp(name, "WAVES") == 0)
        return MatrixMode::WAVES;

    return MatrixMode::UNKNOWN;
}


// ------------------------------------------------------------
// GET MATRIX MODE NAME
// ------------------------------------------------------------

const char *getMatrixModeName(MatrixMode mode)
{
    switch (mode)
    {
        case MatrixMode::IDLE:
            return "IDLE";

        case MatrixMode::STARTUP:
            return "STARTUP";

        case MatrixMode::SAND_CLOCK:
            return "SAND_CLOCK";

        case MatrixMode::WATER:
            return "WATER";

        case MatrixMode::SAND_GRAVITY:
            return "SAND_GRAVITY";

        case MatrixMode::HEART:
            return "HEART";

        case MatrixMode::RAIN:
            return "RAIN";

        case MatrixMode::WAVES:
            return "WAVES";

        case MatrixMode::UNKNOWN:
        default:
            return "UNKNOWN";
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

    // Active matrix animations control the base eye mood.
    // Tilt reactions only modify gaze and curiosity while
    // a matrix personality is active.
    const bool preserveMatrixMood =
        currentMatrixMode != MatrixMode::IDLE &&
        currentMatrixMode != MatrixMode::UNKNOWN;


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

            if (!preserveMatrixMood)
            {
                EmotionRenderer::setMood(
                    EmotionRenderer::Mood::NEUTRAL
                );
            }

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

            if (!preserveMatrixMood)
            {
                EmotionRenderer::setMood(
                    EmotionRenderer::Mood::TIRED
                );
            }

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

            if (!preserveMatrixMood)
            {
                EmotionRenderer::setMood(
                    EmotionRenderer::Mood::SURPRISED
                );
            }

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
// APPLY MATRIX ANIMATION PERSONALITY
// ------------------------------------------------------------
//
// Each MAX7219 animation mode gives the OLED face
// a corresponding personality.
//
// These are base expressions. MPU6050 tilt and shake
// reactions can temporarily override them.
//

void applyMatrixModeProfile(
    MatrixMode mode,
    bool playEntryEffect
)
{
    using EmotionRenderer::Mood;
    using EmotionRenderer::GazeDirection;
    using EmotionRenderer::OneShotAnimation;

    // Keep normal blinking available.
    EmotionRenderer::setAutomaticBlink(true);

    // Set predictable defaults before selecting the profile.
    EmotionRenderer::setCuriosity(false);
    EmotionRenderer::setIdleGaze(false);

    Mood targetMood = Mood::NEUTRAL;
    GazeDirection targetGaze = GazeDirection::CENTER;

    OneShotAnimation entryAnimation =
        OneShotAnimation::NONE;

    bool enableCuriosity = false;
    bool enableIdleGaze = false;


    switch (mode)
    {
        // ----------------------------------------------------
        // IDLE
        // ----------------------------------------------------

        case MatrixMode::IDLE:

            targetMood = Mood::NEUTRAL;
            targetGaze = GazeDirection::CENTER;

            enableIdleGaze = true;

            break;


        // ----------------------------------------------------
        // STARTUP
        // ----------------------------------------------------

        case MatrixMode::STARTUP:

            // The robot looks alert during startup.
            targetMood = Mood::SURPRISED;
            targetGaze = GazeDirection::CENTER;

            entryAnimation =
                OneShotAnimation::FLICKER_VERTICAL;

            break;


        // ----------------------------------------------------
        // HEART
        // ----------------------------------------------------

        case MatrixMode::HEART:

            // Happy expression matches the heart animation.
            targetMood = Mood::HAPPY;
            targetGaze = GazeDirection::CENTER;

            entryAnimation =
                OneShotAnimation::LAUGH;

            break;


        // ----------------------------------------------------
        // RAIN
        // ----------------------------------------------------

        case MatrixMode::RAIN:

            // Look upward as rain begins.
            targetMood = Mood::SURPRISED;
            targetGaze = GazeDirection::UP;

            // Brief falling droplets around the eyes.
            entryAnimation =
                OneShotAnimation::SWEAT_BOTH;

            break;


        // ----------------------------------------------------
        // WAVES
        // ----------------------------------------------------

        case MatrixMode::WAVES:

            // The robot looks around as the wave animation starts.
            targetMood = Mood::NEUTRAL;
            targetGaze = GazeDirection::CENTER;

            enableIdleGaze = true;

            entryAnimation =
                OneShotAnimation::CONFUSED;

            break;


        // ----------------------------------------------------
        // SAND CLOCK
        // ----------------------------------------------------

        case MatrixMode::SAND_CLOCK:

            // A calmer expression looking down at falling sand.
            targetMood = Mood::TIRED;
            targetGaze = GazeDirection::DOWN;

            entryAnimation =
                OneShotAnimation::FLICKER_VERTICAL;

            break;


        // ----------------------------------------------------
        // WATER
        // ----------------------------------------------------

        case MatrixMode::WATER:

            // Curious expression looking toward the water.
            targetMood = Mood::HAPPY;
            targetGaze = GazeDirection::DOWN_RIGHT;

            enableCuriosity = true;

            entryAnimation =
                OneShotAnimation::CONFUSED;

            break;


        // ----------------------------------------------------
        // SAND GRAVITY
        // ----------------------------------------------------

        case MatrixMode::SAND_GRAVITY:

            // Watch the falling material with a surprised face.
            targetMood = Mood::SURPRISED;
            targetGaze = GazeDirection::DOWN;

            entryAnimation =
                OneShotAnimation::SHAKE_VERTICAL;

            break;


        case MatrixMode::UNKNOWN:
        default:

            targetMood = Mood::NEUTRAL;
            targetGaze = GazeDirection::CENTER;

            enableIdleGaze = true;

            break;
    }


    // --------------------------------------------------------
    // APPLY BASE EXPRESSION
    // --------------------------------------------------------

    EmotionRenderer::setMood(targetMood);

    EmotionRenderer::setCuriosity(enableCuriosity);

    EmotionRenderer::setGaze(targetGaze);

    EmotionRenderer::setIdleGaze(enableIdleGaze);


    // --------------------------------------------------------
    // PLAY MATRIX MODE ENTRY EFFECT
    // --------------------------------------------------------

    // This only runs once when entering a matrix mode.
    // Restoring a profile after tilt or shaking does not
    // replay the entry animation.

    if (playEntryEffect &&
        entryAnimation != OneShotAnimation::NONE)
    {
        EmotionRenderer::playOneShot(
            entryAnimation
        );
    }

    Serial.printf(
        "[ROBOT BEHAVIOUR] Matrix personality: %s\n",
        getMatrixModeName(mode)
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

    // Restore the current matrix personality after the
    // temporary shake reaction has finished.
    matrixProfileNeedsApply = true;

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

void update(
    uint32_t nowMs,
    const char *matrixModeName
)
{
    // --------------------------------------------------------
    // CHECK MATRIX MODE CHANGES
    // --------------------------------------------------------

    const MatrixMode detectedMatrixMode =
        parseMatrixMode(matrixModeName);

    if (!matrixModeInitialized ||
        detectedMatrixMode != currentMatrixMode)
    {
        currentMatrixMode = detectedMatrixMode;

        matrixModeInitialized = true;

        // Apply the new matrix personality when higher-priority
        // sensor reactions are not active.
        matrixProfileNeedsApply = true;

        matrixEntryEffectPending =
            detectedMatrixMode != MatrixMode::IDLE &&
            detectedMatrixMode != MatrixMode::UNKNOWN;

        Serial.printf(
            "[ROBOT BEHAVIOUR] Matrix mode detected: %s\n",
            getMatrixModeName(currentMatrixMode)
        );
    }


    // --------------------------------------------------------
    // GET THE LATEST SENSOR SNAPSHOT
    // --------------------------------------------------------

    MPU6050::Data imu{};

    const bool sensorAvailable =
        MPU6050::getData(imu) &&
        imu.valid &&
        (uint32_t)(nowMs - imu.timestampMs) <= SENSOR_STALE_MS;

    // --------------------------------------------------------
    // SENSOR UNAVAILABLE
    // --------------------------------------------------------

    if (!sensorAvailable)
    {
        // Cancel an unfinished shake animation.
        if (shakeVisualActive)
        {
            EmotionRenderer::playOneShot(
                EmotionRenderer::OneShotAnimation::NONE
            );

            shakeVisualActive = false;

            matrixProfileNeedsApply = true;
        }

        shakeArmed = true;

        // Restore the matrix personality instead of leaving
        // the previous tilt expression on the screen.
        if (currentState != BehaviourState::IDLE)
        {
            applyBehaviourState(
                BehaviourState::IDLE
            );

            matrixProfileNeedsApply = true;
        }

        if (matrixProfileNeedsApply)
        {
            applyMatrixModeProfile(
                currentMatrixMode,
                matrixEntryEffectPending
            );

            matrixEntryEffectPending = false;
            matrixProfileNeedsApply = false;
        }

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
            // Matrix mode changes are remembered and applied later.
            return;
        }

        shakeVisualActive = false;

        // The temporary shake expression has ended.
        // Restore whichever behaviour currently has priority.
        matrixProfileNeedsApply = true;
    }


    // --------------------------------------------------------
    // APPLY MATRIX PERSONALITY AND SENSOR TILT OVERLAY
    // --------------------------------------------------------

    // When the device returns to neutral, clear the previous
    // tilt behaviour before restoring the matrix personality.
    if (detectedState == BehaviourState::IDLE)
    {
        if (currentState != BehaviourState::IDLE)
        {
            applyBehaviourState(
                BehaviourState::IDLE
            );

            matrixProfileNeedsApply = true;
        }
    }


    // --------------------------------------------------------
    // APPLY MATRIX PERSONALITY
    // --------------------------------------------------------

    // Matrix mode is the base expression.
    // Apply it immediately when a mode changes or when
    // a temporary shake reaction finishes.
    if (matrixProfileNeedsApply)
    {
        applyMatrixModeProfile(
            currentMatrixMode,
            matrixEntryEffectPending
        );

        matrixEntryEffectPending = false;
        matrixProfileNeedsApply = false;

        // The matrix profile sets its own gaze.
        // Reapply the current tilt as an overlay without
        // replacing the active matrix personality.
        if (detectedState != BehaviourState::IDLE)
        {
            applyBehaviourState(
                detectedState,
                true
            );
        }
    }
    else if (detectedState != BehaviourState::IDLE)
    {
        // No new matrix personality needs applying.
        // Update only the current sensor behaviour.
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

    // Restore matrix mode detection.
    currentMatrixMode = MatrixMode::UNKNOWN;

    matrixModeInitialized = false;

    matrixProfileNeedsApply = true;
    matrixEntryEffectPending = false;

    // Restore shake detection.
    shakeArmed = true;

    shakeTriggeredBefore = false;
    lastShakeMs = 0;

    shakeVisualActive = false;
    shakeVisualUntilMs = 0;
}

} // namespace RobotBehaviour
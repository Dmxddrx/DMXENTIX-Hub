
#include "emotion_renderer.h"

#include <Arduino.h>
#include <math.h>

namespace EmotionRenderer {
namespace {

// ------------------------------------------------------------
// LANDSCAPE EYE CONFIGURATION
// ------------------------------------------------------------

constexpr float DEFAULT_EYE_WIDTH = 36.0f;
constexpr float DEFAULT_EYE_HEIGHT = 38.0f;

constexpr float DEFAULT_EYE_GAP = 18.0f;

// Maximum movement of the complete eye pair.
constexpr float MAX_GAZE_X = 12.0f;
constexpr float MAX_GAZE_Y = 7.0f;

constexpr uint16_t EYE_COLOR = 1;
constexpr uint16_t BACKGROUND_COLOR = 0;

// Animation timing.
constexpr uint32_t BLINK_CLOSE_MS = 85;
constexpr uint32_t BLINK_OPEN_MS = 115;

constexpr uint32_t MIN_BLINK_INTERVAL_MS = 2200;
constexpr uint32_t MAX_BLINK_INTERVAL_MS = 4800;

constexpr uint32_t MIN_GAZE_INTERVAL_MS = 2000;
constexpr uint32_t MAX_GAZE_INTERVAL_MS = 4500;


// ------------------------------------------------------------
// EYE MOOD GEOMETRY
// ------------------------------------------------------------

struct MoodShape
{
    float width;
    float height;

    float topLid;
    float bottomLid;

    bool angry;
};


MoodShape getMoodShape(Mood mood)
{
    switch (mood)
    {
        case Mood::HAPPY:
            return {
                38.0f,
                36.0f,
                0.0f,
                10.0f,
                false
            };

        case Mood::TIRED:
            return {
                36.0f,
                34.0f,
                12.0f,
                0.0f,
                false
            };

        case Mood::ANGRY:
            return {
                36.0f,
                36.0f,
                12.0f,
                0.0f,
                true
            };

        case Mood::SURPRISED:
            return {
                40.0f,
                42.0f,
                0.0f,
                0.0f,
                false
            };

        case Mood::NEUTRAL:
        default:
            return {
                DEFAULT_EYE_WIDTH,
                DEFAULT_EYE_HEIGHT,
                0.0f,
                0.0f,
                false
            };
    }
}


// ------------------------------------------------------------
// INDEPENDENT EYE ANIMATION STATE
// ------------------------------------------------------------

struct EyeAnimationState
{
    // Smooth eye opening / closing.
    float openness = 1.0f;
    float targetOpenness = 1.0f;

    // Independent one-shot blinking.
    bool blinking = false;
    uint32_t blinkStartMs = 0;

    // Independent eye geometry.
    float eyeWidth = DEFAULT_EYE_WIDTH;
    float eyeHeight = DEFAULT_EYE_HEIGHT;

    float topLid = 0.0f;
    float bottomLid = 0.0f;
};


struct AnimationState
{
    bool initialized = false;

    uint32_t lastFrameMs = 0;

    // Shared eye movement.
    float gazeX = 0.0f;
    float gazeY = 0.0f;

    float targetGazeX = 0.0f;
    float targetGazeY = 0.0f;

    // Independent eyes.
    EyeAnimationState leftEye{};
    EyeAnimationState rightEye{};

    // Automatic blinking schedule.
    uint32_t nextBlinkMs = 0;

    // Automatic idle gaze.
    uint32_t nextGazeMs = 0;
};


AnimationState state{};


// Default expression and independent eye expressions.
Mood requestedMood = Mood::NEUTRAL;

Mood requestedLeftMood = Mood::NEUTRAL;
Mood requestedRightMood = Mood::NEUTRAL;


// Requested eye opening states.
float requestedLeftOpenness = 1.0f;
float requestedRightOpenness = 1.0f;


// Preserve blink requests made before the first render.
bool pendingLeftBlink = false;
bool pendingRightBlink = false;


// Requested manual gaze position.
GazeDirection requestedGaze = GazeDirection::CENTER;


// Automatic idle gaze is enabled by default.
bool idleGazeEnabled = true;


// ------------------------------------------------------------
// GAZE DIRECTION COORDINATES
// ------------------------------------------------------------

struct GazeOffset
{
    float x;
    float y;
};


GazeOffset getGazeOffset(GazeDirection direction)
{
    switch (direction)
    {
        case GazeDirection::UP:
            return {0.0f, -MAX_GAZE_Y};

        case GazeDirection::UP_RIGHT:
            return {MAX_GAZE_X, -MAX_GAZE_Y};

        case GazeDirection::RIGHT:
            return {MAX_GAZE_X, 0.0f};

        case GazeDirection::DOWN_RIGHT:
            return {MAX_GAZE_X, MAX_GAZE_Y};

        case GazeDirection::DOWN:
            return {0.0f, MAX_GAZE_Y};

        case GazeDirection::DOWN_LEFT:
            return {-MAX_GAZE_X, MAX_GAZE_Y};

        case GazeDirection::LEFT:
            return {-MAX_GAZE_X, 0.0f};

        case GazeDirection::UP_LEFT:
            return {-MAX_GAZE_X, -MAX_GAZE_Y};

        case GazeDirection::CENTER:
        default:
            return {0.0f, 0.0f};
    }
}


// ------------------------------------------------------------
// TIME HELPERS
// ------------------------------------------------------------

bool timeReached(
    uint32_t now,
    uint32_t deadline
)
{
    return static_cast<int32_t>(
        now - deadline
    ) >= 0;
}


float smoothStep(float value)
{
    if (value < 0.0f) value = 0.0f;
    if (value > 1.0f) value = 1.0f;

    return value * value * (3.0f - 2.0f * value);
}


float interpolate(
    float current,
    float target,
    float amount
)
{
    return current + (target - current) * amount;
}


// ------------------------------------------------------------
// START ONE EYE BLINK
// ------------------------------------------------------------

void startBlink(
    EyeAnimationState &eye,
    uint32_t nowMs
)
{
    eye.blinking = true;
    eye.blinkStartMs = nowMs;
}


// ------------------------------------------------------------
// INITIALIZE ANIMATION
// ------------------------------------------------------------

void initializeAnimation(uint32_t nowMs)
{
    state = AnimationState{};

    state.initialized = true;
    state.lastFrameMs = nowMs;

    // Restore requested gaze.
    const GazeOffset initialGaze =
        getGazeOffset(requestedGaze);

    state.gazeX = initialGaze.x;
    state.gazeY = initialGaze.y;

    state.targetGazeX = initialGaze.x;
    state.targetGazeY = initialGaze.y;

    // Restore independent opening states.
    state.leftEye.openness = requestedLeftOpenness;
    state.leftEye.targetOpenness = requestedLeftOpenness;

    state.rightEye.openness = requestedRightOpenness;
    state.rightEye.targetOpenness = requestedRightOpenness;

    // Schedule automatic blinking.
    state.nextBlinkMs =
        nowMs + random(
            MIN_BLINK_INTERVAL_MS,
            MAX_BLINK_INTERVAL_MS
        );

    // Schedule automatic gaze.
    state.nextGazeMs =
        nowMs + random(
            MIN_GAZE_INTERVAL_MS,
            MAX_GAZE_INTERVAL_MS
        );

    // Restore any blink requests made before initialization.
    if (pendingLeftBlink)
    {
        startBlink(state.leftEye, nowMs);
        pendingLeftBlink = false;
    }

    if (pendingRightBlink)
    {
        startBlink(state.rightEye, nowMs);
        pendingRightBlink = false;
    }
}


// ------------------------------------------------------------
// UPDATE ONE EYE BLINK
// ------------------------------------------------------------

float updateBlink(
    EyeAnimationState &eye,
    uint32_t nowMs
)
{
    if (!eye.blinking)
    {
        return 1.0f;
    }

    const uint32_t elapsed =
        nowMs - eye.blinkStartMs;

    const uint32_t totalDuration =
        BLINK_CLOSE_MS + BLINK_OPEN_MS;

    // Blink completed.
    if (elapsed >= totalDuration)
    {
        eye.blinking = false;
        return 1.0f;
    }

    // Closing animation.
    if (elapsed < BLINK_CLOSE_MS)
    {
        const float progress =
            static_cast<float>(elapsed) /
            BLINK_CLOSE_MS;

        return 1.0f -
            (0.94f * smoothStep(progress));
    }

    // Opening animation.
    const float progress =
        static_cast<float>(
            elapsed - BLINK_CLOSE_MS
        ) / BLINK_OPEN_MS;

    return 0.06f +
        (0.94f * smoothStep(progress));
}


// ------------------------------------------------------------
// AUTOMATIC TWO-EYE BLINKING
// ------------------------------------------------------------

void updateAutomaticBlink(uint32_t nowMs)
{
    if (!timeReached(nowMs, state.nextBlinkMs))
    {
        return;
    }

    // Automatic blinking always affects both eyes.
    startBlink(state.leftEye, nowMs);
    startBlink(state.rightEye, nowMs);

    state.nextBlinkMs =
        nowMs + random(
            MIN_BLINK_INTERVAL_MS,
            MAX_BLINK_INTERVAL_MS
        );
}


// ------------------------------------------------------------
// AUTOMATIC IDLE GAZE
// ------------------------------------------------------------

void updateIdleGaze(uint32_t nowMs)
{
    // Manual gaze takes priority over automatic idle gaze.
    if (!idleGazeEnabled)
    {
        return;
    }

    if (!timeReached(nowMs, state.nextGazeMs))
    {
        return;
    }

    // Horizontal gaze positions.
    const float horizontalPositions[3] =
    {
        -MAX_GAZE_X,
        0.0f,
        MAX_GAZE_X
    };

    // Vertical gaze positions.
    const float verticalPositions[3] =
    {
        -MAX_GAZE_Y,
        0.0f,
        MAX_GAZE_Y
    };

    state.targetGazeX =
        horizontalPositions[random(0, 3)];

    state.targetGazeY =
        verticalPositions[random(0, 3)];

    state.nextGazeMs =
        nowMs + random(
            MIN_GAZE_INTERVAL_MS,
            MAX_GAZE_INTERVAL_MS
        );
}


// ------------------------------------------------------------
// DRAW ONE EYE MOOD OVERLAY
// ------------------------------------------------------------

void drawMoodOverlay(
    Adafruit_GFX &canvas,
    int16_t eyeX,
    int16_t eyeY,
    int16_t eyeWidth,
    int16_t visibleHeight,
    int16_t topLid,
    int16_t bottomLid,
    bool angry,
    bool rightEye
)
{
    if (visibleHeight <= 2)
    {
        return;
    }

    const int16_t maxLidHeight =
        visibleHeight - 2;

    const int16_t safeTopLid =
        (topLid < maxLidHeight)
        ? topLid
        : maxLidHeight;

    const int16_t safeBottomLid =
        (bottomLid < maxLidHeight)
        ? bottomLid
        : maxLidHeight;

    // --------------------------------------------------------
    // TIRED / ANGRY TOP EYELID
    // --------------------------------------------------------

    if (safeTopLid > 0)
    {
        if (angry)
        {
            if (!rightEye)
            {
                // Left eye: inward sloping eyelid.
                canvas.fillTriangle(
                    eyeX,
                    eyeY,
                    eyeX + eyeWidth,
                    eyeY,
                    eyeX + eyeWidth,
                    eyeY + safeTopLid,
                    BACKGROUND_COLOR
                );
            }
            else
            {
                // Right eye: inward sloping eyelid.
                canvas.fillTriangle(
                    eyeX,
                    eyeY,
                    eyeX + eyeWidth,
                    eyeY,
                    eyeX,
                    eyeY + safeTopLid,
                    BACKGROUND_COLOR
                );
            }
        }
        else
        {
            // Tired expression.
            canvas.fillRect(
                eyeX,
                eyeY,
                eyeWidth + 1,
                safeTopLid,
                BACKGROUND_COLOR
            );
        }
    }

    // --------------------------------------------------------
    // HAPPY BOTTOM EYELID
    // --------------------------------------------------------

    if (safeBottomLid > 0)
    {
        const int16_t bottomY =
            eyeY + visibleHeight - safeBottomLid;

        const int16_t radius =
            eyeWidth / 3;

        canvas.fillRoundRect(
            eyeX - 1,
            bottomY,
            eyeWidth + 2,
            safeBottomLid + 2,
            radius,
            BACKGROUND_COLOR
        );
    }
}

// ------------------------------------------------------------
// UPDATE ONE EYE'S EXPRESSION GEOMETRY
// ------------------------------------------------------------

void updateEyeShape(
    EyeAnimationState &eye,
    const MoodShape &target,
    float blend
)
{
    eye.eyeWidth = interpolate(
        eye.eyeWidth,
        target.width,
        blend
    );

    eye.eyeHeight = interpolate(
        eye.eyeHeight,
        target.height,
        blend
    );

    eye.topLid = interpolate(
        eye.topLid,
        target.topLid,
        blend
    );

    eye.bottomLid = interpolate(
        eye.bottomLid,
        target.bottomLid,
        blend
    );
}

// ------------------------------------------------------------
// DRAW ONE INDEPENDENT ROUNDED-SQUARE ROBOT EYE
// ------------------------------------------------------------

void drawSingleEye(
    Adafruit_GFX &canvas,
    EyeAnimationState &eye,
    int16_t eyeX,
    int16_t centerY,
    const MoodShape &mood,
    bool rightEye,
    uint32_t nowMs
)
{
    const int16_t eyeWidth =
        static_cast<int16_t>(
            eye.eyeWidth + 0.5f
        );

    const int16_t eyeHeight =
        static_cast<int16_t>(
            eye.eyeHeight + 0.5f
        );

    // Apply independent opening and blinking.
    const float blinkScale =
        updateBlink(eye, nowMs);

    const float totalScale =
        eye.openness * blinkScale;

    const int16_t visibleHeight =
        static_cast<int16_t>(
            eyeHeight * totalScale + 0.5f
        );

    // Closed eye.
    if (visibleHeight <= 2)
    {
        canvas.drawFastHLine(
            eyeX + 2,
            centerY,
            eyeWidth - 4,
            EYE_COLOR
        );

        return;
    }

    const int16_t eyeY =
        centerY - visibleHeight / 2;

    const int16_t cornerRadius =
        min(
            eyeWidth / 3,
            visibleHeight / 2
        );

    // --------------------------------------------------------
    // DRAW ROUNDED-SQUARE EYE
    // --------------------------------------------------------

    canvas.fillRoundRect(
        eyeX,
        eyeY,
        eyeWidth,
        visibleHeight,
        cornerRadius,
        EYE_COLOR
    );

    // --------------------------------------------------------
    // APPLY INDIVIDUAL EXPRESSION
    // --------------------------------------------------------

    drawMoodOverlay(
        canvas,
        eyeX,
        eyeY,
        eyeWidth,
        visibleHeight,
        static_cast<int16_t>(eye.topLid + 0.5f),
        static_cast<int16_t>(eye.bottomLid + 0.5f),
        mood.angry,
        rightEye
    );
}


// ------------------------------------------------------------
// LANDSCAPE ANIMATION RENDERER
// ------------------------------------------------------------

void drawLandscapeEyes(
    Adafruit_GFX &canvas,
    uint32_t nowMs
)
{
    if (!state.initialized)
    {
        initializeAnimation(nowMs);
    }

    // Calculate frame duration.
    uint32_t deltaMs =
        nowMs - state.lastFrameMs;

    state.lastFrameMs = nowMs;

    // Avoid a large animation jump after leaving the Robot page.
    if (deltaMs > 100)
    {
        deltaMs = 100;
    }

    // Time-based interpolation.
    const float blend =
        1.0f - expf(
            -static_cast<float>(deltaMs) / 140.0f
        );

    // --------------------------------------------------------
    // UPDATE INDEPENDENT EYE EXPRESSIONS
    // --------------------------------------------------------

    const MoodShape leftMood =
        getMoodShape(requestedLeftMood);

    const MoodShape rightMood =
        getMoodShape(requestedRightMood);

    updateEyeShape(
        state.leftEye,
        leftMood,
        blend
    );

    updateEyeShape(
        state.rightEye,
        rightMood,
        blend
    );

    // Smooth independent eye opening and closing.
    state.leftEye.openness = interpolate(
        state.leftEye.openness,
        state.leftEye.targetOpenness,
        blend
    );

    state.rightEye.openness = interpolate(
        state.rightEye.openness,
        state.rightEye.targetOpenness,
        blend
    );

    // --------------------------------------------------------
    // UPDATE AUTOMATIC BEHAVIOUR
    // --------------------------------------------------------

    updateIdleGaze(nowMs);

    // Smooth complete-eye movement.
    state.gazeX = interpolate(
        state.gazeX,
        state.targetGazeX,
        blend
    );

    state.gazeY = interpolate(
        state.gazeY,
        state.targetGazeY,
        blend
    );

    // Automatic blinking still affects both eyes.
    updateAutomaticBlink(nowMs);

    // --------------------------------------------------------
    // CALCULATE INDEPENDENT EYE POSITIONS
    // --------------------------------------------------------

    const int16_t leftWidth =
        static_cast<int16_t>(
            state.leftEye.eyeWidth + 0.5f
        );

    const int16_t rightWidth =
        static_cast<int16_t>(
            state.rightEye.eyeWidth + 0.5f
        );

    const int16_t eyeGap =
        static_cast<int16_t>(
            DEFAULT_EYE_GAP
        );

    const int16_t totalWidth =
        leftWidth + rightWidth + eyeGap;

    // Keep the complete eye pair centred.
    const int16_t leftX =
        (canvas.width() - totalWidth) / 2;

    const int16_t rightX =
        leftX + leftWidth + eyeGap;

    const int16_t centerY =
        canvas.height() / 2;

    // --------------------------------------------------------
    // CALCULATE COMPLETE EYE PAIR POSITION
    // --------------------------------------------------------

    const int16_t eyeOffsetX =
        static_cast<int16_t>(
            state.gazeX >= 0.0f
            ? state.gazeX + 0.5f
            : state.gazeX - 0.5f
        );

    const int16_t eyeOffsetY =
        static_cast<int16_t>(
            state.gazeY >= 0.0f
            ? state.gazeY + 0.5f
            : state.gazeY - 0.5f
        );

    const int16_t eyeCenterY =
        centerY + eyeOffsetY;

    // --------------------------------------------------------
    // DRAW LEFT EYE
    // --------------------------------------------------------

    drawSingleEye(
        canvas,
        state.leftEye,
        leftX + eyeOffsetX,
        eyeCenterY,
        leftMood,
        false,
        nowMs
    );

    // --------------------------------------------------------
    // DRAW RIGHT EYE
    // --------------------------------------------------------

    drawSingleEye(
        canvas,
        state.rightEye,
        rightX + eyeOffsetX,
        eyeCenterY,
        rightMood,
        true,
        nowMs
    );
}

} // namespace


// ============================================================
// PUBLIC API
// ============================================================

// ------------------------------------------------------------
// EXISTING STATIC EYES
// Used as the portrait fallback for now.
// ------------------------------------------------------------

void drawNeutralEyes(
    Adafruit_GFX &canvas,
    uint16_t color
)
{
    const int16_t screenWidth = canvas.width();
    const int16_t screenHeight = canvas.height();

    if (screenWidth <= 0 || screenHeight <= 0)
    {
        return;
    }

    const int16_t eyeWidth = 18;
    const int16_t eyeHeight = 28;

    const int16_t eyeGap = 16;

    const int16_t cornerRadius =
        eyeWidth / 3;

    const int16_t totalWidth =
        eyeWidth * 2 + eyeGap;

    const int16_t leftX =
        (screenWidth - totalWidth) / 2;

    const int16_t rightX =
        leftX + eyeWidth + eyeGap;

    const int16_t eyeY =
        (screenHeight - eyeHeight) / 2;

    canvas.fillRoundRect(
        leftX,
        eyeY,
        eyeWidth,
        eyeHeight,
        cornerRadius,
        color
    );

    canvas.fillRoundRect(
        rightX,
        eyeY,
        eyeWidth,
        eyeHeight,
        cornerRadius,
        color
    );
}


// ------------------------------------------------------------
// MAIN ROBOT EYE RENDERER
// ------------------------------------------------------------

void drawRobotEyes(
    Adafruit_GFX &canvas,
    uint32_t nowMs
)
{
    // Portrait animation will be implemented separately.
    if (canvas.width() < 100)
    {
        drawNeutralEyes(canvas);
        return;
    }

    drawLandscapeEyes(canvas, nowMs);
}


// ------------------------------------------------------------
// CHANGE MOOD
// ------------------------------------------------------------

void setMood(Mood mood)
{
    requestedMood = mood;

    // A normal mood change affects both eyes.
    requestedLeftMood = mood;
    requestedRightMood = mood;
}


Mood getMood()
{
    return requestedMood;
}

// ------------------------------------------------------------
// MANUAL GAZE CONTROL
// ------------------------------------------------------------

void setGaze(GazeDirection direction)
{
    requestedGaze = direction;

    // Manual gaze takes priority.
    idleGazeEnabled = false;

    const GazeOffset target =
        getGazeOffset(direction);

    // Set the target without interrupting smooth interpolation.
    state.targetGazeX = target.x;
    state.targetGazeY = target.y;
}


// ------------------------------------------------------------
// AUTOMATIC IDLE GAZE CONTROL
// ------------------------------------------------------------

void setIdleGaze(bool enabled)
{
    idleGazeEnabled = enabled;

    // Schedule the next automatic gaze movement.
    if (enabled)
    {
        state.nextGazeMs =
            millis() + random(
                MIN_GAZE_INTERVAL_MS,
                MAX_GAZE_INTERVAL_MS
            );
    }
}

// ------------------------------------------------------------
// INDEPENDENT EYE OPEN / CLOSE
// ------------------------------------------------------------

void setEyeOpen(Eye eye, bool open)
{
    const float target =
        open ? 1.0f : 0.0f;

    if (eye == Eye::LEFT || eye == Eye::BOTH)
    {
        requestedLeftOpenness = target;

        if (state.initialized)
        {
            state.leftEye.targetOpenness = target;
        }
    }

    if (eye == Eye::RIGHT || eye == Eye::BOTH)
    {
        requestedRightOpenness = target;

        if (state.initialized)
        {
            state.rightEye.targetOpenness = target;
        }
    }
}


// ------------------------------------------------------------
// MANUAL BLINK
// ------------------------------------------------------------

void blink(Eye eye)
{
    const uint32_t nowMs = millis();

    if (eye == Eye::LEFT || eye == Eye::BOTH)
    {
        if (state.initialized)
        {
            startBlink(state.leftEye, nowMs);
        }
        else
        {
            pendingLeftBlink = true;
        }
    }

    if (eye == Eye::RIGHT || eye == Eye::BOTH)
    {
        if (state.initialized)
        {
            startBlink(state.rightEye, nowMs);
        }
        else
        {
            pendingRightBlink = true;
        }
    }
}


// ------------------------------------------------------------
// ONE-EYE WINK
// ------------------------------------------------------------

void wink(Eye eye)
{
    if (eye == Eye::LEFT || eye == Eye::RIGHT)
    {
        blink(eye);
    }
}


// ------------------------------------------------------------
// INDEPENDENT EYE MOOD
// ------------------------------------------------------------

void setEyeMood(Eye eye, Mood mood)
{
    if (eye == Eye::BOTH)
    {
        setMood(mood);
        return;
    }

    if (eye == Eye::LEFT)
    {
        requestedLeftMood = mood;
    }
    else if (eye == Eye::RIGHT)
    {
        requestedRightMood = mood;
    }
}

// ------------------------------------------------------------
// RESET ANIMATION
// ------------------------------------------------------------

void reset()
{
    state = AnimationState{};

    requestedMood = Mood::NEUTRAL;

    requestedLeftMood = Mood::NEUTRAL;
    requestedRightMood = Mood::NEUTRAL;

    requestedLeftOpenness = 1.0f;
    requestedRightOpenness = 1.0f;

    pendingLeftBlink = false;
    pendingRightBlink = false;

    requestedGaze = GazeDirection::CENTER;

    idleGazeEnabled = true;
}

} // namespace EmotionRenderer

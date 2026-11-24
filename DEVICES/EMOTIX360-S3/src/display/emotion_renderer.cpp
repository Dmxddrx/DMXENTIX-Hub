
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

constexpr float MAX_GAZE_X = 5.0f;
constexpr float MAX_GAZE_Y = 3.0f;

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
// ANIMATION STATE
// ------------------------------------------------------------

struct AnimationState
{
    bool initialized = false;

    uint32_t lastFrameMs = 0;

    // Eye geometry.
    float eyeWidth = DEFAULT_EYE_WIDTH;
    float eyeHeight = DEFAULT_EYE_HEIGHT;

    float topLid = 0.0f;
    float bottomLid = 0.0f;

    // Current gaze.
    float gazeX = 0.0f;
    float gazeY = 0.0f;

    float targetGazeX = 0.0f;
    float targetGazeY = 0.0f;

    // Automatic blinking.
    bool blinking = false;

    uint32_t blinkStartMs = 0;
    uint32_t nextBlinkMs = 0;

    // Automatic idle gaze.
    uint32_t nextGazeMs = 0;
};

AnimationState state{};

Mood requestedMood = Mood::NEUTRAL;

// Requested manual gaze position.
GazeDirection requestedGaze =
    GazeDirection::CENTER;

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
// INITIALIZE ANIMATION
// ------------------------------------------------------------

void initializeAnimation(uint32_t nowMs)
{
    state = AnimationState{};

    state.initialized = true;
    state.lastFrameMs = nowMs;

    // Restore the requested gaze position after initialization.
    const GazeOffset initialGaze =
        getGazeOffset(requestedGaze);

    state.gazeX = initialGaze.x;
    state.gazeY = initialGaze.y;

    state.targetGazeX = initialGaze.x;
    state.targetGazeY = initialGaze.y;

    state.nextBlinkMs =
        nowMs + random(
            MIN_BLINK_INTERVAL_MS,
            MAX_BLINK_INTERVAL_MS
        );

    state.nextGazeMs =
        nowMs + random(
            MIN_GAZE_INTERVAL_MS,
            MAX_GAZE_INTERVAL_MS
        );
}


// ------------------------------------------------------------
// AUTOMATIC BLINKING
// ------------------------------------------------------------

float updateBlink(uint32_t nowMs)
{
    // Start a new blink when its scheduled time arrives.
    if (!state.blinking &&
        timeReached(nowMs, state.nextBlinkMs))
    {
        state.blinking = true;
        state.blinkStartMs = nowMs;
    }

    if (!state.blinking)
    {
        return 1.0f;
    }

    const uint32_t elapsed =
        nowMs - state.blinkStartMs;

    const uint32_t totalDuration =
        BLINK_CLOSE_MS + BLINK_OPEN_MS;

    // Blink has completed.
    if (elapsed >= totalDuration)
    {
        state.blinking = false;

        state.nextBlinkMs =
            nowMs + random(
                MIN_BLINK_INTERVAL_MS,
                MAX_BLINK_INTERVAL_MS
            );

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
// DRAW EYE MOOD OVERLAYS
// ------------------------------------------------------------

void drawMoodOverlay(
    Adafruit_GFX &canvas,
    int16_t leftX,
    int16_t rightX,
    int16_t eyeY,
    int16_t eyeWidth,
    int16_t visibleHeight,
    int16_t topLid,
    int16_t bottomLid,
    bool angry
)
{
    if (visibleHeight <= 2)
    {
        return;
    }

    const int16_t maxLidHeight =
        static_cast<int16_t>(visibleHeight - 2);

    const int16_t safeTopLid =
        (topLid < maxLidHeight)
        ? topLid
        : maxLidHeight;

    const int16_t safeBottomLid =
        (bottomLid < maxLidHeight)
        ? bottomLid
        : maxLidHeight;

    // --------------------------------------------------------
    // TIRED / ANGRY TOP EYELIDS
    // --------------------------------------------------------

    if (safeTopLid > 0)
    {
        if (angry)
        {
            // Left eye: inward sloping eyelid.
            canvas.fillTriangle(
                leftX,
                eyeY,
                leftX + eyeWidth,
                eyeY,
                leftX + eyeWidth,
                eyeY + safeTopLid,
                BACKGROUND_COLOR
            );

            // Right eye: inward sloping eyelid.
            canvas.fillTriangle(
                rightX,
                eyeY,
                rightX + eyeWidth,
                eyeY,
                rightX,
                eyeY + safeTopLid,
                BACKGROUND_COLOR
            );
        }
        else
        {
            // Tired expression: straight upper eyelids.
            canvas.fillRect(
                leftX,
                eyeY,
                eyeWidth + 1,
                safeTopLid,
                BACKGROUND_COLOR
            );

            canvas.fillRect(
                rightX,
                eyeY,
                eyeWidth + 1,
                safeTopLid,
                BACKGROUND_COLOR
            );
        }
    }

    // --------------------------------------------------------
    // HAPPY BOTTOM EYELIDS
    // --------------------------------------------------------

    if (safeBottomLid > 0)
    {
        const int16_t bottomY =
            eyeY + visibleHeight - safeBottomLid;

        const int16_t radius =
            eyeWidth / 3;

        canvas.fillRoundRect(
            leftX - 1,
            bottomY,
            eyeWidth + 2,
            safeBottomLid + 2,
            radius,
            BACKGROUND_COLOR
        );

        canvas.fillRoundRect(
            rightX - 1,
            bottomY,
            eyeWidth + 2,
            safeBottomLid + 2,
            radius,
            BACKGROUND_COLOR
        );
    }
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

    // Get requested expression geometry.
    const MoodShape target =
        getMoodShape(requestedMood);

    // Smoothly transition eye geometry.
    state.eyeWidth = interpolate(
        state.eyeWidth,
        target.width,
        blend
    );

    state.eyeHeight = interpolate(
        state.eyeHeight,
        target.height,
        blend
    );

    state.topLid = interpolate(
        state.topLid,
        target.topLid,
        blend
    );

    state.bottomLid = interpolate(
        state.bottomLid,
        target.bottomLid,
        blend
    );

    // Update automatic behaviour.
    updateIdleGaze(nowMs);

    // Smooth gaze movement.
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

    // Update blinking.
    const float blinkScale =
        updateBlink(nowMs);

    // --------------------------------------------------------
    // CALCULATE EYE DIMENSIONS
    // --------------------------------------------------------

    const int16_t eyeWidth =
        static_cast<int16_t>(
            state.eyeWidth + 0.5f
        );

    const int16_t eyeHeight =
        static_cast<int16_t>(
            state.eyeHeight + 0.5f
        );

    const int16_t calculatedHeight =
        static_cast<int16_t>(
            eyeHeight * blinkScale
        );

    const int16_t visibleHeight =
        (calculatedHeight < 2)
        ? 2
        : calculatedHeight;

    const int16_t eyeGap =
        static_cast<int16_t>(
            DEFAULT_EYE_GAP
        );

    // --------------------------------------------------------
    // CALCULATE EYE POSITIONS
    // --------------------------------------------------------

    const int16_t totalWidth =
        eyeWidth * 2 + eyeGap;

    const int16_t baseX =
        (canvas.width() - totalWidth) / 2;

    const int16_t gazeOffsetX =
        static_cast<int16_t>(
            state.gazeX + 0.5f
        );

    const int16_t gazeOffsetY =
        static_cast<int16_t>(
            state.gazeY + 0.5f
        );

    const int16_t leftX =
        baseX + gazeOffsetX;

    const int16_t rightX =
        leftX + eyeWidth + eyeGap;

    const int16_t eyeY =
        (canvas.height() - eyeHeight) / 2 +
        gazeOffsetY +
        (eyeHeight - visibleHeight) / 2;

    const int16_t radius =
        min(
            eyeWidth / 3,
            visibleHeight / 2
        );

    // --------------------------------------------------------
    // DRAW LEFT AND RIGHT EYES
    // --------------------------------------------------------

    canvas.fillRoundRect(
        leftX,
        eyeY,
        eyeWidth,
        visibleHeight,
        radius,
        EYE_COLOR
    );

    canvas.fillRoundRect(
        rightX,
        eyeY,
        eyeWidth,
        visibleHeight,
        radius,
        EYE_COLOR
    );

    // --------------------------------------------------------
    // APPLY EXPRESSION
    // --------------------------------------------------------

    drawMoodOverlay(
        canvas,
        leftX,
        rightX,
        eyeY,
        eyeWidth,
        visibleHeight,
        static_cast<int16_t>(state.topLid + 0.5f),
        static_cast<int16_t>(state.bottomLid + 0.5f),
        target.angry
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
// RESET ANIMATION
// ------------------------------------------------------------

void reset()
{
    state = AnimationState{};

    requestedMood = Mood::NEUTRAL;

    requestedGaze = GazeDirection::CENTER;

    idleGazeEnabled = true;
}

} // namespace EmotionRenderer

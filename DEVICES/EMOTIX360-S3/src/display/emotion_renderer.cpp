
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

// ------------------------------------------------------------
// PORTRAIT EYE LAYOUT
// ------------------------------------------------------------

// Preserve the existing 18 x 28 portrait eye dimensions.
constexpr float PORTRAIT_SCALE_X =
    18.0f / DEFAULT_EYE_WIDTH;

constexpr float PORTRAIT_SCALE_Y =
    28.0f / DEFAULT_EYE_HEIGHT;

// Preserve the existing 16-pixel portrait eye spacing.
constexpr float PORTRAIT_GAP_SCALE =
    16.0f / DEFAULT_EYE_GAP;

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

// One-shot animation durations.
constexpr uint32_t FLICKER_DURATION_MS = 520;
constexpr uint32_t CONFUSED_DURATION_MS = 1200;
constexpr uint32_t LAUGH_DURATION_MS = 1200;
constexpr uint32_t SHAKE_DURATION_MS = 700;
constexpr uint32_t SWEAT_DURATION_MS = 1500;

constexpr float ANIMATION_PI = 3.14159265f;
constexpr float ANIMATION_TWO_PI = 6.28318530f;


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

    // Independent geometry scaling.
    float geometryScaleX = 1.0f;
    float targetGeometryScaleX = 1.0f;

    float geometryScaleY = 1.0f;
    float targetGeometryScaleY = 1.0f;

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

    // Smooth eye spacing.
    float eyeGap = DEFAULT_EYE_GAP;
    float targetEyeGap = DEFAULT_EYE_GAP;

    // Corner radius multiplier.
    float cornerRadiusScale = 1.0f;
    float targetCornerRadiusScale = 1.0f;

    // Smooth transition between a normal eye pair and Cyclops.
    float cyclopsBlend = 0.0f;
    float targetCyclopsBlend = 0.0f;

    // --------------------------------------------------------
    // ONE-SHOT ANIMATION STATE
    // --------------------------------------------------------

    OneShotAnimation activeOneShot = OneShotAnimation::NONE;

    uint32_t oneShotStartMs = 0;
    uint32_t oneShotDurationMs = 0;

    // Smooth curiosity scaling for each eye.
    float leftCuriosityScale = 1.0f;
    float targetLeftCuriosityScale = 1.0f;

    float rightCuriosityScale = 1.0f;
    float targetRightCuriosityScale = 1.0f;

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

// Automatic blinking is enabled by default.
bool automaticBlinkEnabled = true;

// Curiosity is disabled by default.
bool curiosityEnabled = false;

// ------------------------------------------------------------
// REQUESTED EYE GEOMETRY
// ------------------------------------------------------------

// Independent left-eye dimensions.
float requestedLeftScaleX = 1.0f;
float requestedLeftScaleY = 1.0f;

// Independent right-eye dimensions.
float requestedRightScaleX = 1.0f;
float requestedRightScaleY = 1.0f;

// Shared eye spacing.
float requestedEyeGap = DEFAULT_EYE_GAP;

// Shared corner radius multiplier.
float requestedCornerRadiusScale = 1.0f;

// Single-eye layout.
bool requestedCyclopsMode = false;

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
// ONE-SHOT ANIMATION FRAME
// ------------------------------------------------------------

struct OneShotFrame
{
    float scaleX = 1.0f;
    float scaleY = 1.0f;

    float offsetX = 0.0f;
    float offsetY = 0.0f;

    bool useHappyMood = false;

    // Animated sweat droplets.
    bool showLeftSweat = false;
    bool showRightSweat = false;

    float sweatProgress = 0.0f;
};


// ------------------------------------------------------------
// UPDATE ONE-SHOT ANIMATION
// ------------------------------------------------------------

OneShotFrame updateOneShotAnimation(uint32_t nowMs)
{
    OneShotFrame frame{};

    if (state.activeOneShot == OneShotAnimation::NONE ||
        state.oneShotDurationMs == 0)
    {
        return frame;
    }

    const uint32_t elapsed =
        nowMs - state.oneShotStartMs;

    // One-shot animation has finished.
    if (elapsed >= state.oneShotDurationMs)
    {
        state.activeOneShot = OneShotAnimation::NONE;
        state.oneShotDurationMs = 0;

        return frame;
    }

    const float progress =
        static_cast<float>(elapsed) /
        static_cast<float>(state.oneShotDurationMs);

    // Smooth envelope: starts and finishes at zero.
    const float envelope =
        sinf(progress * ANIMATION_PI);

    switch (state.activeOneShot)
    {
        // ----------------------------------------------------
        // HORIZONTAL FLICKER
        // ----------------------------------------------------

        case OneShotAnimation::FLICKER_HORIZONTAL:
        {
            const float phase =
                static_cast<float>(elapsed) * ANIMATION_TWO_PI / 65.0f;

            const float pulse =
                0.5f + 0.5f * sinf(phase);

            frame.scaleX =
                1.0f - (0.82f * envelope * pulse);

            break;
        }

        // ----------------------------------------------------
        // VERTICAL FLICKER
        // ----------------------------------------------------

        case OneShotAnimation::FLICKER_VERTICAL:
        {
            const float phase =
                static_cast<float>(elapsed) * ANIMATION_TWO_PI / 65.0f;

            const float pulse =
                0.5f + 0.5f * sinf(phase);

            frame.scaleY =
                1.0f - (0.82f * envelope * pulse);

            break;
        }

        // ----------------------------------------------------
        // CONFUSED
        // ----------------------------------------------------

        case OneShotAnimation::CONFUSED:
        {
            const float phase =
                static_cast<float>(elapsed) *
                ANIMATION_TWO_PI / 260.0f;

            // Alternate gaze direction.
            frame.offsetX =
                9.0f * sinf(phase) * envelope;

            // Add a smaller vertical wobble.
            frame.offsetY =
                2.5f * sinf(phase * 0.5f) * envelope;

            break;
        }

        // ----------------------------------------------------
        // LAUGH
        // ----------------------------------------------------

        case OneShotAnimation::LAUGH:
        {
            const float pulse =
                fabsf(
                    sinf(
                        progress * 3.0f * ANIMATION_PI
                    )
                );

            frame.useHappyMood = true;

            // Repeated vertical squashing.
            frame.scaleY =
                1.0f - (0.48f * envelope * pulse);

            // Slight horizontal expansion.
            frame.scaleX =
                1.0f + (0.04f * envelope * pulse);

            break;
        }

        // ----------------------------------------------------
        // HORIZONTAL SHAKE
        // ----------------------------------------------------

        case OneShotAnimation::SHAKE_HORIZONTAL:
        {
            const float phase =
                static_cast<float>(elapsed) *
                ANIMATION_TWO_PI / 70.0f;

            const float decay =
                1.0f - progress;

            frame.offsetX =
                8.0f * sinf(phase) * decay;

            break;
        }

        // ----------------------------------------------------
        // VERTICAL SHAKE
        // ----------------------------------------------------

        case OneShotAnimation::SHAKE_VERTICAL:
        {
            const float phase =
                static_cast<float>(elapsed) *
                ANIMATION_TWO_PI / 70.0f;

            const float decay =
                1.0f - progress;

            frame.offsetY =
                6.0f * sinf(phase) * decay;

            break;
        }

                // ----------------------------------------------------
        // SWEAT — LEFT SIDE
        // ----------------------------------------------------

        case OneShotAnimation::SWEAT_LEFT:
        {
            frame.showLeftSweat = true;
            frame.sweatProgress = progress;

            break;
        }

        // ----------------------------------------------------
        // SWEAT — RIGHT SIDE
        // ----------------------------------------------------

        case OneShotAnimation::SWEAT_RIGHT:
        {
            frame.showRightSweat = true;
            frame.sweatProgress = progress;

            break;
        }

        // ----------------------------------------------------
        // SWEAT — BOTH SIDES
        // ----------------------------------------------------

        case OneShotAnimation::SWEAT_BOTH:
        {
            frame.showLeftSweat = true;
            frame.showRightSweat = true;

            frame.sweatProgress = progress;

            break;
        }

        case OneShotAnimation::NONE:
        default:
            break;
    }

    return frame;
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

    // --------------------------------------------------------
    // RESTORE REQUESTED GEOMETRY
    // --------------------------------------------------------

    state.leftEye.geometryScaleX = requestedLeftScaleX;
    state.leftEye.targetGeometryScaleX = requestedLeftScaleX;

    state.leftEye.geometryScaleY = requestedLeftScaleY;
    state.leftEye.targetGeometryScaleY = requestedLeftScaleY;

    state.rightEye.geometryScaleX = requestedRightScaleX;
    state.rightEye.targetGeometryScaleX = requestedRightScaleX;

    state.rightEye.geometryScaleY = requestedRightScaleY;
    state.rightEye.targetGeometryScaleY = requestedRightScaleY;

    state.eyeGap = requestedEyeGap;
    state.targetEyeGap = requestedEyeGap;

    state.cornerRadiusScale = requestedCornerRadiusScale;
    state.targetCornerRadiusScale = requestedCornerRadiusScale;

    // Start with the normal pair and animate toward the requested layout.
    state.cyclopsBlend = 0.0f;

    state.targetCyclopsBlend =
        requestedCyclopsMode ? 1.0f : 0.0f;

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
    if (!automaticBlinkEnabled)
    {
        return;
    }

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
    float cornerRadiusScale,
    float renderScaleX,
    float renderScaleY,
    const MoodShape &mood,
    bool rightEye,
    uint32_t nowMs
)
{
    const int16_t eyeWidth =
        static_cast<int16_t>(
            eye.eyeWidth * renderScaleX + 0.5f
        );

    const int16_t eyeHeight =
        static_cast<int16_t>(
            eye.eyeHeight * renderScaleY + 0.5f
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

    // Calculate the requested corner radius.
    const float requestedRadius =
        (static_cast<float>(eyeWidth) / 3.0f) *
        cornerRadiusScale;

    int16_t cornerRadius =
        static_cast<int16_t>(requestedRadius + 0.5f);

    // Prevent the radius from exceeding the eye dimensions.
    const int16_t maxCornerRadius =
        min(
            eyeWidth / 2,
            visibleHeight / 2
        );

    if (cornerRadius > maxCornerRadius)
    {
        cornerRadius = maxCornerRadius;
    }

    if (cornerRadius < 0)
    {
        cornerRadius = 0;
    }

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

        static_cast<int16_t>(
            eye.topLid * renderScaleY + 0.5f
        ),

        static_cast<int16_t>(
            eye.bottomLid * renderScaleY + 0.5f
        ),

        mood.angry,
        rightEye
    );
}

// ------------------------------------------------------------
// DRAW ANIMATED SWEAT DROPLET
// ------------------------------------------------------------
//
// Draws a small downward-pointing teardrop.
//
// The animation position is controlled by the caller.
// This function only draws the droplet.
//

void drawSweatDrop(
    Adafruit_GFX &canvas,
    int16_t centerX,
    int16_t topY
)
{
    // Pointed upper section.
    canvas.fillTriangle(
        centerX,
        topY,
        centerX - 2,
        topY + 4,
        centerX + 2,
        topY + 4,
        EYE_COLOR
    );

    // Rounded lower section.
    canvas.fillCircle(
        centerX,
        topY + 4,
        2,
        EYE_COLOR
    );
}

// ------------------------------------------------------------
// ANIMATED ROBOT EYE RENDERER
// Supports both landscape and portrait layouts.
// ------------------------------------------------------------

void drawAnimatedEyes(
    Adafruit_GFX &canvas,
    uint32_t nowMs,
    bool portrait
)
{
    // Select drawing dimensions for the current orientation.
    // Animation state remains in the original landscape units.

    const float renderScaleX =
        portrait ? PORTRAIT_SCALE_X : 1.0f;

    const float renderScaleY =
        portrait ? PORTRAIT_SCALE_Y : 1.0f;

    const float renderGapScale =
        portrait ? PORTRAIT_GAP_SCALE : 1.0f;

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
    // UPDATE INDEPENDENT EYE GEOMETRY
    // --------------------------------------------------------

    state.leftEye.geometryScaleX = interpolate(
        state.leftEye.geometryScaleX,
        state.leftEye.targetGeometryScaleX,
        blend
    );

    state.leftEye.geometryScaleY = interpolate(
        state.leftEye.geometryScaleY,
        state.leftEye.targetGeometryScaleY,
        blend
    );

    state.rightEye.geometryScaleX = interpolate(
        state.rightEye.geometryScaleX,
        state.rightEye.targetGeometryScaleX,
        blend
    );

    state.rightEye.geometryScaleY = interpolate(
        state.rightEye.geometryScaleY,
        state.rightEye.targetGeometryScaleY,
        blend
    );

    // Smooth shared geometry.
    state.eyeGap = interpolate(
        state.eyeGap,
        state.targetEyeGap,
        blend
    );

    state.cornerRadiusScale = interpolate(
        state.cornerRadiusScale,
        state.targetCornerRadiusScale,
        blend
    );

    // Smoothly merge or separate the two eyes.
    state.cyclopsBlend = interpolate(
        state.cyclopsBlend,
        state.targetCyclopsBlend,
        blend
    );

    // --------------------------------------------------------
    // UPDATE AUTOMATIC / MANUAL GAZE
    // --------------------------------------------------------

    updateIdleGaze(nowMs);

    // Smooth horizontal and vertical gaze movement.
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


    // --------------------------------------------------------
    // UPDATE CURIOSITY
    // --------------------------------------------------------

    float targetLeftCuriosity = 1.0f;
    float targetRightCuriosity = 1.0f;

    if (curiosityEnabled)
    {
        // Calculate how far the eyes are looking horizontally.
        const float normalizedGaze =
            fabsf(state.gazeX) / MAX_GAZE_X;

        // Curiosity increases gradually as horizontal gaze grows.
        const float curiosityAmount =
            smoothStep(
                (normalizedGaze - 0.12f) / 0.68f
            );

        const float focusScale =
            1.0f + (0.18f * curiosityAmount);

        // Enlarge the eye on the side being inspected.
        if (state.gazeX < -0.5f)
        {
            targetLeftCuriosity = focusScale;
        }
        else if (state.gazeX > 0.5f)
        {
            targetRightCuriosity = focusScale;
        }
    }

    // Smooth enlargement and restoration.
    state.leftCuriosityScale = interpolate(
        state.leftCuriosityScale,
        targetLeftCuriosity,
        blend
    );

    state.rightCuriosityScale = interpolate(
        state.rightCuriosityScale,
        targetRightCuriosity,
        blend
    );
    // --------------------------------------------------------
    // UPDATE INDEPENDENT EYE EXPRESSIONS
    // --------------------------------------------------------

    // --------------------------------------------------------
    // UPDATE ONE-SHOT ANIMATION
    // --------------------------------------------------------

    const OneShotFrame oneShot =
        updateOneShotAnimation(nowMs);


    // --------------------------------------------------------
    // UPDATE INDEPENDENT EYE EXPRESSIONS
    // --------------------------------------------------------

    MoodShape leftMood =
        oneShot.useHappyMood
        ? getMoodShape(Mood::HAPPY)
        : getMoodShape(requestedLeftMood);

    MoodShape rightMood =
        oneShot.useHappyMood
        ? getMoodShape(Mood::HAPPY)
        : getMoodShape(requestedRightMood);

    // Apply geometry, Curiosity and one-shot scaling.
    leftMood.width *=
        state.leftEye.geometryScaleX *
        state.leftCuriosityScale *
        oneShot.scaleX;

    leftMood.height *=
        state.leftEye.geometryScaleY *
        state.leftCuriosityScale *
        oneShot.scaleY;

    rightMood.width *=
        state.rightEye.geometryScaleX *
        state.rightCuriosityScale *
        oneShot.scaleX;

    rightMood.height *=
        state.rightEye.geometryScaleY *
        state.rightCuriosityScale *
        oneShot.scaleY;

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
    // UPDATE AUTOMATIC BLINKING
    // --------------------------------------------------------

    updateAutomaticBlink(nowMs);

    // --------------------------------------------------------
    // CALCULATE INDEPENDENT EYE POSITIONS
    // --------------------------------------------------------

    const int16_t leftWidth =
        static_cast<int16_t>(
            state.leftEye.eyeWidth * renderScaleX + 0.5f
        );

    const int16_t rightWidth =
        static_cast<int16_t>(
            state.rightEye.eyeWidth * renderScaleX + 0.5f
        );

    const int16_t eyeGap =
        static_cast<int16_t>(
            state.eyeGap * renderGapScale + 0.5f
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

    const float combinedOffsetX =
        (state.gazeX + oneShot.offsetX) * renderScaleX;

    const float combinedOffsetY =
        (state.gazeY + oneShot.offsetY) * renderScaleY;

    int16_t eyeOffsetX =
        static_cast<int16_t>(
            combinedOffsetX >= 0.0f
            ? combinedOffsetX + 0.5f
            : combinedOffsetX - 0.5f
        );

    // --------------------------------------------------------
    // KEEP PORTRAIT EYES INSIDE DISPLAY BOUNDARIES
    // --------------------------------------------------------

    if (portrait)
    {
        // When Cyclops is active, both eye centres converge.
        // Therefore, use an interpolated effective pair width
        // instead of permanently reserving space for two eyes.

        const float widestEye =
            (leftWidth > rightWidth)
            ? static_cast<float>(leftWidth)
            : static_cast<float>(rightWidth);

        const float effectivePairWidth =
            static_cast<float>(totalWidth) +
            (widestEye - static_cast<float>(totalWidth)) *
            state.cyclopsBlend;

        float availableOffset =
            (static_cast<float>(canvas.width()) -
             effectivePairWidth) * 0.5f - 1.0f;

        if (availableOffset < 0.0f)
        {
            availableOffset = 0.0f;
        }

        const int16_t maxPortraitOffset =
            static_cast<int16_t>(availableOffset);

        if (eyeOffsetX > maxPortraitOffset)
        {
            eyeOffsetX = maxPortraitOffset;
        }
        else if (eyeOffsetX < -maxPortraitOffset)
        {
            eyeOffsetX = -maxPortraitOffset;
        }
    }

    const int16_t eyeOffsetY =
        static_cast<int16_t>(
            combinedOffsetY >= 0.0f
            ? combinedOffsetY + 0.5f
            : combinedOffsetY - 0.5f
        );

    const int16_t eyeCenterY =
        centerY + eyeOffsetY;

    // --------------------------------------------------------
    // SMOOTH CYCLOPS LAYOUT TRANSITION
    // --------------------------------------------------------

    // Centre position of the display, including horizontal gaze.
    const float displayCenterX =
        (static_cast<float>(canvas.width()) - 1.0f) / 2.0f
        + eyeOffsetX;

    // Normal centre positions of both eyes.
    const float normalLeftCenterX =
        static_cast<float>(leftX) +
        (static_cast<float>(leftWidth) / 2.0f) +
        eyeOffsetX;

    const float normalRightCenterX =
        static_cast<float>(rightX) +
        (static_cast<float>(rightWidth) / 2.0f) +
        eyeOffsetX;

    // Move both eyes smoothly toward the same centre point.
    const float animatedLeftCenterX =
        interpolate(
            normalLeftCenterX,
            displayCenterX,
            state.cyclopsBlend
        );

    const float animatedRightCenterX =
        interpolate(
            normalRightCenterX,
            displayCenterX,
            state.cyclopsBlend
        );

    // --------------------------------------------------------
    // CONVERT EYE CENTRES BACK INTO DRAWING POSITIONS
    // --------------------------------------------------------
    const int16_t leftDrawX =
        static_cast<int16_t>(
            animatedLeftCenterX -
            (static_cast<float>(leftWidth) / 2.0f) +
            0.5f
        );

    const int16_t rightDrawX =
        static_cast<int16_t>(
            animatedRightCenterX -
            (static_cast<float>(rightWidth) / 2.0f) +
            0.5f
        );

    // --------------------------------------------------------
    // DRAW THE CONVERGING EYE PAIR
    // --------------------------------------------------------

    // Both eyes continuously move toward the display centre.
    // In full Cyclops mode, their identical shapes overlap
    // perfectly and visually become one central eye.

    // --------------------------------------------------------
    // DRAW LEFT EYE
    // --------------------------------------------------------

    drawSingleEye(
        canvas,
        state.leftEye,
        leftDrawX,
        eyeCenterY,
        state.cornerRadiusScale,
        renderScaleX,
        renderScaleY,
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
        rightDrawX,
        eyeCenterY,
        state.cornerRadiusScale,
        renderScaleX,
        renderScaleY,
        rightMood,
        true,
        nowMs
    );

    // --------------------------------------------------------
    // ANIMATED SWEAT DROPLETS
    // --------------------------------------------------------

    // Smooth the droplet's movement along the outside edges.
    const float sweatTravel =
        smoothStep(oneShot.sweatProgress);

    // Move the droplets downward.
    const int16_t sweatOffsetY =
        static_cast<int16_t>(
            22.0f * renderScaleY * sweatTravel + 0.5f
        );

    // --------------------------------------------------------
    // LEFT SWEAT DROPLET
    // --------------------------------------------------------

    if (oneShot.showLeftSweat)
    {
        // Start just outside the upper-left eye corner.
        int16_t leftDropX =
            leftDrawX -
            static_cast<int16_t>(3.0f * renderScaleX + 0.5f) -
            static_cast<int16_t>(
                3.0f * renderScaleX * sweatTravel + 0.5f
            );

        if (leftDropX < 2)
        {
            leftDropX = 2;
        }

        const int16_t leftEyeTop =
            eyeCenterY -
            static_cast<int16_t>(
                state.leftEye.eyeHeight *
                renderScaleY * 0.5f + 0.5f
            );

        const int16_t leftDropY =
            leftEyeTop - 5 + sweatOffsetY;

        drawSweatDrop(
            canvas,
            leftDropX,
            leftDropY
        );
    }

    // --------------------------------------------------------
    // RIGHT SWEAT DROPLET
    // --------------------------------------------------------

    if (oneShot.showRightSweat)
    {
        // Start just outside the upper-right eye corner.
        int16_t rightDropX =
            rightDrawX + rightWidth +
            static_cast<int16_t>(3.0f * renderScaleX + 0.5f) +
            static_cast<int16_t>(
                3.0f * renderScaleX * sweatTravel + 0.5f
            );

        if (rightDropX > canvas.width() - 3)
        {
            rightDropX = canvas.width() - 3;
        }

        const int16_t rightEyeTop =
            eyeCenterY -
            static_cast<int16_t>(
                state.rightEye.eyeHeight *
                renderScaleY * 0.5f + 0.5f
            );
        const int16_t rightDropY =
            rightEyeTop - 5 + sweatOffsetY;

        drawSweatDrop(
            canvas,
            rightDropX,
            rightDropY
        );
    }

    
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
    // Detect the current OLED orientation.
    const bool portrait =
        canvas.width() < 100;

    // Both orientations use the same animation engine.
    // Only the rendering dimensions change.
    drawAnimatedEyes(
        canvas,
        nowMs,
        portrait
    );
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
// AUTOMATIC BLINK CONTROL
// ------------------------------------------------------------

void setAutomaticBlink(bool enabled)
{
    automaticBlinkEnabled = enabled;
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
// EYE GEOMETRY PRESETS
// ------------------------------------------------------------

void setGeometryPreset(GeometryPreset preset)
{
    // Begin with the original geometry.
    float leftScaleX = 1.0f;
    float leftScaleY = 1.0f;

    float rightScaleX = 1.0f;
    float rightScaleY = 1.0f;

    float eyeGap = DEFAULT_EYE_GAP;

    float cornerRadiusScale = 1.0f;

    bool cyclopsMode = false;

    switch (preset)
    {
        // ----------------------------------------------------
        // DEFAULT GEOMETRY
        // ----------------------------------------------------

        case GeometryPreset::NORMAL:
        case GeometryPreset::RESTORE_EYE_PAIR:
            break;

        // ----------------------------------------------------
        // EYE WIDTH
        // ----------------------------------------------------

        case GeometryPreset::WIDE:
            leftScaleX = 1.20f;
            rightScaleX = 1.20f;
            break;

        case GeometryPreset::NARROW:
            leftScaleX = 0.80f;
            rightScaleX = 0.80f;
            break;

        // ----------------------------------------------------
        // EYE HEIGHT
        // ----------------------------------------------------

        case GeometryPreset::TALL:
            leftScaleY = 1.20f;
            rightScaleY = 1.20f;
            break;

        case GeometryPreset::SHORT:
            leftScaleY = 0.80f;
            rightScaleY = 0.80f;
            break;

        // ----------------------------------------------------
        // EYE SPACING
        // ----------------------------------------------------

        case GeometryPreset::CLOSE_SPACING:
            eyeGap = 8.0f;
            break;

        case GeometryPreset::WIDE_SPACING:
            eyeGap = 30.0f;
            break;

        // ----------------------------------------------------
        // CORNER RADIUS
        // ----------------------------------------------------

        case GeometryPreset::SMALL_RADIUS:
            cornerRadiusScale = 0.40f;
            break;

        case GeometryPreset::LARGE_RADIUS:
            cornerRadiusScale = 1.50f;
            break;

        // ----------------------------------------------------
        // INDEPENDENT EYE GEOMETRY
        // ----------------------------------------------------

        case GeometryPreset::INDEPENDENT:

            // Left eye: wider and shorter.
            leftScaleX = 1.20f;
            leftScaleY = 0.85f;

            // Right eye: narrower and taller.
            rightScaleX = 0.85f;
            rightScaleY = 1.20f;

            break;

        // ----------------------------------------------------
        // REVERSE INDEPENDENT EYE GEOMETRY
        // ----------------------------------------------------

        case GeometryPreset::REVERSE_INDEPENDENT:

            // Left eye: narrower and taller.
            leftScaleX = 0.85f;
            leftScaleY = 1.20f;

            // Right eye: wider and shorter.
            rightScaleX = 1.20f;
            rightScaleY = 0.85f;

            break;

        // ----------------------------------------------------
        // CYCLOPS
        // ----------------------------------------------------

        case GeometryPreset::CYCLOPS:

            // Both eyes gradually become the same size.
            // They will overlap perfectly at the display centre.
            leftScaleX = 1.25f;
            leftScaleY = 1.12f;

            rightScaleX = 1.25f;
            rightScaleY = 1.12f;

            // Move both eyes toward the centre.
            cyclopsMode = true;

            break;
    }

    // --------------------------------------------------------
    // STORE REQUESTED GEOMETRY
    // --------------------------------------------------------

    requestedLeftScaleX = leftScaleX;
    requestedLeftScaleY = leftScaleY;

    requestedRightScaleX = rightScaleX;
    requestedRightScaleY = rightScaleY;

    requestedEyeGap = eyeGap;

    requestedCornerRadiusScale = cornerRadiusScale;

    requestedCyclopsMode = cyclopsMode;

    // --------------------------------------------------------
    // UPDATE ANIMATION TARGETS
    // --------------------------------------------------------

    if (state.initialized)
    {
        state.leftEye.targetGeometryScaleX = leftScaleX;
        state.leftEye.targetGeometryScaleY = leftScaleY;

        state.rightEye.targetGeometryScaleX = rightScaleX;
        state.rightEye.targetGeometryScaleY = rightScaleY;

        state.targetEyeGap = eyeGap;

        state.targetCornerRadiusScale =
            cornerRadiusScale;

        // Animate between the normal pair and Cyclops.
        state.targetCyclopsBlend =
            cyclopsMode ? 1.0f : 0.0f;
    }
}

// ------------------------------------------------------------
// CURIOSITY CONTROL
// ------------------------------------------------------------

void setCuriosity(bool enabled)
{
    curiosityEnabled = enabled;
}

// ------------------------------------------------------------
// PLAY ONE-SHOT EYE ANIMATION
// ------------------------------------------------------------

void playOneShot(OneShotAnimation animation)
{
    // Cancel any currently running one-shot animation.
    if (animation == OneShotAnimation::NONE)
    {
        state.activeOneShot = OneShotAnimation::NONE;
        state.oneShotDurationMs = 0;
        return;
    }

    const uint32_t nowMs = millis();

    // Initialise animation state if the renderer has not
    // drawn its first frame yet.
    if (!state.initialized)
    {
        initializeAnimation(nowMs);
    }

    state.activeOneShot = animation;
    state.oneShotStartMs = nowMs;

    switch (animation)
    {
        case OneShotAnimation::FLICKER_HORIZONTAL:
        case OneShotAnimation::FLICKER_VERTICAL:
            state.oneShotDurationMs =
                FLICKER_DURATION_MS;
            break;

        case OneShotAnimation::CONFUSED:
            state.oneShotDurationMs =
                CONFUSED_DURATION_MS;
            break;

        case OneShotAnimation::LAUGH:
            state.oneShotDurationMs =
                LAUGH_DURATION_MS;
            break;

        case OneShotAnimation::SHAKE_HORIZONTAL:
        case OneShotAnimation::SHAKE_VERTICAL:
            state.oneShotDurationMs =
                SHAKE_DURATION_MS;
            break;

        case OneShotAnimation::SWEAT_LEFT:
        case OneShotAnimation::SWEAT_RIGHT:
        case OneShotAnimation::SWEAT_BOTH:
            state.oneShotDurationMs =
                SWEAT_DURATION_MS;
            break;

        case OneShotAnimation::NONE:
        default:
            state.oneShotDurationMs = 0;
            state.activeOneShot = OneShotAnimation::NONE;
            break;
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

    automaticBlinkEnabled = true;

    // Restore default geometry.
    requestedLeftScaleX = 1.0f;
    requestedLeftScaleY = 1.0f;

    requestedRightScaleX = 1.0f;
    requestedRightScaleY = 1.0f;

    requestedEyeGap = DEFAULT_EYE_GAP;

    requestedCornerRadiusScale = 1.0f;

    requestedCyclopsMode = false;

    // Curiosity returns to its normal disabled state.
    curiosityEnabled = false;
}

} // namespace EmotionRenderer

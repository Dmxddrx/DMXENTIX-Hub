#include "display_manager.h"

#include "fonts.h"
#include "mpu6050.h"
#include "i2c_manager.h"
#include "device_config.h"
#include "emotion_renderer.h"

#include <Wire.h>
#include <Adafruit_GFX.h>
#include <Adafruit_SSD1306.h>

#include <stdio.h>
#include <stdarg.h>
#include <string.h>

#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/semphr.h>

namespace DisplayManager {
namespace {

SemaphoreHandle_t statusMutex = nullptr;

constexpr int DISPLAY_NATIVE_WIDTH = 128;
constexpr int DISPLAY_NATIVE_HEIGHT = 64;
constexpr int OLED_RESET = -1;
constexpr uint8_t OLED_PAGE_COUNT = 4;
// Number of manually selectable eye behaviour tests.
constexpr uint8_t EYE_DEMO_STEP_COUNT = 38;


Adafruit_SSD1306 display(
    DISPLAY_NATIVE_WIDTH,
    DISPLAY_NATIVE_HEIGHT,
    &Wire,
    OLED_RESET,
    I2CConfig::OLED_TRANSFER_HZ,
    I2CConfig::BUS_CLOCK_HZ
);

bool displayReady = false;

// ------------------------------------------------------------
// OLED FREERTOS CONFIGURATION
// ------------------------------------------------------------

constexpr uint32_t OLED_TASK_STACK_SIZE = 8192;

constexpr UBaseType_t OLED_TASK_PRIORITY = 2;

constexpr uint32_t OLED_REFRESH_INTERVAL_MS = 50;

TaskHandle_t oledTaskHandle = nullptr;

Status currentStatus{};

// ------------------------------------------------------------
// AUTOMATIC DEVICE POSITION DETECTION
// ------------------------------------------------------------

enum class DevicePosition : uint8_t {
    UNKNOWN,
    POSITION_1,
    POSITION_2,
    POSITION_3,
    POSITION_4
};

// Current confirmed device position.
DevicePosition currentPosition = DevicePosition::UNKNOWN;

// Candidate position used for stability checking.
DevicePosition candidatePosition = DevicePosition::UNKNOWN;

uint32_t candidatePositionSinceMs = 0;

// Position must remain stable for this duration.
constexpr uint32_t POSITION_STABLE_MS = 400;

// Accelerometer raw thresholds.
constexpr int16_t GRAVITY_THRESHOLD = 10000;
constexpr int16_t DIAGONAL_THRESHOLD2 = 1500;
constexpr int16_t DIAGONAL_THRESHOLD = 9000;

uint8_t currentDisplayRotation = 0;

// Private OLED rendering function.
void update(const Status &status);


/*
 * Detect device position using stationary gravity direction.
 *
 * POSITION 1:
 *     Negative Z gravity.
 *
 * POSITION 2:
 *     Positive Z gravity.
 *
 * POSITION 3:
 *     Negative Y gravity.
 *
 * POSITION 4:
 *     Positive X and positive Y together.
 *
 * Gyroscope values are deliberately not used here.
 */
DevicePosition detectDevicePosition(const MPU6050::Data &imu)
{
    // Position 4: both X and Y are strongly positive.
    // Check this first because it is a diagonal orientation.
    if (imu.axRaw > DIAGONAL_THRESHOLD2 &&
        imu.ayRaw > DIAGONAL_THRESHOLD)
    {
        return DevicePosition::POSITION_4;
    }

    // Position 1: Z axis points in the negative direction.
    if (imu.azRaw < -GRAVITY_THRESHOLD)
    {
        return DevicePosition::POSITION_1;
    }

    // Position 2: Z axis points in the positive direction.
    if (imu.azRaw > GRAVITY_THRESHOLD)
    {
        return DevicePosition::POSITION_2;
    }

    // Position 3: Y axis points in the negative direction.
    if (imu.ayRaw < -GRAVITY_THRESHOLD)
    {
        return DevicePosition::POSITION_3;
    }

    return DevicePosition::UNKNOWN;
}


/*
 * Convert the detected physical position into OLED rotation.
 *
 * Position 1: 90 degrees anticlockwise.
 * Position 2: 90 degrees clockwise.
 * Position 3: No rotation.
 * Position 4: 180 degrees.
 */
uint8_t getRotationForPosition(DevicePosition position)
{
    switch (position)
    {
        case DevicePosition::POSITION_1:
            return 3;

        case DevicePosition::POSITION_2:
            return 1;

        case DevicePosition::POSITION_3:
            return 0;

        case DevicePosition::POSITION_4:
            return 2;

        default:
            return currentDisplayRotation;
    }
}

// ------------------------------------------------------------
// OLED FREERTOS TASK
// ------------------------------------------------------------

void oledTask(void *parameter)
{
    Status snapshot{};

    for (;;) {

        // Wait for a display notification or timed refresh.
        //
        // This blocks the task instead of continuously
        // checking for changes.

        ulTaskNotifyTake(
            pdTRUE,
            pdMS_TO_TICKS(OLED_REFRESH_INTERVAL_MS)
        );


        // ----------------------------------------------------
        // GET THREAD-SAFE STATUS SNAPSHOT
        // ----------------------------------------------------

        if (xSemaphoreTake(
                statusMutex,
                pdMS_TO_TICKS(20)) != pdTRUE) {

            continue;
        }

        snapshot = currentStatus;

        xSemaphoreGive(statusMutex);


        // ----------------------------------------------------
        // RENDER DISPLAY
        // ----------------------------------------------------

        update(snapshot);
    }
}

void flushDisplay()
{
    if (!I2CManager::lock(100)) {
        return;
    }

    display.display();

    I2CManager::unlock();
}

/*
 * Update OLED orientation only after a stable position is detected.
 *
 * During movement, unknown or changing accelerometer readings do not
 * immediately rotate the display.
 */
void updateAutomaticRotation()
{
    if (!MPU6050::isReady()) {
        return;
    }

    MPU6050::Data imu;

    if (!MPU6050::getData(imu) || !imu.valid) {
        return;
    }

    const uint32_t now = millis();

    const DevicePosition detected =
        detectDevicePosition(imu);

    // Device is between known positions.
    // Keep the previous display orientation.
    if (detected == DevicePosition::UNKNOWN)
    {
        candidatePosition = DevicePosition::UNKNOWN;
        return;
    }

    // A new candidate position has appeared.
    if (detected != candidatePosition)
    {
        candidatePosition = detected;
        candidatePositionSinceMs = now;
        return;
    }

    // Wait until the candidate position remains stable.
    if ((uint32_t)(now - candidatePositionSinceMs) <
        POSITION_STABLE_MS)
    {
        return;
    }

    // Position is confirmed.
    if (detected != currentPosition)
    {
        currentPosition = detected;

        const uint8_t newRotation =
            getRotationForPosition(currentPosition);

        // Apply rotation only when it actually changes.
        if (newRotation != currentDisplayRotation)
        {
            currentDisplayRotation = newRotation;

            display.setRotation(currentDisplayRotation);

            // Clear the old framebuffer after changing orientation.
            display.clearDisplay();
        }
    }
}


// Width of the built-in Adafruit GFX font.
int16_t textWidth(const char *text, uint8_t size)
{
    if (text == nullptr) {
        return 0;
    }

    return (int16_t)strlen(text) * 6 * size;
}

void drawCenteredText(
    const char *text,
    int16_t y,
    uint8_t size
)
{
    if (text == nullptr) {
        return;
    }

    int16_t width = textWidth(text, size);
    int16_t x = (display.width() - width) / 2;

    if (x < 0) {
        x = 0;
    }

    display.setTextSize(size);
    display.setCursor(x, y);
    display.print(text);
}


// ------------------------------------------------------------
// CUSTOM BITMAP FONT RENDERING
// ------------------------------------------------------------

void drawBitmapChar(
    char ch,
    int16_t x,
    int16_t y,
    const FontDef_t *font
)
{
    if (font == nullptr || font->data == nullptr) return;
    if (ch < 32 || ch > 126) return;

    const uint16_t offset =
        (uint16_t)(ch - 32) * font->FontHeight;

    for (uint8_t row = 0; row < font->FontHeight; ++row)
    {
        const uint16_t rowBits = font->data[offset + row];

        for (uint8_t col = 0;
             col < font->FontWidth && col < 16;
             ++col)
        {
            if (rowBits & (uint16_t)(0x8000U >> col))
            {
                const int16_t px = x + col;
                const int16_t py = y + row;

                if (px >= 0 && px < display.width() &&
                    py >= 0 && py < display.height())
                {
                    display.drawPixel(
                        px,
                        py,
                        SSD1306_WHITE
                    );
                }
            }
        }
    }
}


void drawBitmapString(
    const char *text,
    int16_t x,
    int16_t y,
    const FontDef_t *font
)
{
    if (text == nullptr || font == nullptr) return;

    while (*text)
    {
        drawBitmapChar(*text, x, y, font);

        x += font->FontWidth;
        ++text;
    }
}


// ------------------------------------------------------------
// DISPLAY ANIMATION NAME
// ------------------------------------------------------------

const char *getDisplayAnimationName(
    const Status &status,
    bool portrait
)
{
    const char *mode =
        status.modeName[0] != '\0'
        ? status.modeName
        : "UNKNOWN";

    if (!portrait)
    {
        return mode;
    }

    // Shorten long animation names for the 64-pixel width.

    if (strcmp(mode, "SAND_CLOCK") == 0)
        return "SAND CLOCK";

    if (strcmp(mode, "SAND_GRAVITY") == 0)
        return "GRAVITY";

    return mode;
}

// ============================================================
// EYE DEMO SEQUENCE
// ============================================================

const char *getEyeDemoStepName(uint8_t step)
{
    static const char * const names[EYE_DEMO_STEP_COUNT] = {
        "NEUTRAL",                   // 0
        "HAPPY",                     // 1
        "TIRED",                     // 2
        "ANGRY",                     // 3
        "SURPRISED",                 // 4
        "LEFT_HAPPY_RIGHT_ANGRY",    // 5
        "LEFT_ANGRY_RIGHT_HAPPY",    // 6
        "CENTER",                    // 7
        "UP",                        // 8
        "UP_RIGHT",                  // 9
        "RIGHT",                     // 10
        "DOWN_RIGHT",                // 11
        "DOWN",                      // 12
        "DOWN_LEFT",                 // 13
        "LEFT",                      // 14
        "UP_LEFT",                   // 15
        "BLINK_BOTH",                // 16
        "WINK_LEFT",                 // 17
        "WINK_RIGHT",                // 18
        "CLOSE_LEFT",                // 19
        "OPEN_LEFT",                 // 20
        "CLOSE_RIGHT",               // 21
        "OPEN_RIGHT",                // 22
        "CLOSE_BOTH",                // 23
        "OPEN_BOTH",                 // 24
        "AUTO_IDLE_GAZE",            // 25

        // --------------------------------------------------------
        // EYE GEOMETRY TESTS
        // --------------------------------------------------------

        "DEFAULT_GEOMETRY",          // 26
        "WIDE_EYES",                  // 27
        "NARROW_EYES",                // 28
        "TALL_EYES",                  // 29
        "SHORT_EYES",                 // 30
        "CLOSE_SPACING",              // 31
        "WIDE_SPACING",               // 32
        "SMALL_RADIUS",               // 33
        "LARGE_RADIUS",               // 34
        "INDEPENDENT_GEOMETRY",       // 35
        "CYCLOPS",                    // 36
        "RESTORE_EYE_PAIR"            // 37
    };

    return step < EYE_DEMO_STEP_COUNT
        ? names[step]
        : "UNKNOWN";
}


// Apply one eye-demo command from the OLED task.
// All EmotionRenderer calls remain in the OLED task.
void applyEyeDemoStep(uint8_t step)
{
    using EmotionRenderer::Eye;
    using EmotionRenderer::GazeDirection;
    using EmotionRenderer::Mood;
    using EmotionRenderer::GeometryPreset;

    // Automatic blinking is reserved for the automatic
    // behaviour test. Manual blink and wink commands still work.
    EmotionRenderer::setAutomaticBlink(step == 25);

    // Every test starts with predictable default geometry.
    // Geometry presets below override this when necessary.
    EmotionRenderer::setGeometryPreset(
        GeometryPreset::NORMAL
    );

    // Steps 19-24 form consecutive close/open tests.
    // Preserve the previous eye state during the opening steps.
    const bool openingClosingTest =
        step >= 19 && step <= 24;

    const bool startNewOpeningClosingTest =
        step == 19 ||
        step == 21 ||
        step == 23;

    // Restore a predictable starting state for other tests.
    if (!openingClosingTest ||
        startNewOpeningClosingTest)
    {
        EmotionRenderer::setIdleGaze(false);

        EmotionRenderer::setMood(Mood::NEUTRAL);

        EmotionRenderer::setEyeOpen(
            Eye::BOTH,
            true
        );

        EmotionRenderer::setGaze(
            GazeDirection::CENTER
        );
    }

    switch (step)
    {
        // ----------------------------------------------------
        // BASIC EXPRESSIONS
        // ----------------------------------------------------

        case 0:
            // Neutral rounded-square eyes.
            break;

        case 1:
            EmotionRenderer::setMood(Mood::HAPPY);
            break;

        case 2:
            EmotionRenderer::setMood(Mood::TIRED);
            break;

        case 3:
            EmotionRenderer::setMood(Mood::ANGRY);
            break;

        case 4:
            EmotionRenderer::setMood(Mood::SURPRISED);
            break;

        // ----------------------------------------------------
        // INDEPENDENT EYE EXPRESSIONS
        // ----------------------------------------------------

        case 5:
            // Left happy, right angry.
            EmotionRenderer::setEyeMood(
                Eye::LEFT,
                Mood::HAPPY
            );

            EmotionRenderer::setEyeMood(
                Eye::RIGHT,
                Mood::ANGRY
            );
            break;

        case 6:
            // Left angry, right happy.
            EmotionRenderer::setEyeMood(
                Eye::LEFT,
                Mood::ANGRY
            );

            EmotionRenderer::setEyeMood(
                Eye::RIGHT,
                Mood::HAPPY
            );
            break;

        // ----------------------------------------------------
        // NINE GAZE DIRECTIONS
        // ----------------------------------------------------

        case 7:
            EmotionRenderer::setGaze(
                GazeDirection::CENTER
            );
            break;

        case 8:
            EmotionRenderer::setGaze(
                GazeDirection::UP
            );
            break;

        case 9:
            EmotionRenderer::setGaze(
                GazeDirection::UP_RIGHT
            );
            break;

        case 10:
            EmotionRenderer::setGaze(
                GazeDirection::RIGHT
            );
            break;

        case 11:
            EmotionRenderer::setGaze(
                GazeDirection::DOWN_RIGHT
            );
            break;

        case 12:
            EmotionRenderer::setGaze(
                GazeDirection::DOWN
            );
            break;

        case 13:
            EmotionRenderer::setGaze(
                GazeDirection::DOWN_LEFT
            );
            break;

        case 14:
            EmotionRenderer::setGaze(
                GazeDirection::LEFT
            );
            break;

        case 15:
            EmotionRenderer::setGaze(
                GazeDirection::UP_LEFT
            );
            break;

        // ----------------------------------------------------
        // BLINKING AND WINKING
        // ----------------------------------------------------

        case 16:
            EmotionRenderer::blink(Eye::BOTH);
            break;

        case 17:
            EmotionRenderer::wink(Eye::LEFT);
            break;

        case 18:
            EmotionRenderer::wink(Eye::RIGHT);
            break;

        // ----------------------------------------------------
        // INDEPENDENT EYE OPENING / CLOSING
        // ----------------------------------------------------

        case 19:
            EmotionRenderer::setEyeOpen(
                Eye::LEFT,
                false
            );
            break;

        case 20:
            EmotionRenderer::setEyeOpen(
                Eye::LEFT,
                true
            );
            break;

        case 21:
            EmotionRenderer::setEyeOpen(
                Eye::RIGHT,
                false
            );
            break;

        case 22:
            EmotionRenderer::setEyeOpen(
                Eye::RIGHT,
                true
            );
            break;

        case 23:
            EmotionRenderer::setEyeOpen(
                Eye::BOTH,
                false
            );
            break;

        case 24:
            EmotionRenderer::setEyeOpen(
                Eye::BOTH,
                true
            );
            break;

        // ----------------------------------------------------
        // AUTOMATIC BEHAVIOUR
        // ----------------------------------------------------

        case 25:
            EmotionRenderer::setIdleGaze(true);
            break;

        // ----------------------------------------------------
        // EYE GEOMETRY TESTS
        // ----------------------------------------------------

        case 26:
            // Restore original eye dimensions and layout.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::NORMAL
            );
            break;

        case 27:
            // Increase both eyes' width.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::WIDE
            );
            break;

        case 28:
            // Reduce both eyes' width.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::NARROW
            );
            break;

        case 29:
            // Increase both eyes' height.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::TALL
            );
            break;

        case 30:
            // Reduce both eyes' height.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::SHORT
            );
            break;

        case 31:
            // Move both eyes closer together.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::CLOSE_SPACING
            );
            break;

        case 32:
            // Move both eyes farther apart.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::WIDE_SPACING
            );
            break;

        case 33:
            // Reduce corner rounding.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::SMALL_RADIUS
            );
            break;

        case 34:
            // Increase corner rounding.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::LARGE_RADIUS
            );
            break;

        case 35:
            // Different dimensions for each eye.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::INDEPENDENT
            );
            break;

        case 36:
            // Replace the normal pair with one central eye.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::CYCLOPS
            );
            break;

        case 37:
            // Restore the original two-eye arrangement.
            EmotionRenderer::setGeometryPreset(
                GeometryPreset::RESTORE_EYE_PAIR
            );
            break;

        default:
            break;
    }

    Serial.printf(
        "[EYES TEST] Step %u/%u: %s\n",
        static_cast<unsigned int>(step + 1),
        static_cast<unsigned int>(EYE_DEMO_STEP_COUNT),
        getEyeDemoStepName(step)
    );
}


// ============================================================
// PAGE 0 — ROBOT
// ============================================================

void drawRobotPage(const Status &status)
{
    static bool started = false;
    static bool demoWasActive = false;

    static uint8_t previousDemoStep = 0xFF;
    static uint32_t previousDemoSession = 0;

    const uint32_t now = millis();

    if (!started)
    {
        EmotionRenderer::reset();
        started = true;
    }

    if (status.eyeDemoActive)
    {
        // Execute a test only when the step or demo session changes.
        if (!demoWasActive ||
            previousDemoStep != status.eyeDemoStep ||
            previousDemoSession != status.eyeDemoSession)
        {
            applyEyeDemoStep(status.eyeDemoStep);

            demoWasActive = true;
            previousDemoStep = status.eyeDemoStep;
            previousDemoSession = status.eyeDemoSession;
        }
    }
    else if (demoWasActive)
    {
        // Leaving the test restores normal automatic eye behaviour.
        EmotionRenderer::reset();

        demoWasActive = false;
        previousDemoStep = 0xFF;

        Serial.println(
            "[EYES TEST] Automatic eye behaviour restored"
        );
    }

    EmotionRenderer::drawRobotEyes(display, now);
}



// ============================================================
// PAGE 1 — NETWORK
// ============================================================

// ------------------------------------------------------------
// LANDSCAPE NETWORK PAGE (128 x 64)
// ------------------------------------------------------------

void drawNetworkRow(
    const char *label,
    const char *value,
    int16_t y
)
{
    display.setCursor(0, y);
    display.print(label);

    display.setCursor(34, y);
    display.print(value);
}


void drawNetworkPage(const Status &status)
{
    display.setTextSize(1);

    const char *wifi =
        status.wifiConnected ? "CONNECTED" : "OFFLINE";

    const char *ntp =
        status.ntpSynced ? "SYNCED" : "WAITING";

    const char *ip =
        (status.ipAddress[0] != '\0' &&
         strcmp(status.ipAddress, "-") != 0)
        ? status.ipAddress
        : "NO IP";

    const char *ble =
        status.bleConnected ? "CONNECTED" : "WAITING";

    drawNetworkRow("WIFI", wifi, 0);
    drawNetworkRow("NTP", ntp, 16);

    // IP needs more horizontal space.
    display.setCursor(0, 26);
    display.print("IP");

    display.setCursor(18, 26);
    display.print(ip);

    drawNetworkRow("BLE", ble, 36);
}


// ------------------------------------------------------------
// PORTRAIT NETWORK PAGE (64 x 128)
// ------------------------------------------------------------

void drawPortraitIpAddress(const char *ipAddress)
{
    if (ipAddress == nullptr ||
        ipAddress[0] == '\0' ||
        strcmp(ipAddress, "-") == 0)
    {
        drawCenteredText("NO IP", 68, 1);
        return;
    }

    // Split a normal IPv4 address into two readable lines.

    const char *firstDot = strchr(ipAddress, '.');

    const char *secondDot =
        firstDot != nullptr
        ? strchr(firstDot + 1, '.')
        : nullptr;

    if (secondDot == nullptr)
    {
        drawCenteredText(ipAddress, 68, 1);
        return;
    }

    char firstPart[16] = {};
    char secondPart[16] = {};

    size_t firstLength =
        (size_t)(secondDot - ipAddress) + 1;

    if (firstLength >= sizeof(firstPart))
    {
        firstLength = sizeof(firstPart) - 1;
    }

    memcpy(firstPart, ipAddress, firstLength);
    firstPart[firstLength] = '\0';

    snprintf(
        secondPart,
        sizeof(secondPart),
        "%s",
        secondDot + 1
    );

    drawCenteredText(firstPart, 68, 1);
    drawCenteredText(secondPart, 79, 1);
}


void drawPortraitNetworkPage(const Status &status)
{
    display.setTextSize(1);

    // Wi-Fi

    drawCenteredText("WIFI", 2, 1);

    drawCenteredText(
        status.wifiConnected ? "CONNECTED" : "OFFLINE",
        12,
        1
    );

    // NTP

    drawCenteredText("NTP", 28, 1);

    drawCenteredText(
        status.ntpSynced ? "SYNCED" : "WAITING",
        38,
        1
    );

    // IP address

    drawCenteredText("IP", 55, 1);

    drawPortraitIpAddress(status.ipAddress);

    // Bluetooth LE

    drawCenteredText("BLE", 99, 1);

    drawCenteredText(
        status.bleConnected ? "CONNECTED" : "WAITING",
        109,
        1
    );
}



// ============================================================
// PAGE 2 — RAW MPU6050 DATA
// ============================================================

// ------------------------------------------------------------
// GET VALID MPU6050 DATA
// ------------------------------------------------------------


bool getImuSnapshot(
    const Status &status,
    MPU6050::Data &imu,
    const char *&statusText
)
{
    if (!status.mpuReady)
    {
        statusText = "NOT READY";
        return false;
    }

    if (!MPU6050::getData(imu) || !imu.valid)
    {
        statusText = "NO DATA";
        return false;
    }

    statusText = "READY";
    return true;
}



// ------------------------------------------------------------
// DRAW ONE RAW AXIS VALUE
// ------------------------------------------------------------

void drawRawImuValue(
    const char *axis,
    int16_t value,
    int16_t x,
    int16_t y
)
{
    char line[16];

    snprintf(
        line,
        sizeof(line),
        "%s:%+6d",
        axis,
        (int)value
    );

    display.setCursor(x, y);
    display.print(line);
}


// ------------------------------------------------------------
// LANDSCAPE IMU PAGE (128 x 64)
// ------------------------------------------------------------
//
// Left column:  Accelerometer X, Y, Z
// Right column: Gyroscope X, Y, Z
//
// ------------------------------------------------------------

void drawLandscapeImuPage(const Status &status)
{
    display.setTextSize(1);

    MPU6050::Data imu{};
    const char *mpuStatus = nullptr;

    const bool dataReady =
        getImuSnapshot(status, imu, mpuStatus);

    // MPU status at the top.
    char statusLine[24];

    snprintf(
        statusLine,
        sizeof(statusLine),
        "MPU6050: %s",
        mpuStatus
    );

    drawCenteredText(statusLine, 1, 1);

    if (!dataReady)
    {
        return;
    }

    // ACCELEROMETER — LEFT COLUMN
    drawRawImuValue("AX", imu.axRaw, 2, 17);
    drawRawImuValue("AY", imu.ayRaw, 2, 33);
    drawRawImuValue("AZ", imu.azRaw, 2, 49);

    // GYROSCOPE — RIGHT COLUMN
    drawRawImuValue("GX", imu.gxRaw, 66, 17);
    drawRawImuValue("GY", imu.gyRaw, 66, 33);
    drawRawImuValue("GZ", imu.gzRaw, 66, 49);
}


// ------------------------------------------------------------
// PORTRAIT IMU PAGE (64 x 128)
// ------------------------------------------------------------
//
// Display all six raw values vertically.
//
// ------------------------------------------------------------

void drawPortraitImuPage(const Status &status)
{
    display.setTextSize(1);

    MPU6050::Data imu{};
    const char *mpuStatus = nullptr;

    const bool dataReady =
        getImuSnapshot(status, imu, mpuStatus);

    // MPU identification and status.
    drawCenteredText("MPU6050", 1, 1);
    drawCenteredText(mpuStatus, 11, 1);

    if (!dataReady)
    {
        return;
    }

    // ACCELEROMETER
    drawRawImuValue("AX", imu.axRaw, 5, 28);
    drawRawImuValue("AY", imu.ayRaw, 5, 44);
    drawRawImuValue("AZ", imu.azRaw, 5, 60);

    // GYROSCOPE
    drawRawImuValue("GX", imu.gxRaw, 5, 76);
    drawRawImuValue("GY", imu.gyRaw, 5, 92);
    drawRawImuValue("GZ", imu.gzRaw, 5, 108);
}



// ============================================================
// PAGE 3 — CLOCK
// ============================================================

// ------------------------------------------------------------
// LANDSCAPE CLOCK PAGE (128 x 64)
// ------------------------------------------------------------

void drawClockPage(const Status &status)
{
    char dateText[24];
    char clockText[6];
    char secondsText[3];

    // Date at the top.

    if (status.timeValid)
    {
        strftime(
            dateText,
            sizeof(dateText),
            "%a %d %b %Y",
            &status.localTime
        );

        snprintf(
            clockText,
            sizeof(clockText),
            "%02d:%02d",
            status.localTime.tm_hour,
            status.localTime.tm_min
        );

        snprintf(
            secondsText,
            sizeof(secondsText),
            "%02d",
            status.localTime.tm_sec
        );
    }
    else
    {
        snprintf(dateText, sizeof(dateText), "WAITING FOR NTP");
        snprintf(clockText, sizeof(clockText), "--:--");
        snprintf(secondsText, sizeof(secondsText), "--");
    }

    drawCenteredText(dateText, 2, 1);

    // Large HH:MM clock.

    const int16_t clockWidth =
        5 * Font_16x26.FontWidth;

    const int16_t clockX =
        (display.width() - clockWidth) / 2;

    drawBitmapString(
        clockText,
        clockX,
        18,
        &Font_16x26
    );

    // Seconds on the right side.

    const int16_t secondsX =
        display.width() -
        (2 * Font_7x10.FontWidth) - 4;

    drawBitmapString(
        secondsText,
        secondsX,
        28,
        &Font_7x10
    );

    // Current MAX7219 animation at the bottom.

    drawCenteredText(
        getDisplayAnimationName(status, false),
        55,
        1
    );
}


// ------------------------------------------------------------
// PORTRAIT CLOCK PAGE (64 x 128)
// ------------------------------------------------------------

void drawPortraitClockPage(const Status &status)
{
    char dateText[20];
    char yearText[12];

    char hourText[3];
    char minuteText[3];
    char secondsText[3];

    // --------------------------------------------------------
    // DATE
    // --------------------------------------------------------

    if (status.timeValid)
    {
        strftime(
            dateText,
            sizeof(dateText),
            "%a %d %b",
            &status.localTime
        );

        snprintf(
            yearText,
            sizeof(yearText),
            "%04d",
            status.localTime.tm_year + 1900
        );

        snprintf(
            hourText,
            sizeof(hourText),
            "%02d",
            status.localTime.tm_hour
        );

        snprintf(
            minuteText,
            sizeof(minuteText),
            "%02d",
            status.localTime.tm_min
        );

        snprintf(
            secondsText,
            sizeof(secondsText),
            "%02d",
            status.localTime.tm_sec
        );
    }
    else
    {
        snprintf(dateText, sizeof(dateText), "NTP WAIT");
        snprintf(yearText, sizeof(yearText), "----");

        snprintf(hourText, sizeof(hourText), "--");
        snprintf(minuteText, sizeof(minuteText), "--");
        snprintf(secondsText, sizeof(secondsText), "--");
    }

    // Date and year.
    drawCenteredText(dateText, 2, 1);
    drawCenteredText(yearText, 12, 1);


    // --------------------------------------------------------
    // HOURS — LARGE FONT
    // --------------------------------------------------------

    const int16_t digitWidth =
        2 * Font_16x26.FontWidth;

    const int16_t clockX =
        (display.width() - digitWidth) / 2;

    drawBitmapString(
        hourText,
        clockX,
        30,
        &Font_16x26
    );


    // --------------------------------------------------------
    // MINUTES — LARGE FONT
    // --------------------------------------------------------

    drawBitmapString(
        minuteText,
        clockX,
        57,
        &Font_16x26
    );


    // --------------------------------------------------------
    // SECONDS — NORMAL FONT
    // --------------------------------------------------------

    drawCenteredText(
        secondsText,
        86,
        1
    );


    // --------------------------------------------------------
    // CURRENT ANIMATION
    // --------------------------------------------------------

    drawCenteredText(
        getDisplayAnimationName(status, true),
        116,
        1
    );
}


} // namespace

// ------------------------------------------------------------
// START OLED TASK
// ------------------------------------------------------------

bool startTask()
{
    if (!displayReady ||
        oledTaskHandle != nullptr ||
        statusMutex == nullptr)
    {
        return false;
    }

    BaseType_t result = xTaskCreatePinnedToCore(
        oledTask,
        "OLED",
        OLED_TASK_STACK_SIZE,
        nullptr,
        OLED_TASK_PRIORITY,
        &oledTaskHandle,
        1
    );

    if (result != pdPASS) {
        oledTaskHandle = nullptr;
        return false;
    }

    return true;
}


// ------------------------------------------------------------
// UPDATE DISPLAY STATUS
// ------------------------------------------------------------
void notifyDisplayUpdate()
{
    if (oledTaskHandle != nullptr) {
        xTaskNotifyGive(oledTaskHandle);
    }
}

void setNetworkStatus(
    bool wifiConnected,
    bool ntpSynced,
    const char *ipAddress
)
{
    if (xSemaphoreTake(statusMutex, portMAX_DELAY) != pdTRUE) {
        return;
    }

    currentStatus.wifiConnected = wifiConnected;
    currentStatus.ntpSynced = ntpSynced;

    snprintf(
        currentStatus.ipAddress,
        sizeof(currentStatus.ipAddress),
        "%s",
        ipAddress != nullptr ? ipAddress : "-"
    );

    xSemaphoreGive(statusMutex);

    notifyDisplayUpdate();
}

void setPowerStatus(
    bool externalPower,
    int batteryPercent
)
{
    if (xSemaphoreTake(statusMutex, portMAX_DELAY) != pdTRUE) {
        return;
    }

    currentStatus.externalPowerPresent = externalPower;
    currentStatus.batteryPercent = batteryPercent;

    xSemaphoreGive(statusMutex);

    notifyDisplayUpdate();
}

void setClockStatus(
    const struct tm &time,
    bool valid
)
{
    if (xSemaphoreTake(statusMutex, portMAX_DELAY) != pdTRUE) {
        return;
    }

    currentStatus.localTime = time;
    currentStatus.timeValid = valid;

    xSemaphoreGive(statusMutex);

    notifyDisplayUpdate();
}

void setMPUStatus(
    bool ready,
    float pitchDeg,
    float rollDeg
)
{
    if (xSemaphoreTake(statusMutex, portMAX_DELAY) != pdTRUE) {
        return;
    }

    currentStatus.mpuReady = ready;
    currentStatus.pitchDeg = pitchDeg;
    currentStatus.rollDeg = rollDeg;

    xSemaphoreGive(statusMutex);

    notifyDisplayUpdate();
}

void setApplicationStatus(
    uint8_t page,
    const char *modeName
)
{
    if (xSemaphoreTake(statusMutex, portMAX_DELAY) != pdTRUE) {
        return;
    }

    currentStatus.page = page;

    snprintf(
        currentStatus.modeName,
        sizeof(currentStatus.modeName),
        "%s",
        modeName != nullptr ? modeName : "EMOTIX360"
    );

    xSemaphoreGive(statusMutex);

    notifyDisplayUpdate();
}

void setBluetoothStatus(bool connected)
{
    if (statusMutex == nullptr ||
        xSemaphoreTake(statusMutex, pdMS_TO_TICKS(20)) != pdTRUE)
    {
        return;
    }

    currentStatus.bleConnected = connected;

    xSemaphoreGive(statusMutex);

    notifyDisplayUpdate();
}


void setAnimationMode(const char *modeName)
{
    if (statusMutex == nullptr ||
        xSemaphoreTake(statusMutex, pdMS_TO_TICKS(20)) != pdTRUE)
    {
        return;
    }

    snprintf(
        currentStatus.modeName,
        sizeof(currentStatus.modeName),
        "%s",
        modeName != nullptr ? modeName : "UNKNOWN"
    );

    xSemaphoreGive(statusMutex);

    notifyDisplayUpdate();
}

// ------------------------------------------------------------
// EYE DEMO BUTTON CONTROL
// ------------------------------------------------------------

bool nextEyeDemoStep()
{
    if (!displayReady || statusMutex == nullptr)
    {
        return false;
    }

    if (xSemaphoreTake(
            statusMutex,
            pdMS_TO_TICKS(20)) != pdTRUE)
    {
        return false;
    }

    // The first short press starts a new demo session.
    if (!currentStatus.eyeDemoActive)
    {
        currentStatus.eyeDemoActive = true;
        currentStatus.eyeDemoStep = 0;

        ++currentStatus.eyeDemoSession;
    }
    else
    {
        currentStatus.eyeDemoStep =
            (currentStatus.eyeDemoStep + 1) %
            EYE_DEMO_STEP_COUNT;
    }

    // Always show the Robot page during the demo.
    currentStatus.page = 0;

    xSemaphoreGive(statusMutex);

    notifyDisplayUpdate();

    return true;
}


bool exitEyeDemo()
{
    if (!displayReady || statusMutex == nullptr)
    {
        return false;
    }

    if (xSemaphoreTake(
            statusMutex,
            pdMS_TO_TICKS(20)) != pdTRUE)
    {
        return false;
    }

    if (!currentStatus.eyeDemoActive)
    {
        xSemaphoreGive(statusMutex);
        return false;
    }

    currentStatus.eyeDemoActive = false;
    currentStatus.eyeDemoStep = 0;
    currentStatus.page = 0;

    xSemaphoreGive(statusMutex);

    notifyDisplayUpdate();

    return true;
}

// ------------------------------------------------------------
// OLED PAGE NAVIGATION
// ------------------------------------------------------------

bool nextPage()
{
    if (statusMutex == nullptr) {
        return false;
    }

    if (xSemaphoreTake(
            statusMutex,
            pdMS_TO_TICKS(20)) != pdTRUE) {
        return false;
    }

    // Advance to the next page and wrap back to Home.
    currentStatus.page =
        (currentStatus.page + 1) % OLED_PAGE_COUNT;

    // Stop the eye demo when navigating to another OLED page.
    currentStatus.eyeDemoActive = false;
    currentStatus.eyeDemoStep = 0;

    xSemaphoreGive(statusMutex);

    notifyDisplayUpdate();

    return true;
}

// ------------------------------------------------------------
// REQUEST DISPLAY REFRESH
// ------------------------------------------------------------

void requestUpdate()
{
    if (oledTaskHandle != nullptr) {
        xTaskNotifyGive(oledTaskHandle);
    }
}

// ------------------------------------------------------------
// INITIALIZE SSD1306 OLED
// ------------------------------------------------------------

bool begin()
{
    displayReady = false;

    if (!I2CManager::isReady()) {
        return false;
    }

    // Create the status synchronization object.
    if (statusMutex == nullptr)
    {
        statusMutex = xSemaphoreCreateMutex();
    }

    if (statusMutex == nullptr)
    {
        return false;
    }

    // Check OLED acknowledgement.
    if (!I2CManager::isDeviceConnected(
            DeviceConfig::OLED_ADDRESS)) {
        return false;
    }

    // Initialize OLED using the shared Wire instance.
    if (!I2CManager::lock(100)) {
        return false;
    }

    const bool initialized = display.begin(
        SSD1306_SWITCHCAPVCC,
        DeviceConfig::OLED_ADDRESS,
        true,
        false
    );

    I2CManager::unlock();

    if (!initialized) {
        return false;
    }

    displayReady = true;

    display.setRotation(0);

    currentDisplayRotation = 0;

    currentPosition = DevicePosition::UNKNOWN;
    candidatePosition = DevicePosition::UNKNOWN;

    candidatePositionSinceMs = 0;

    display.clearDisplay();

    display.setTextColor(SSD1306_WHITE);
    display.setTextWrap(false);

    flushDisplay();

    return true;
}


bool isReady()
{
    return displayReady;
}


void showStartupMessage(const char *message)
{
    if (!displayReady || message == nullptr || message[0] == '\0') {
        return;
    }

    display.setTextWrap(false);
    display.setTextColor(SSD1306_WHITE);
    display.setTextSize(1);

    const int16_t messageWidth = textWidth(message, 1);

    // Scroll once from the right edge to beyond the left edge.
    for (int16_t x = display.width();
         x >= -messageWidth;
         x -= 3) {

        display.clearDisplay();
        display.setTextSize(1);
        display.setCursor(x, (display.height() - 8) / 2);
        display.print(message);
        flushDisplay();

        delay(8);
    }

    display.clearDisplay();
    flushDisplay();

}

void logMessage(const char *format, ...)
{
    if (format == nullptr)
    {
        return;
    }

    char message[96];

    va_list args;

    va_start(args, format);
    vsnprintf(message, sizeof(message), format, args);
    va_end(args);

    // Diagnostic messages go to Serial, never the OLED.
    Serial.println(message);
}


namespace {

void update(const Status &status)
{
    if (!displayReady)
    {
        return;
    }

    // Update physical orientation before rendering.
    updateAutomaticRotation();

    // Start with a clean framebuffer.
    display.clearDisplay();

    display.setTextColor(SSD1306_WHITE);
    display.setTextWrap(false);
    display.setTextSize(1);

    const bool portrait =
        display.height() > display.width();

    // --------------------------------------------------------
    // SELECT ONE COMPLETE PAGE
    // --------------------------------------------------------

    switch (status.page)
    {
        // ----------------------------------------------------
        // PAGE 0 — ROBOT
        // ----------------------------------------------------

        case 0:
        {
            drawRobotPage(status);
            break;
        }

        // ----------------------------------------------------
        // PAGE 1 — NETWORK
        // ----------------------------------------------------

        case 1:
        {
            if (portrait)
            {
                drawPortraitNetworkPage(status);
            }
            else
            {
                drawNetworkPage(status);
            }

            break;
        }

        // ----------------------------------------------------
        // PAGE 2 — IMU
        // ----------------------------------------------------
        case 2:
        {
            if (portrait)
            {
                drawPortraitImuPage(status);
            }
            else
            {
                drawLandscapeImuPage(status);
            }

            break;
        }

        // ----------------------------------------------------
        // PAGE 3 — CLOCK
        // ----------------------------------------------------

        case 3:
        {
            if (portrait)
            {
                drawPortraitClockPage(status);
            }
            else
            {
                drawClockPage(status);
            }

            break;
        }

        // Unknown page returns to the robot face.

        default:
        {
            drawRobotPage(status);
            break;
        }
    }

    // No shared header.
    // No shared footer.
    // No log line.

    flushDisplay();
}

} // namespace update


} // namespace DisplayManager
#include "emotion_renderer.h"

namespace EmotionRenderer {

void drawNeutralEyes(
    Adafruit_GFX &canvas,
    uint16_t color
)
{
    const int16_t screenWidth = canvas.width();
    const int16_t screenHeight = canvas.height();

    if (screenWidth <= 0 || screenHeight <= 0) {
        return;
    }

    // --------------------------------------------------------
    // EYE DIMENSIONS
    // --------------------------------------------------------

    const bool landscape = screenWidth >= 100;

    const int16_t eyeWidth =
        landscape ? 30 : 18;

    const int16_t eyeHeight =
        landscape ? 36 : 28;

    const int16_t eyeGap =
        landscape ? 32 : 16;

    const int16_t cornerRadius =
        eyeWidth / 3;

    // --------------------------------------------------------
    // CENTER THE TWO EYES
    // --------------------------------------------------------

    const int16_t totalWidth =
        (eyeWidth * 2) + eyeGap;

    const int16_t leftX =
        (screenWidth - totalWidth) / 2;

    const int16_t rightX =
        leftX + eyeWidth + eyeGap;

    const int16_t eyeY =
        (screenHeight - eyeHeight) / 2;

    // --------------------------------------------------------
    // DRAW LEFT EYE
    // --------------------------------------------------------

    canvas.fillRoundRect(
        leftX,
        eyeY,
        eyeWidth,
        eyeHeight,
        cornerRadius,
        color
    );

    // --------------------------------------------------------
    // DRAW RIGHT EYE
    // --------------------------------------------------------

    canvas.fillRoundRect(
        rightX,
        eyeY,
        eyeWidth,
        eyeHeight,
        cornerRadius,
        color
    );
}

}
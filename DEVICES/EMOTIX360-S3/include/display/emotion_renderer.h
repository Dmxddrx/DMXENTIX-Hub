#ifndef EMOTION_RENDERER_H
#define EMOTION_RENDERER_H

#include <Adafruit_GFX.h>

namespace EmotionRenderer {

// Draw the default pair of rounded-square robot eyes.
//
// The renderer adapts its eye dimensions to landscape and
// portrait OLED orientations.
//
// The DisplayManager retains ownership of the OLED hardware.
void drawNeutralEyes(
    Adafruit_GFX &canvas,
    uint16_t color = 1
);

}

#endif
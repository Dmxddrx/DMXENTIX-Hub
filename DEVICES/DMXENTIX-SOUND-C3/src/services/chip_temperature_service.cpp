#include "chip_temperature_service.h"

#include <Arduino.h>
#include <math.h>

#include "driver/temperature_sensor.h"
#include "esp_err.h"

namespace ChipTemperatureService {
namespace {

temperature_sensor_handle_t sensorHandle = nullptr;

}

bool begin()
{
    if (sensorHandle != nullptr) {
        return true;
    }

    // Configure the internal sensor for a higher-temperature
    // monitoring range.
    temperature_sensor_config_t config = {};

    config.range_min = 20;
    config.range_max = 100;
    config.clk_src = TEMPERATURE_SENSOR_CLK_SRC_DEFAULT;
    config.flags.allow_pd = 0;

    esp_err_t error = temperature_sensor_install(
        &config,
        &sensorHandle
    );

    if (error != ESP_OK) {
        Serial.printf(
            "[TEMP] Sensor installation failed: %s\n",
            esp_err_to_name(error)
        );

        sensorHandle = nullptr;
        return false;
    }

    error = temperature_sensor_enable(sensorHandle);

    if (error != ESP_OK) {
        Serial.printf(
            "[TEMP] Sensor enable failed: %s\n",
            esp_err_to_name(error)
        );

        (void)temperature_sensor_uninstall(sensorHandle);
        sensorHandle = nullptr;

        return false;
    }

    Serial.println(
        "[TEMP] ESP32-C3 internal temperature sensor initialized"
    );

    return true;
}

bool readCelsius(float &temperatureC)
{
    if (sensorHandle == nullptr) {
        return false;
    }

    float measuredTemperature = 0.0f;

    esp_err_t error = temperature_sensor_get_celsius(
        sensorHandle,
        &measuredTemperature
    );

    if (error != ESP_OK || !isfinite(measuredTemperature)) {
        return false;
    }

    temperatureC = measuredTemperature;

    return true;
}

}
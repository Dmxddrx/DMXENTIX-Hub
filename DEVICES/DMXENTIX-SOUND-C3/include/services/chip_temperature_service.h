#ifndef CHIP_TEMPERATURE_SERVICE_H
#define CHIP_TEMPERATURE_SERVICE_H

namespace ChipTemperatureService {

bool begin();
bool readCelsius(float &temperatureC);

}

#endif
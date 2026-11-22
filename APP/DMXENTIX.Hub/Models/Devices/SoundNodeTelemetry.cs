using System.Globalization;

namespace DMXENTIX.Hub.Models.Devices;

/// <summary>
/// Represents the latest SoundNode audio and chip-temperature telemetry.
/// </summary>
public sealed record SoundNodeTelemetry(
    int Rms,
    int Peak,
    double? ChipTemperatureC)
{
    /// <summary>
    /// Parses the SoundNode key-value status payload.
    ///
    /// Example:
    /// RMS=123,PK=456,TEMP=47.3
    ///
    /// TEMP=NA is accepted when temperature is unavailable.
    /// </summary>
    public static bool TryParse(
        string? payload,
        out SoundNodeTelemetry? telemetry)
    {
        telemetry = null;

        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        );

        foreach (string field in payload.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            int separator = field.IndexOf('=');

            if (separator <= 0)
                continue;

            string key = field[..separator].Trim();
            string value = field[(separator + 1)..].Trim();

            values[key] = value;
        }

        // RMS and PK are required for a valid audio telemetry packet.
        if (!values.TryGetValue("RMS", out string? rmsText) ||
            !int.TryParse(
                rmsText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int rms) ||
            rms < 0)
        {
            return false;
        }

        if (!values.TryGetValue("PK", out string? peakText) ||
            !int.TryParse(
                peakText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int peak) ||
            peak < 0)
        {
            return false;
        }

        // Temperature is optional so older firmware remains compatible.
        double? temperature = null;

        if (values.TryGetValue("TEMP", out string? temperatureText) &&
            double.TryParse(
                temperatureText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedTemperature) &&
            double.IsFinite(parsedTemperature))
        {
            temperature = parsedTemperature;
        }

        telemetry = new SoundNodeTelemetry(
            rms,
            peak,
            temperature
        );

        return true;
    }
}
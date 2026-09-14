using DaveHome;

namespace DaveBrain;

public static class PhilipsAirPlusSensorFormatter
{
    public static string FormatState(DeviceState? state)
    {
        if (state is null)
        {
            return "Unknown";
        }

        var readings = new List<string>();
        if (state.IsOn is { } isOn)
        {
            readings.Add(isOn ? "On" : "Off");
        }

        if (state.Attributes is { } attributes)
        {
            foreach (var attribute in attributes.OrderBy(attribute => attribute.Key, StringComparer.Ordinal))
            {
                readings.Add($"{GetSensorLabel(attribute.Key)}: {attribute.Value}");
            }
        }

        return readings.Count == 0 ? "No readings reported" : string.Join("; ", readings);
    }

    public static string GetSensorLabel(string attribute) => attribute switch
    {
        "pm25UgM3" => "PM2.5 (ug/m3)",
        "allergenIndex" => "Allergen index",
        "airQualityIndex" => "Air quality index",
        "airQualityIndexTarget" => "Air quality target",
        "gasLevel" => "Gas level",
        "temperatureC" => "Temperature (C)",
        "humidityPercent" => "Humidity (%)",
        "humiditySetpointPercent" => "Humidity setpoint (%)",
        "filterStatus0" => "Filter 0 (%)",
        "filterStatus1" => "Filter 1 (%)",
        "filterStatus2" => "Filter 2 (%)",
        "displayBrightnessPercent" => "Display brightness (%)",
        "fanLevel" => "Fan level",
        "mainFilterRemainingHours" => "Main filter remaining (hours)",
        "preFilterRemainingHours" => "Pre-filter remaining (hours)",
        "productState" => "Product state",
        "productError" => "Product error",
        "fanSpeed" => "Fan speed",
        _ => attribute,
    };
}

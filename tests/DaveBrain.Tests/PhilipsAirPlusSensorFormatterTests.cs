using DaveBrain;
using DaveHome;

namespace DaveBrain.Tests;

public sealed class PhilipsAirPlusSensorFormatterTests
{
    [Fact]
    public void FormatStateUsesEnvironmentalSensorLabels()
    {
        var state = new DeviceState(
            true,
            null,
            Attributes: new Dictionary<string, string>
            {
                ["pm25UgM3"] = "12",
                ["temperatureC"] = "21.5",
                ["humidityPercent"] = "44",
                ["fanSpeed"] = "3",
            });

        var formatted = PhilipsAirPlusSensorFormatter.FormatState(state);

        Assert.Contains("On", formatted, StringComparison.Ordinal);
        Assert.Contains("PM2.5 (ug/m3): 12", formatted, StringComparison.Ordinal);
        Assert.Contains("Temperature (C): 21.5", formatted, StringComparison.Ordinal);
        Assert.Contains("Humidity (%): 44", formatted, StringComparison.Ordinal);
        Assert.Contains("Fan speed: 3", formatted, StringComparison.Ordinal);
    }
}

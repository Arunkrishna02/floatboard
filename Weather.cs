using System.Text.Json;

namespace Floatboard;

static class Weather
{
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<string> Read(string city)
    {
        try
        {
            using var places = await Get("https://geocoding-api.open-meteo.com/v1/search?count=1&name="
                + Uri.EscapeDataString(city));
            if (!places.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return $"City not found · {city}";
            var match = results[0];
            double latitude = match.GetProperty("latitude").GetDouble();
            double longitude = match.GetProperty("longitude").GetDouble();
            string url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current=temperature_2m,weather_code");
            using var forecast = await Get(url);
            var current = forecast.RootElement.GetProperty("current");
            double temperature = current.GetProperty("temperature_2m").GetDouble();
            string condition = current.GetProperty("weather_code").GetInt32() switch
            {
                0 => "Clear", 1 or 2 => "Partly cloudy", 3 => "Overcast",
                45 or 48 => "Fog", >= 51 and <= 57 => "Drizzle",
                >= 61 and <= 67 => "Rain", >= 71 and <= 77 => "Snow",
                >= 80 and <= 82 => "Showers", 85 or 86 => "Snow showers",
                >= 95 => "Thunderstorms", _ => "Conditions unavailable"
            };
            string place = match.GetProperty("name").GetString() ?? city;
            return $"{temperature:F0} °C · {condition} · {place}";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return $"Weather unavailable · {city}";
        }
    }

    static async Task<JsonDocument> Get(string url) => JsonDocument.Parse(await Client.GetStringAsync(url));
}

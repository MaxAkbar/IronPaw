# Example: Weather Forecast Skill

This is a complete worked example of building a skill add-in that provides a weather forecast tool. The agent will be able to call `skill_weather_forecast` to get weather data.

> **Note**: This example uses a mock implementation. To use a real weather API, replace the `GetForecastAsync` method with an HTTP call to a service like OpenWeatherMap.

## Full source code

### Project file (`IronPaw.Skills.Weather.csproj`)

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\IronPaw.AgentCore.Abstractions\IronPaw.AgentCore.Abstractions.csproj" />
  </ItemGroup>

  <Target Name="CopyToAddins" AfterTargets="Build">
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(SolutionDir)addins\skills" SkipUnchangedFiles="true" />
  </Target>

</Project>
```

### Add-in entry point (`WeatherAddin.cs`)

```csharp
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Skills.Weather;

public sealed class WeatherAddin : ISkillAddin
{
    public string AddinId => "weather.skills";

    public string Description => "Weather-related skills for location-based forecasts.";

    public IReadOnlyList<ISkill> CreateSkills()
    {
        return [new WeatherForecastSkill()];
    }
}
```

### Skill implementation (`WeatherForecastSkill.cs`)

```csharp
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.Skills.Weather;

internal sealed class WeatherForecastSkill : ISkill
{
    public SkillDescriptor Descriptor { get; } = new(
        Name: "weather_forecast",
        Description: "Gets the current weather forecast for a given city. Returns temperature, conditions, and humidity.",
        InputSchema: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                city = new
                {
                    type = "string",
                    description = "The city name (e.g., 'London', 'New York', 'Tokyo')."
                },
                units = new
                {
                    type = "string",
                    description = "Temperature units: 'celsius' or 'fahrenheit'. Defaults to 'celsius'.",
                    @enum = new[] { "celsius", "fahrenheit" }
                }
            },
            required = new[] { "city" }
        }));

    public async Task<SkillResult> ExecuteAsync(
        SkillCall call,
        SkillExecutionContext context,
        CancellationToken ct)
    {
        // 1. Validate required parameter
        if (!call.Arguments.TryGetProperty("city", out var cityElement) ||
            cityElement.ValueKind is not JsonValueKind.String)
        {
            return SkillResult.Error("Missing required string argument: city");
        }

        var city = cityElement.GetString()!;

        // 2. Read optional parameter with default
        var units = "celsius";
        if (call.Arguments.TryGetProperty("units", out var unitsElement) &&
            unitsElement.ValueKind is JsonValueKind.String)
        {
            units = unitsElement.GetString() ?? "celsius";
        }

        // 3. Get the forecast (mock implementation)
        var forecast = await GetForecastAsync(city, units, ct);

        if (forecast is null)
        {
            return SkillResult.Error($"Could not find weather data for '{city}'.");
        }

        // 4. Return as JSON
        var json = JsonSerializer.Serialize(forecast);
        return SkillResult.Success(json);
    }

    /// <summary>
    /// Mock forecast implementation.
    /// Replace with a real API call (e.g., OpenWeatherMap) for production use.
    /// </summary>
    private static Task<WeatherData?> GetForecastAsync(
        string city, string units, CancellationToken ct)
    {
        // Simulate some known cities
        var data = city.ToLowerInvariant() switch
        {
            "london" => new WeatherData(city, 15, 72, "Partly cloudy", units),
            "new york" => new WeatherData(city, 22, 55, "Sunny", units),
            "tokyo" => new WeatherData(city, 28, 80, "Humid and warm", units),
            "sydney" => new WeatherData(city, 18, 60, "Clear skies", units),
            "paris" => new WeatherData(city, 17, 65, "Light rain", units),
            _ => new WeatherData(city, 20, 50, "Fair", units)
        };

        // Convert to Fahrenheit if requested
        if (units == "fahrenheit")
        {
            data = data with { Temperature = data.Temperature * 9 / 5 + 32 };
        }

        return Task.FromResult<WeatherData?>(data);
    }

    private sealed record WeatherData(
        string City,
        int Temperature,
        int HumidityPercent,
        string Conditions,
        string Units);
}
```

## What this demonstrates

1. **Required and optional parameters** — `city` is required, `units` is optional with a default
2. **Enum constraint** — `units` is restricted to `"celsius"` or `"fahrenheit"`
3. **Validation** — returns `SkillResult.Error` for missing arguments
4. **Async pattern** — `ExecuteAsync` is async-ready (the mock uses `Task.FromResult`, but a real HTTP call would `await`)
5. **Structured JSON output** — the result is a serialized object, giving the LLM rich data to work with

## Usage

After building, start an interactive session:

```powershell
dotnet run --project src/IronPaw.Cli -- --provider openai
```

```
> What's the weather like in Tokyo?
```

The agent calls `skill_weather_forecast` with `{"city": "Tokyo"}` and responds with something like:

> The weather in Tokyo is currently humid and warm at 28°C with 80% humidity.

```
> How about New York in Fahrenheit?
```

The agent calls `skill_weather_forecast` with `{"city": "New York", "units": "fahrenheit"}` and responds:

> New York is sunny at 72°F with 55% humidity.

## Making it real

To connect to a real weather API, replace `GetForecastAsync` with an HTTP client call:

```csharp
private readonly HttpClient _http = new();

private async Task<WeatherData?> GetForecastAsync(
    string city, string units, CancellationToken ct)
{
    var apiKey = Environment.GetEnvironmentVariable("WEATHER_API_KEY");
    var url = $"https://api.openweathermap.org/data/2.5/weather?q={city}&appid={apiKey}&units=metric";

    var response = await _http.GetAsync(url, ct);
    if (!response.IsSuccessStatusCode)
        return null;

    var json = await response.Content.ReadAsStringAsync(ct);
    var doc = JsonDocument.Parse(json);
    var main = doc.RootElement.GetProperty("main");
    var weather = doc.RootElement.GetProperty("weather")[0];

    var tempC = (int)main.GetProperty("temp").GetDouble();
    var humidity = main.GetProperty("humidity").GetInt32();
    var conditions = weather.GetProperty("description").GetString() ?? "Unknown";

    var temp = units == "fahrenheit" ? tempC * 9 / 5 + 32 : tempC;

    return new WeatherData(city, temp, humidity, conditions, units);
}
```

## Testing

You can test skills directly without running the full agent:

```csharp
[Fact]
public async Task WeatherForecast_ReturnsDataForKnownCity()
{
    var skill = new WeatherForecastSkill();
    var call = new SkillCall(
        "test-id",
        "weather_forecast",
        JsonSerializer.SerializeToElement(new { city = "London" }));

    var result = await skill.ExecuteAsync(
        call,
        new SkillExecutionContext(RunId.New(), new SessionKey("test")),
        CancellationToken.None);

    Assert.False(result.IsError);
    var doc = JsonDocument.Parse(result.Content);
    Assert.Equal("London", doc.RootElement.GetProperty("City").GetString());
}

[Fact]
public async Task WeatherForecast_ReturnsErrorForMissingCity()
{
    var skill = new WeatherForecastSkill();
    var call = new SkillCall(
        "test-id",
        "weather_forecast",
        JsonSerializer.SerializeToElement(new { units = "fahrenheit" }));

    var result = await skill.ExecuteAsync(
        call,
        new SkillExecutionContext(RunId.New(), new SessionKey("test")),
        CancellationToken.None);

    Assert.True(result.IsError);
}
```

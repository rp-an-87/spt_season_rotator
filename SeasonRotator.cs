using SPTarkov.Common.Extensions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json;
using Season = SPTarkov.Server.Core.Models.Enums.Season;
namespace SeasonRotator;

public record ModMetadata : IModMetadata{
    public string ModGuid { get; init; } = "com.turbodestroyer.seasonrotator";
    public string Name { get; init; } = "SeasonRotator";
    public string Author { get; init; } = "turbodestroyer";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");

    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class ChangeSeason(WeatherConfig weatherConfig, ISptLogger<ChangeSeason> logger, ModHelper modHelper) : IOnLoad {
    private readonly WeatherConfig _weatherConfig = weatherConfig;
    private readonly int[] seasonsArray = [0, 1, 4, 6, 2, 5, 3];
    private ModState _modState = new();

    public Task OnLoadAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config.json");
        // get season, override season
        var season = CurrentStateSeason(pathToMod, config);
        var seasonEnum = (Season)season;
        _weatherConfig.OverrideSeason = seasonEnum;
        // override weather
        // currently weather presets (enums) contain only 'sunny', 'cloudy' and 'rainy', so have to change them directly
        if (config.UseCustomWeather) {
            var root = modHelper.GetJsonDataFromFile<JsonNode>(pathToMod, "customWeather.json");
            var weatherPresetWeightNode = root?["weatherPresetWeight"] as JsonObject;
            if (weatherPresetWeightNode is not null) {
                foreach (var kvp in weatherPresetWeightNode) {
                    var seasonKey = kvp.Key;
                    var valueObj = kvp.Value as JsonObject;
                    var dict = new Dictionary<SPTarkov.Server.Core.Models.Spt.Config.WeatherPreset, double>();
                    if (valueObj != null) {
                        foreach (var inner in valueObj) {
                            if (Enum.TryParse(inner.Key, true, out SPTarkov.Server.Core.Models.Spt.Config.WeatherPreset preset) && inner.Value is JsonValue jv && jv.TryGetValue<double>(out var w)) {
                                dict[preset] = w;
                            }
                        }
                    }
                    _weatherConfig.Weather.WeatherPresetWeight[seasonKey] = dict;
                }
            }
            if (seasonEnum is Season.AUTUMN or Season.SPRING or Season.SPRING_EARLY or Season.AUTUMN_LATE or Season.WINTER) {
                var seasonWeatherKey = "DEMI";
                if (seasonEnum is Season.WINTER) { seasonWeatherKey = "WINTER"; }
                var presetWeightNode = root?["Presets"]?[seasonWeatherKey] as JsonObject;
                if (presetWeightNode is not null) {
                    foreach (var kvp in presetWeightNode) {
                        var key = kvp.Key;
                        if (kvp.Value is JsonNode node)
                        {
                            var value = node.Deserialize<SPTarkov.Server.Core.Models.Spt.Config.PresetWeights>();
                            if (value != null && _weatherConfig.Weather.PresetWeights is { } presetWeights)
                            {
                                presetWeights[key] = value;
                            }
                        }
                    }
                }
            }
        }
        logger.Info("[Season Rotator] " + _weatherConfig.OverrideSeason?.ToString() + " will end on " + _modState.EndDate);
        return Task.CompletedTask;
    }

    // Returns the current season based on the state file, or creates a new state file if it doesn't exist
    private int CurrentStateSeason(string modPath, ModConfig config) {
        var stateFilePath = Path.Combine(modPath, "state.json");
        if (!File.Exists(stateFilePath)) {
            // if state file does not exist, create it and set the season to be the next one after the current season
            var currentSeason = GetCurrentDefaultSeason(modPath);
            var currentSeasonEnum = (Season)currentSeason;
            var startDate = DateTime.Now;
            var currentSeasonIndex = seasonsArray.ToList().IndexOf(currentSeason);
            var newSeasonIndex = (currentSeasonIndex + 1) % seasonsArray.Length;
            var newSeason = seasonsArray[newSeasonIndex];
            var newSeasonEnum = (Season)newSeason;
            var seasonLength = config.SeasonsLengths.GetValueOrDefault(newSeasonEnum, 7);
            
            // Skip seasons with 0 length
            var attempts = 0;
            while (seasonLength == 0 && attempts < seasonsArray.Length) {
                newSeasonIndex = (newSeasonIndex + 1) % seasonsArray.Length;
                newSeason = seasonsArray[newSeasonIndex];
                newSeasonEnum = (Season)newSeason;
                seasonLength = config.SeasonsLengths.GetValueOrDefault(newSeasonEnum, 7);
                attempts++;
            }
            if (seasonLength == 0) {
                seasonLength = 7; 
            }
            var endDDate = startDate.AddDays(seasonLength);
            logger.Info("[Season Rotator] The state file not found, creating a new one. The old season was " + currentSeasonEnum.ToString());
            var newState = new ModState {
                StartDate = startDate.ToString("yyyy-MM-dd"),
                EndDate = endDDate.ToString("yyyy-MM-dd"),
                CurrentSeason = newSeason
            };
            ModExtensions.SaveJsonDataToFile(modHelper, modPath, "state.json", newState);
            _modState = newState;
            return newSeason;
        } 
        // if state file exists, check if the season has expired
        var state = modHelper.GetJsonDataFromFile<ModState>(modPath, "state.json");
        var today = DateTime.Now;
        var endDate = DateTime.Parse(state.EndDate);
        if (today > endDate) {
            // season has expired, calculate how many seasons have passed and set the new season
            var currentSeasonEnum = (Season)state.CurrentSeason;
            var currentSeasonIndex = seasonsArray.ToList().IndexOf(state.CurrentSeason);
            var daysOverdue = (today - endDate).TotalDays;
            var seasonsPassed = 0;
            var accumulatedDays = 0.0;
            var tempIndex = currentSeasonIndex;
            while (accumulatedDays < daysOverdue) {
                tempIndex = (tempIndex + 1) % seasonsArray.Length;
                var tempSeasonEnum = (Season)seasonsArray[tempIndex];
                var seasonLength = config.SeasonsLengths.GetValueOrDefault(tempSeasonEnum, 7);
                accumulatedDays += seasonLength;
                seasonsPassed++;
            }
            
            var newSeasonIndex = (currentSeasonIndex + seasonsPassed) % seasonsArray.Length;
            var newSeason = seasonsArray[newSeasonIndex];
            var newSeasonEnum = (Season)newSeason;
            var newSeasonLength = config.SeasonsLengths.GetValueOrDefault(newSeasonEnum, 7);
            
            // Skip seasons with 0 length
            var skipAttempts = 0;
            while (newSeasonLength == 0 && skipAttempts < seasonsArray.Length) {
                newSeasonIndex = (newSeasonIndex + 1) % seasonsArray.Length;
                newSeason = seasonsArray[newSeasonIndex];
                newSeasonEnum = (Season)newSeason;
                newSeasonLength = config.SeasonsLengths.GetValueOrDefault(newSeasonEnum, 7);
                skipAttempts++;
            }

            if (newSeasonLength == 0) {
                newSeasonLength = 7; 
            }
            
            var newStartDate = endDate.AddDays(accumulatedDays - newSeasonLength);
            var newEndDate = newStartDate.AddDays(newSeasonLength);
            
            state = new ModState {
                StartDate = newStartDate.ToString("yyyy-MM-dd"),
                EndDate = newEndDate.ToString("yyyy-MM-dd"),
                CurrentSeason = newSeason
            };
            ModExtensions.SaveJsonDataToFile(modHelper, modPath, "state.json", state);
        }
        _modState = state;
        return state.CurrentSeason;
    }

    // Returns the current season based on the default season dates in the SPT weather config
    private int GetCurrentDefaultSeason(string modPath) {
        if (_weatherConfig.OverrideSeason.HasValue && seasonsArray.Contains((int)_weatherConfig.OverrideSeason.Value)) {
            return (int)_weatherConfig.OverrideSeason.Value;
        }

        var seasonDates = _weatherConfig.SeasonDates;
        var md = DateTime.Now.Month * 100 + DateTime.Now.Day;
        foreach (var s in seasonDates) {
            var start = s.StartMonth.GetValueOrDefault() * 100 + s.StartDay.GetValueOrDefault();
            var end = s.EndMonth.GetValueOrDefault() * 100 + s.EndDay.GetValueOrDefault();
            var wraps = end < start;
            var inRange = wraps ? md >= start || md <= end : md >= start && md <= end;
            if (inRange) {
                return (int)s.SeasonType.GetValueOrDefault();
            }
        }

        return 0;
    }
}

public class ModConfig {
    public Dictionary<Season, int> SeasonsLengths { get; set; } = new();
    public bool UseCustomWeather { get; set; }
}

public class ModState {
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    public int CurrentSeason { get; set; }
}

public static class ModExtensions
{
    public static void SaveJsonDataToFile<T>(this ModHelper modHelper, string pathToFile, string fileName, T data)
    {
        var fullPath = Path.Combine(pathToFile, fileName);
        var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(fullPath, json);
    }
}

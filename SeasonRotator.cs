using SPTarkov.Common.Extensions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Web;
using SPTarkov.Server.Web.Models.Configs;
using SPTarkov.Server.Web.Services;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Season = SPTarkov.Server.Core.Models.Enums.Season;

namespace SeasonRotator;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.turbodestroyer.seasonrotator.raids";
    public string Name { get; init; } = "SeasonRotator (raid-based)";
    public string Author { get; init; } = "turbodestroyer";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}

[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 1)]
public class ChangeSeason(WeatherConfig weatherConfig, ISptLogger<ChangeSeason> logger, ModHelper modHelper) : IOnLoad
{
    private readonly int[] seasonsArray = [0, 1, 4, 6, 2, 5, 3];
    private readonly object _lock = new();
    private string _modPath = string.Empty;
    private ModState _state = new();

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<ModConfig>(_modPath, "config.json");

        lock (_lock)
        {
            LoadOrCreateState(config);
            ApplySeason(config);
        }

        LogStatus(config);
        return Task.CompletedTask;
    }

    // Called by the router every time a raid ends
    public void RegisterRaidFinished()
    {
        lock (_lock)
        {
            var config = modHelper.GetJsonDataFromFile<ModConfig>(_modPath, "config.json");

            _state.RaidsInSeason++;
            _state.TotalRaids++;

            var changed = AdvanceIfDue(config);
            ModExtensions.SaveJsonDataToFile(modHelper, _modPath, "state.json", _state);

            if (changed)
            {
                ApplySeason(config);
            }

            LogStatus(config);
        }
    }

    private void LoadOrCreateState(ModConfig config)
    {
        var stateFilePath = Path.Combine(_modPath, "state.json");

        if (File.Exists(stateFilePath))
        {
            _state = modHelper.GetJsonDataFromFile<ModState>(_modPath, "state.json");
            // season lengths may have been edited in config since the last run
            if (AdvanceIfDue(config))
            {
                ModExtensions.SaveJsonDataToFile(modHelper, _modPath, "state.json", _state);
            }
            return;
        }

        // First run: start on the season after the current default one (same as the original mod)
        var currentSeason = GetCurrentDefaultSeason();
        var idx = Array.IndexOf(seasonsArray, currentSeason);
        _state = new ModState
        {
            CurrentSeason = seasonsArray[(idx + 1) % seasonsArray.Length],
            RaidsInSeason = 0,
            TotalRaids = 0
        };
        AdvanceIfDue(config); // skips seasons with a length of 0
        ModExtensions.SaveJsonDataToFile(modHelper, _modPath, "state.json", _state);
        logger.Info("[Season Rotator] No state file found, created a new one. Previous season was " + (Season)currentSeason);
    }

    // Moves to the next season while the current one is finished (or disabled with length 0).
    // Returns true if the season changed.
    private bool AdvanceIfDue(ModConfig config)
    {
        var changed = false;

        for (var guard = 0; guard <= seasonsArray.Length; guard++)
        {
            var length = LengthOf(config, _state.CurrentSeason);
            if (length > 0 && _state.RaidsInSeason < length)
            {
                break;
            }

            var idx = Array.IndexOf(seasonsArray, _state.CurrentSeason);
            _state.CurrentSeason = seasonsArray[(idx + 1) % seasonsArray.Length];
            _state.RaidsInSeason = 0;
            changed = true;
        }

        return changed;
    }

    private static int LengthOf(ModConfig config, int season)
    {
        var name = ((Season)season).ToString();
        foreach (var (key, length) in config.SeasonsLengths)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return length;
            }
        }
        return 7;
    }

    private void ApplySeason(ModConfig config)
    {
        var seasonEnum = (Season)_state.CurrentSeason;
        weatherConfig.OverrideSeason = seasonEnum;

        if (!config.UseCustomWeather)
        {
            return;
        }

        var root = modHelper.GetJsonDataFromFile<JsonNode>(_modPath, "customWeather.json");
        var weatherPresetWeightNode = root?["weatherPresetWeight"] as JsonObject;

        if (weatherPresetWeightNode is not null)
        {
            foreach (var kvp in weatherPresetWeightNode)
            {
                var dict = new Dictionary<WeatherPreset, double>();
                if (kvp.Value is JsonObject valueObj)
                {
                    foreach (var inner in valueObj)
                    {
                        if (Enum.TryParse(inner.Key, true, out WeatherPreset preset)
                            && inner.Value is JsonValue jv && jv.TryGetValue<double>(out var w))
                        {
                            dict[preset] = w;
                        }
                    }
                }
                weatherConfig.Weather.WeatherPresetWeight[kvp.Key] = dict;
            }
        }

        if (seasonEnum is Season.AUTUMN or Season.SPRING or Season.SPRING_EARLY or Season.AUTUMN_LATE or Season.WINTER)
        {
            var seasonWeatherKey = seasonEnum is Season.WINTER ? "WINTER" : "DEMI";
            if (root?["Presets"]?[seasonWeatherKey] is JsonObject presetWeightNode)
            {
                foreach (var kvp in presetWeightNode)
                {
                    if (kvp.Value is JsonNode node)
                    {
                        var value = node.Deserialize<PresetWeights>();
                        if (value != null && weatherConfig.Weather.PresetWeights is { } presetWeights)
                        {
                            presetWeights[kvp.Key] = value;
                        }
                    }
                }
            }
        }
    }

    private void LogStatus(ModConfig config)
    {
        var length = LengthOf(config, _state.CurrentSeason);
        logger.Info($"[Season Rotator] {(Season)_state.CurrentSeason}: raid {_state.RaidsInSeason}/{length} completed " +
                    $"(lifetime raids: {_state.TotalRaids})");
    }

    private int GetCurrentDefaultSeason()
    {
        if (weatherConfig.OverrideSeason.HasValue && seasonsArray.Contains((int)weatherConfig.OverrideSeason.Value))
        {
            return (int)weatherConfig.OverrideSeason.Value;
        }

        var md = DateTime.Now.Month * 100 + DateTime.Now.Day;
        foreach (var s in weatherConfig.SeasonDates)
        {
            var start = s.StartMonth.GetValueOrDefault() * 100 + s.StartDay.GetValueOrDefault();
            var end = s.EndMonth.GetValueOrDefault() * 100 + s.EndDay.GetValueOrDefault();
            var wraps = end < start;
            var inRange = wraps ? md >= start || md <= end : md >= start && md <= end;
            if (inRange)
            {
                return (int)s.SeasonType.GetValueOrDefault();
            }
        }
        return 0;
    }
}

// Lets the web config editor (/configs) list and edit this mod's config.json
[Injectable(InjectionType.Singleton)]
public class SeasonRotatorConfigProvider(ModHelper modHelper) : IConfigEditorConfigProvider
{
    public IEnumerable<ConfigEditorConfigRegistration> GetConfigs()
    {
        var meta = new ModMetadata();
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        yield return ConfigEditorConfigRegistration.Create(
            meta.ModGuid,
            meta.Name,
            modHelper.GetJsonDataFromFile<ModConfig>(modPath, "config.json"),
            Path.Combine(modPath, "config.json"));
    }
}

// Hooks the end-of-raid request so every finished raid is counted.
// NOTE: RouteAction / StaticRouter signatures can differ slightly between SPT 4.x builds -
// if this doesn't compile, check the signature against another 4.1 mod that uses StaticRouter.
[Injectable]
public class RaidCounterRouter(JsonUtil jsonUtil, ChangeSeason changeSeason) : StaticRouter(jsonUtil,
[
    new RouteAction<EndLocalRaidRequestData>(
        "/client/match/local/end",
        async (url, info, sessionId, output, _) =>
        {
            changeSeason.RegisterRaidFinished();
            return output!;
        })
])
{
}

public class ModConfig
{
    // Now measured in RAIDS instead of days. 0 = skip this season.
    // Keyed by season name (WINTER, SPRING, ...) so the config editor shows names, not enum numbers.
    public Dictionary<string, int> SeasonsLengths { get; set; } = new();
    public bool UseCustomWeather { get; set; }
}

public class ModState
{
    public int CurrentSeason { get; set; }
    public int RaidsInSeason { get; set; }
    public int TotalRaids { get; set; }
}

public static class ModExtensions
{
    public static void SaveJsonDataToFile<T>(this ModHelper modHelper, string pathToFile, string fileName, T data)
    {
        var fullPath = Path.Combine(pathToFile, fileName);
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(fullPath, json);
    }
}
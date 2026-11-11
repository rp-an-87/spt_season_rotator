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
using EftWeather = SPTarkov.Server.Core.Models.Eft.Weather.Weather;
using HarmonyLib;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Models.Spt.Weather;

namespace SeasonRotator;

public record ModMetadata : IModMetadata, IModBlazorMetadata
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

    public string? WWWRootUrl { get; init; }
    public string? HomePage { get; init; } = "/season-rotator";
    public string? HomePageDescription { get; init; } = "Current season, raid counter and settings.";
}

[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 1)]
public class ChangeSeason(WeatherConfig weatherConfig, ISptLogger<ChangeSeason> logger, ModHelper modHelper, MailSendService mailSendService) : IOnLoad
{
    private readonly int[] seasonsArray = [0, 1, 4, 6, 2, 5, 3];
    private readonly object _lock = new();
    private string _modPath = string.Empty;
    private ModState _state = new();
    private readonly Dictionary<string, (string Location, bool IsScav)> _raidInfo = new();
    private static readonly HashSet<string> FactoryLocations = new(StringComparer.OrdinalIgnoreCase) { "factory4_day", "factory4_night" };
    // The patch on the weather request reaches the mod through this
    internal static ChangeSeason? Instance;
    // Maps with no outdoor weather: a weather event is never used on them
    private static readonly HashSet<string> NoWeatherLocations = new(StringComparer.OrdinalIgnoreCase) { "factory4_day", "factory4_night", "laboratory", "labyrinth" };
    // Sessions whose current raid is getting a weather event (it counts as used when that raid finishes)
    private readonly HashSet<string> _eventApplied = new();

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Instance = this;
        InstallEventPatch();

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

    // Snapshot for the web page
    public SeasonStatus GetStatus()
    {
        lock (_lock)
        {
            var config = modHelper.GetJsonDataFromFile<ModConfig>(_modPath, "config.json");
            return new SeasonStatus
            {
                Season = ((Season)_state.CurrentSeason).ToString(),
                SeasonLength = LengthOf(config, _state.CurrentSeason),
                RaidsInSeason = _state.RaidsInSeason,
                TotalRaids = _state.TotalRaids,
                FactoryRaidsSinceAdvance = _state.FactoryRaidsSinceAdvance,
                FactoryRaidsPerAdvance = config.FactoryRaidsPerAdvance,
                NextSeason = ((Season)NextSeasonAfter(config, _state.CurrentSeason)).ToString(),
                SeasonOrder = seasonsArray.Select(season => ((Season)season).ToString()).ToList()
            };
        }
    }

    public ModConfig GetConfig()
    {
        lock (_lock)
        {
            return modHelper.GetJsonDataFromFile<ModConfig>(_modPath, "config.json");
        }
    }

    // Saves the config and applies it right away (a shorter season may already be over, custom weather is re-applied)
    public void SaveConfig(ModConfig config)
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(_modPath, "config.json"), json);

            AdvanceIfDue(config);
            ModExtensions.SaveJsonDataToFile(modHelper, _modPath, "state.json", _state);
            ApplySeason(config);
            LogStatus(config);
        }
    }

    // Ends the current season now and moves to the next one that is enabled
    public void SkipSeason()
    {
        lock (_lock)
        {
            var config = modHelper.GetJsonDataFromFile<ModConfig>(_modPath, "config.json");
            _state.CurrentSeason = NextSeasonAfter(config, _state.CurrentSeason);
            _state.RaidsInSeason = 0;
            _state.FactoryRaidsSinceAdvance = 0;
            ModExtensions.SaveJsonDataToFile(modHelper, _modPath, "state.json", _state);
            ApplySeason(config);
            LogStatus(config);
        }
    }

    // The season that follows the given one, skipping disabled seasons (length 0)
    private int NextSeasonAfter(ModConfig config, int season)
    {
        var idx = Array.IndexOf(seasonsArray, season);
        for (var step = 1; step <= seasonsArray.Length; step++)
        {
            var next = seasonsArray[(idx + step) % seasonsArray.Length];
            if (LengthOf(config, next) > 0)
            {
                return next;
            }
        }
        return season;
    }

    // Called by the router when a raid starts, so we know which map the matching end-of-raid belongs to
    public void RegisterRaidStarted(string sessionId, string? location, string? playerSide)
    {
        lock (_lock)
        {
            _raidInfo[sessionId] = (location ?? string.Empty, string.Equals(playerSide, "Savage", StringComparison.OrdinalIgnoreCase));
            _eventApplied.Remove(sessionId);
        }
    }

    // Called by the router every time a raid ends
    public void RegisterRaidFinished(string sessionId)
    {
        lock (_lock)
        {
            var config = modHelper.GetJsonDataFromFile<ModConfig>(_modPath, "config.json");

            _raidInfo.Remove(sessionId, out var raid);
            if (_eventApplied.Remove(sessionId))
            {
                FinishEvent();
            }

            _state.TotalRaids++;

            if (CountsTowardSeason(config, raid.Location, raid.IsScav))
            {
                _state.RaidsInSeason++;
            }

            var changed = AdvanceIfDue(config);
            ModExtensions.SaveJsonDataToFile(modHelper, _modPath, "state.json", _state);

            if (changed)
            {
                ApplySeason(config);
            }

            AnnounceEventIfDue(config, sessionId);
            LogStatus(config);
        }
    }

    // Factory raids are short, so they can be set to only advance the season every N factory raids
    // (day and night share one counter). FactoryRaidsPerAdvance <= 1 keeps the normal behavior.
    private bool CountsTowardSeason(ModConfig config, string? location, bool isScav)
    {
        if (isScav && !config.ScavRaidsAdvanceSeason)
        {
            return false;
        }

        var isFactory = location is not null && FactoryLocations.Contains(location);
        if (!isFactory || config.FactoryRaidsPerAdvance <= 1)
        {
            return true;
        }

        _state.FactoryRaidsSinceAdvance++;
        if (_state.FactoryRaidsSinceAdvance < config.FactoryRaidsPerAdvance)
        {
            return false;
        }

        _state.FactoryRaidsSinceAdvance = 0;
        return true;
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
            _state.FactoryRaidsSinceAdvance = 0;
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
        EnsureEventSchedule(config);

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

    // ---- Weather events -----------------------------------------------------------------------------------------
    // A weather event (storm, fog, blizzard, see "Events" in customWeather.json) replaces the weather of one raid. Each full
    // season gets EventsPerSeason of them, on raids picked at random when the season starts. Seasons that come in two halves
    // count as one: AUTUMN + AUTUMN_LATE and SPRING_EARLY + SPRING share one schedule. Season ids in rotation order:
    // SUMMER 0, AUTUMN 1, AUTUMN_LATE 4, STORM 6, WINTER 2, SPRING_EARLY 5, SPRING 3 (STORM has no events).
    private static readonly int[][] EventGroups = [[0], [1, 4], [2], [5, 3]];

    private static int[]? GroupFor(int season) => EventGroups.FirstOrDefault(group => group.Contains(season));

    private static string GroupKey(int[] group) => ((Season)group[0]).ToString();

    // Counted raids of the group that came before the current season of it, plus the ones in the current season
    private int RaidsDoneInGroup(ModConfig config, int[] group) =>
        group.TakeWhile(season => season != _state.CurrentSeason).Sum(season => LengthOf(config, season)) + _state.RaidsInSeason;

    // Caller holds _lock. When the season group changes (or there is no schedule yet), pick the raids that get an event.
    private void EnsureEventSchedule(ModConfig config)
    {
        var group = GroupFor(_state.CurrentSeason);
        var key = group is null ? null : GroupKey(group);
        if (key == _state.EventGroup)
        {
            return;
        }

        _state.EventGroup = key;
        _state.EventRaids = [];
        _state.ActiveEvent = null;
        _state.EventAnnounced = false;

        if (group is not null && config.EventsPerSeason > 0)
        {
            var length = group.Sum(season => LengthOf(config, season));
            var done = RaidsDoneInGroup(config, group);
            var free = Enumerable.Range(done + 1, Math.Max(0, length - done)).ToList();
            var count = config.RandomEventCount ? Random.Shared.Next(0, config.EventsPerSeason + 1) : config.EventsPerSeason;
            count = Math.Min(count, free.Count);
            _state.EventRaids = free.OrderBy(_ => Random.Shared.Next()).Take(count).Order().ToList();
            logger.Info($"[Season Rotator] Weather events for {key}: {count} planned, on raid {(count == 0 ? "-" : string.Join(", ", _state.EventRaids))} of {length}");
        }

        ModExtensions.SaveJsonDataToFile(modHelper, _modPath, "state.json", _state);
    }

    // Caller holds _lock. The raid that had the event is over: it is used up.
    private void FinishEvent()
    {
        if (_state.EventRaids.Count > 0)
        {
            _state.EventRaids.RemoveAt(0);
        }

        _state.ActiveEvent = null;
        _state.EventAnnounced = false;
        logger.Info($"[Season Rotator] Weather event finished, {_state.EventRaids.Count} left this season");
    }

    // Caller holds _lock. True when the next counted raid of the season group is one that has an event planned.
    private bool EventDueNextRaid(ModConfig config)
    {
        if (!config.UseCustomWeather || _state.EventRaids.Count == 0)
        {
            return false;
        }

        var group = GroupFor(_state.CurrentSeason);
        return group is not null && GroupKey(group) == _state.EventGroup && RaidsDoneInGroup(config, group) + 1 >= _state.EventRaids[0];
    }

    // Caller holds _lock. Picks (weighted, for the current season) the event of the due raid unless one is already picked and saved.
    private bool EnsureActiveEvent(Dictionary<string, WeatherEventDef> events)
    {
        if (_state.ActiveEvent is not null && events.ContainsKey(_state.ActiveEvent))
        {
            return true;
        }

        var season = ((Season)_state.CurrentSeason).ToString();
        var allowed = events.Where(e => e.Value.Seasons.Any(s => string.Equals(s, season, StringComparison.OrdinalIgnoreCase)) && e.Value.Weight > 0).ToList();
        if (allowed.Count == 0)
        {
            logger.Warning($"[Season Rotator] A weather event is due but none in customWeather.json \"Events\" allows {season}");
            return false;
        }

        var roll = Random.Shared.NextDouble() * allowed.Sum(e => e.Value.Weight);
        var pick = allowed[^1];
        foreach (var candidate in allowed)
        {
            roll -= candidate.Value.Weight;
            if (roll < 0)
            {
                pick = candidate;
                break;
            }
        }

        _state.ActiveEvent = pick.Key;
        _state.EventAnnounced = false;
        ModExtensions.SaveJsonDataToFile(modHelper, _modPath, "state.json", _state);
        return true;
    }

    // Caller holds _lock. After a raid: when the next raid has an event, Jaeger sends the weather warning (once per event).
    private void AnnounceEventIfDue(ModConfig config, string sessionId)
    {
        try
        {
            if (!config.AnnounceEvents || _state.EventAnnounced || !EventDueNextRaid(config))
            {
                return;
            }

            var events = LoadEvents();
            if (!EnsureActiveEvent(events))
            {
                return;
            }

            var name = _state.ActiveEvent!;
            var text = string.IsNullOrWhiteSpace(events[name].Announcement)
                ? $"Mercenary. Word from the woods: {name.ToLowerInvariant()} is on its way. Be ready for it."
                : events[name].Announcement!;
            text += "\n\nIt will hit your next raid outside. The factory and the labs never see the weather.";

            mailSendService.SendDirectNpcMessageToPlayer(new MongoId(sessionId), Traders.JAEGER, MessageType.NpcTraderMessage, text, null);
            _state.EventAnnounced = true;
            ModExtensions.SaveJsonDataToFile(modHelper, _modPath, "state.json", _state);
            logger.Info($"[Season Rotator] Jaeger announced the weather event '{name}' for the next raid");
        }
        catch (Exception e)
        {
            logger.Error($"[Season Rotator] Could not send the weather event message: {e}");
        }
    }

    private Dictionary<string, WeatherEventDef> LoadEvents()
    {
        var root = modHelper.GetJsonDataFromFile<JsonNode>(_modPath, "customWeather.json");
        return root?["Events"]?.Deserialize<Dictionary<string, WeatherEventDef>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? new Dictionary<string, WeatherEventDef>();
    }

    // The raid is about to start and asks for its weather (called by the patch on WeatherController.GenerateLocal).
    // Returns the forecast with the event applied, or null when this raid gets no event.
    internal List<EftWeather>? ApplyEvent(string sessionId, List<EftWeather>? forecast)
    {
        if (forecast is null || forecast.Count == 0)
        {
            return null;
        }

        lock (_lock)
        {
            var config = modHelper.GetJsonDataFromFile<ModConfig>(_modPath, "config.json");
            if (!config.UseCustomWeather || _state.EventRaids.Count == 0)
            {
                return null;
            }

            // only a raid that counts toward the season and has outdoor weather uses up an event
            if (!_raidInfo.TryGetValue(sessionId, out var raid) || NoWeatherLocations.Contains(raid.Location) || (raid.IsScav && !config.ScavRaidsAdvanceSeason))
            {
                return null;
            }

            if (!EventDueNextRaid(config))
            {
                return null;
            }

            var events = LoadEvents();
            var season = ((Season)_state.CurrentSeason).ToString();
            if (!EnsureActiveEvent(events))
            {
                return null;
            }

            var def = events[_state.ActiveEvent!];
            double? temp = def.Temp is { } range ? range.Min + Random.Shared.NextDouble() * (range.Max - range.Min) : null;
            if (_eventApplied.Add(sessionId))
            {
                logger.Info($"[Season Rotator] Weather event '{_state.ActiveEvent}' for this raid ({season}, {_state.EventRaids.Count} due this season)");
            }

            // copies: the forecast list is the server's cache, which must keep the normal weather for later raids
            return forecast.Select(w => w with
            {
                Fog = def.Fog ?? w.Fog,
                Rain = def.Rain ?? w.Rain,
                RainIntensity = def.RainIntensity ?? w.RainIntensity,
                WindSpeed = def.WindSpeed ?? w.WindSpeed,
                WindGustiness = def.WindGustiness ?? w.WindGustiness,
                Cloud = def.Cloud ?? w.Cloud,
                Pressure = def.Pressure ?? w.Pressure,
                Temperature = temp ?? w.Temperature,
            }).ToList();
        }
    }

    private void InstallEventPatch()
    {
        try
        {
            // the method isn't virtual, hence Harmony (the server's own HarmonyX)
            new Harmony("com.turbodestroyer.seasonrotator.events").Patch(
                AccessTools.Method(typeof(WeatherController), nameof(WeatherController.GenerateLocal)),
                postfix: new HarmonyMethod(typeof(WeatherEventPatches), nameof(WeatherEventPatches.GenerateLocalPostfix)));
        }
        catch (Exception e)
        {
            logger.Error($"[Season Rotator] Could not patch the raid weather, weather events are off: {e}");
        }
    }

    private void LogStatus(ModConfig config)
    {
        var length = LengthOf(config, _state.CurrentSeason);
        logger.Info($"[Season Rotator] {(Season)_state.CurrentSeason}: raid {_state.RaidsInSeason}/{length} completed " +
                    $"(lifetime raids: {_state.TotalRaids})" +
                    (config.FactoryRaidsPerAdvance > 1
                        ? $", factory raids toward next advance: {_state.FactoryRaidsSinceAdvance}/{config.FactoryRaidsPerAdvance}"
                        : string.Empty));
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

// Hooks the start-of-raid request (to learn the map) and the end-of-raid request so every finished raid is counted.
// NOTE: RouteAction / StaticRouter signatures can differ slightly between SPT 4.x builds -
// if this doesn't compile, check the signature against another 4.1 mod that uses StaticRouter.
[Injectable]
public class RaidCounterRouter(JsonUtil jsonUtil, ChangeSeason changeSeason) : StaticRouter(jsonUtil,
[
    new RouteAction<StartLocalRaidRequestData>(
        "/client/match/local/start",
        async (url, info, sessionId, output, _) =>
        {
            changeSeason.RegisterRaidStarted(sessionId.ToString(), info.Location, info.PlayerSide);
            return output!;
        }),
    new RouteAction<EndLocalRaidRequestData>(
        "/client/match/local/end",
        async (url, info, sessionId, output, _) =>
        {
            changeSeason.RegisterRaidFinished(sessionId.ToString());
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

    // Factory (day and night) raids only count toward the season every N factory raids.
    // 1 (default) = disabled, every raid counts.
    public int FactoryRaidsPerAdvance { get; set; } = 1;

    // true (default) = scav raids count toward the season like any other raid; false = only PMC raids do.
    public bool ScavRaidsAdvanceSeason { get; set; } = true;

    // Weather events ("Events" in customWeather.json) per full season (autumn and spring count as one, not per half):
    // 0 = none, 1 = once, 2 = twice... on raids picked at random. Needs UseCustomWeather. A change applies from the next season.
    public int EventsPerSeason { get; set; } = 1;

    // true = the number of events each season is random, from 0 up to EventsPerSeason
    public bool RandomEventCount { get; set; }

    // true (default) = Jaeger messages you after the raid before a weather event raid, saying what is coming
    public bool AnnounceEvents { get; set; } = true;
}

public class SeasonStatus
{
    public string Season { get; set; } = string.Empty;
    public int SeasonLength { get; set; }
    public int RaidsInSeason { get; set; }
    public int TotalRaids { get; set; }
    public int FactoryRaidsSinceAdvance { get; set; }
    public int FactoryRaidsPerAdvance { get; set; }
    public string NextSeason { get; set; } = string.Empty;
    public List<string> SeasonOrder { get; set; } = [];
}

public class ModState
{
    public int CurrentSeason { get; set; }
    public int RaidsInSeason { get; set; }
    public int TotalRaids { get; set; }
    // Factory raids played since the last one that counted toward the season
    public int FactoryRaidsSinceAdvance { get; set; }
    // Season group the event plan below is for, the group raid numbers (1 = first counted raid of the group) still due an
    // event, and the event picked for the next one
    public string? EventGroup { get; set; }
    public List<int> EventRaids { get; set; } = [];
    public string? ActiveEvent { get; set; }
    // Jaeger's warning for the next event raid was already sent
    public bool EventAnnounced { get; set; }
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

// One entry of "Events" in customWeather.json. A missing value keeps the normal weather's value.
public class WeatherEventDef
{
    // season names this event can happen in (WINTER, SUMMER, AUTUMN, AUTUMN_LATE, SPRING, SPRING_EARLY)
    public List<string> Seasons { get; set; } = [];
    // how likely it is picked among the events that fit the season
    public double Weight { get; set; } = 1;
    // Jaeger's message before the raid (optional, there is a generic one)
    public string? Announcement { get; set; }
    public double? Fog { get; set; }
    public double? Rain { get; set; }
    public double? RainIntensity { get; set; }
    public double? WindSpeed { get; set; }
    public double? WindGustiness { get; set; }
    public double? Cloud { get; set; }
    public double? Pressure { get; set; }
    public EventRange? Temp { get; set; }
}

public class EventRange
{
    public double Min { get; set; }
    public double Max { get; set; }
}

public static class WeatherEventPatches
{
    // The weather the client gets when a raid starts: swap in the event when this raid has one
    public static void GenerateLocalPostfix(MongoId sessionId, GetLocalWeatherResponseData __result)
    {
        try
        {
            var withEvent = ChangeSeason.Instance?.ApplyEvent(sessionId.ToString(), __result.Weather);
            if (withEvent is not null)
            {
                __result.Weather = withEvent;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Season Rotator] Weather event failed: {e}");
        }
    }
}

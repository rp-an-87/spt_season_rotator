# spt_season_rotator
Season Rotator

## Config (`config.json`)

| Key | Effect |
|---|---|
| `SeasonsLengths` | Raids each season lasts, keyed by season name (`WINTER`, `SPRING`, ...). `0` skips a season. |
| `UseCustomWeather` | Apply the weather presets from `customWeather.json`. |
| `FactoryRaidsPerAdvance` | Factory (day and night share one counter) raids only count toward the season once every N factory raids. `1` (default) = off, every raid counts. Other maps are unaffected and lifetime raids still count every raid. |
| `ScavRaidsAdvanceSeason` | `true` (default): scav raids count toward the season like any other raid. `false`: only PMC raids count (scav raids still count in lifetime raids). |
| `EventsPerSeason` | Weather events per full season: `0` = none, `1` = once (default), `2` = twice... Autumn (`AUTUMN` + `AUTUMN_LATE`) and spring (`SPRING_EARLY` + `SPRING`) count as one season, so they share the events instead of getting them per half. The raids are picked at random when the season starts and saved in `state.json`. Needs `UseCustomWeather`. A change applies from the next season. |
| `RandomEventCount` | `true`: the number of events each season is random, from 0 up to `EventsPerSeason`. Default `false`. |
| `AnnounceEvents` | `true` (default): after the raid before an event raid, Jaeger messages you what weather is coming (`announcement` text per event in `customWeather.json`, with a generic fallback). Sent once per event; the event type is picked at that moment so the message always matches. |

## Weather events (`Events` in `customWeather.json`)

A weather event replaces the weather of one raid (the whole forecast the raid receives). Each event lists the `seasons` it can happen in and a `weight` (chance to be picked among the events that fit the season), plus any of `fog`, `rain`, `rainIntensity`, `windSpeed`, `windGustiness`, `cloud`, `pressure` and `temp` (`min`/`max`). A value left out keeps the normal weather's value. Shipped: Dense fog, Storm, Blizzard (winter only), Gale, Gloom, Misty rain. An optional `announcement` text is what Jaeger says before the raid. The values stay inside what the game's own test weather uses (wind up to 4, fog up to 0.03, clouds up to 1.3).

An event is used up when the raid it was on finishes, so leaving before the raid starts does not waste it. Factory, Labs and Labyrinth raids (no outdoor weather) never get one, and scav raids don't either when `ScavRaidsAdvanceSeason` is `false`; the event waits for the next raid that can. The server log says when events are planned, applied and finished. It uses a Harmony patch on `WeatherController.GenerateLocal`.

## Client companion (`Client\`)

A small BepInEx plugin (`SeasonRotator.Client`, net471, goes in `BepInEx\plugins\SeasonRotator.Client\`). **Install it for fog events to be visible.** The server sends the event's fog and the game receives it, but in normal weather the game only gives the classic fog renderer its density while the weather debug mode (the Time & Weather Changer panel) is on, so the fog stayed invisible. The plugin does the same assignment in normal weather (a postfix on `WeatherController.ControlScattering`). Settings (F12, restart needed):

- `1. Weather / 1. Draw server fog in normal weather` (default on): affects all fog, natural fog too.
- `2. Logging / 1. Log raid weather` (default on): writes to the BepInEx log the first weather node the game receives at raid start and, six times 5 seconds apart, the fog, rain and clouds it is drawing. The server log has a matching `Event weather sent` line.

**F12 controls** (section `3. Control`, needs the server mod running): a status line, a season picker with a **Set season** button, a **Skip to the next season** button and an event picker with a **Force event** button. Pick with the `<` `>` arrows, then press the button next to the choice; a message confirms it.

- **Set season** starts that season now with its raid counter at 0, like a skip (a season switched off with length 0 ends after its first raid).
- **Force event** puts the event on your next outdoor raid (not Factory, Labs, Labyrinth). It is an extra: the planned events of the season are not used up and still happen, it ignores the `seasons` list of the event, and Jaeger sends no message. The event list is `Events` in `customWeather.json`, read by the server. The forced event is saved in `state.json` (`ForcedEvent`) and cleared when the raid it was used on ends.
- Server routes used: `GET /seasonrotator/info` and `POST /seasonrotator/do` (`{"action": "season" | "skip" | "event", "value": ...}`).

Build: `dotnet build Client\SeasonRotator.Client.csproj -c Release`; `SptDir` goes in `Client\SeasonRotator.Client.csproj.user`. Only the fog line is fixed so far; the other event values (rain, wind, temperature) still need checking in game.

## Web page

The mod has its own page in the server's web UI (card "SeasonRotator (raid-based)" under Mod pages, or `/season-rotator`, admin only). It shows the current season, the raid counter, the next season and the factory counter, edits the settings above (applied right away on save) and has a button to skip to the next season. The same `config.json` is also listed in the generic config editor (`/configs`).

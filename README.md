# spt_season_rotator
Season Rotator

## Config (`config.json`)

| Key | Effect |
|---|---|
| `SeasonsLengths` | Raids each season lasts, keyed by season name (`WINTER`, `SPRING`, ...). `0` skips a season. |
| `UseCustomWeather` | Apply the weather presets from `customWeather.json`. |
| `FactoryRaidsPerAdvance` | Factory (day and night share one counter) raids only count toward the season once every N factory raids. `1` (default) = off, every raid counts. Other maps are unaffected and lifetime raids still count every raid. |
| `ScavRaidsAdvanceSeason` | `true` (default): scav raids count toward the season like any other raid. `false`: only PMC raids count (scav raids still count in lifetime raids). |

## Web page

The mod has its own page in the server's web UI (card "SeasonRotator (raid-based)" under Mod pages, or `/season-rotator`, admin only). It shows the current season, the raid counter, the next season and the factory counter, edits the settings above (applied right away on save) and has a button to skip to the next season. The same `config.json` is also listed in the generic config editor (`/configs`).

# spt_season_rotator
Season Rotator

## Config (`config.json`)

| Key | Effect |
|---|---|
| `SeasonsLengths` | Raids each season lasts, keyed by season name (`WINTER`, `SPRING`, ...). `0` skips a season. |
| `UseCustomWeather` | Apply the weather presets from `customWeather.json`. |
| `FactoryRaidsPerAdvance` | Factory (day and night share one counter) raids only count toward the season once every N factory raids. `1` (default) = off, every raid counts. Other maps are unaffected and lifetime raids still count every raid. |
| `ScavRaidsAdvanceSeason` | `true` (default): scav raids count toward the season like any other raid. `false`: only PMC raids count (scav raids still count in lifetime raids). |

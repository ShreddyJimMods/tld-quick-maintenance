# QuickMaintenance

A mod for **The Long Dark** that makes one click of **Sharpen** or **Clean** take an item as far as it will go, instead of clicking the same action over and over.

It is fair about it: the game time, the wear on your whetstone or gun cleaning kit, and the skill rolls are all multiplied to match the separate clicks it replaces. You save clicks, not resources.

## Requirements

- The Long Dark 2.60 (Steam, PC)
- [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.2

No other mods are needed.

## Install

1. Install MelonLoader into the game folder if you have not already, and start the game once.
2. Download `QuickMaintenance.dll` from the [Releases](https://github.com/ShreddyJimMods/tld-quick-maintenance/releases) page.
3. Put it in `TheLongDark/Mods/`.

## Settings

Settings live in `TheLongDark/UserData/MelonPreferences.cfg` under `[QuickMaintenance]` (the file is created after the first launch):

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | One click sharpens or cleans as far as possible. |
| `ScaleTime` | `true` | Game time passes for every step that was combined. `false` = the time of a single step. |
| `ScaleToolWear` | `true` | The whetstone or cleaning kit wears for every step that was combined. `false` = the wear of a single step. |
| `ProgressBarSeconds` | `3` | Real seconds the progress bar takes. `0` leaves the game's own value. |
| `Verbose` | `false` | Writes what the mod did to the MelonLoader log. Turn on when reporting a problem. |

## Notes

- If the game does not accept the combined values for some item, the mod falls back to normal one-step behaviour for that click.
- Uninstalling is safe: the mod stores nothing in your save.

## Building from source

Needs the .NET SDK (6.0 or newer) and a copy of the game with MelonLoader installed and run once.

```
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\TheLongDark"
```

Instead of `-p:GameDir=...` you can create a `GameDir.props` file next to the project (it is git-ignored):

```xml
<Project>
  <PropertyGroup>
    <GameDir>/path/to/TheLongDark</GameDir>
  </PropertyGroup>
</Project>
```

The DLL is written to `bin/Release/net6.0/QuickMaintenance.dll`.

## License

MIT. See [LICENSE](LICENSE).

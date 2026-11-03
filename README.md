# QuestItemESP (quest item ESP)

In-raid markers for quest items, quest zones, keys and your own exits. Drawn only when it makes sense: not in the inventory, not after death, not while aiming through a scope.

## How this mod was made (AI disclosure)

This mod was **programmed by Claude**, an AI assistant made by Anthropic.
**Kaan was the tester**: Kaan has no programming background, played with the mod
on SPT 4.1.6, reported what went wrong, and Claude fixed it. No human programmer
has reviewed the code. It is open source so anyone can check it — if you find a
bug, please open an [issue](https://github.com/Kaanori/spt-questitemesp/issues).

## Requirements

- SPT 4.1.6 (EFT 0.16.9). BepInEx ships with SPT.

## Installation

1. Download `QuestItemESP-2.1.0.zip` from the [Releases](https://github.com/Kaanori/spt-questitemesp/releases) page.
2. Extract it **into your SPT game folder** (the folder that contains `EscapeFromTarkov.exe`).
   The ZIP already has the right layout; the mod ends up in `BepInEx/plugins/`.
   On Windows, make sure "Extract All" does not add an extra subfolder.
3. Restart the game.

## Uninstall

Delete the mod's files from `BepInEx/plugins/` (settings: `BepInEx/config/` for client mods) and restart the game.

## Categories (individually switchable)

- Quest items (with quest flag) and items required by quests
- Highlight items to bring, show only the quantities still needed
- Quest zones, optionally without a distance limit
- Keys and keycards (optionally only missing ones), keys at the destination
- My exits
- also search in containers and corpses

## Settings

F12 → QuestItemESP: toggle key, maximum distance, update interval, categories, colours.

## Origin

Icons, colour scheme and the "when to draw" logic come from **QuestMarkers** by *maschine* (MIT). See [LICENSES.md](THIRD-PARTY-NOTICES.md).

> **For SPT only (offline, bots only).** Never use in the real Escape from Tarkov.

## Building from source

Needs the .NET 10 SDK and an SPT 4.1.6 installation to build against.

```bash
./build.sh             # Linux: build only
./build.sh --install   # build and copy into the game folder
```
```powershell
.\build.ps1            # Windows
.\build.ps1 -Install
```

The game folder is set in `Directory.Build.props` (Windows default `C:/SPT`)
or via the environment variable `SPT_DIR`.

## Support

Bugs and questions: [GitHub issues](https://github.com/Kaanori/spt-questitemesp/issues).

## License

MIT — see [LICENSE](LICENSE).

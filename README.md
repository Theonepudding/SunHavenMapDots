# Sun Haven Map Dots

A BepInEx 5 plugin for **Sun Haven** that restores the missing player dot on the world map and adds **colour-coded dots for every player in multiplayer**, tracking each player's position accurately in real time.

## Features

- Fixes the missing / broken local-player dot on the world map (M key) introduced in Sun Haven 3.0+
- Shows a **yellow dot** for your own character
- In multiplayer: shows a uniquely-coloured dot for each other player **in the same area** (different colours per player slot)
- Optional **name labels** above each remote-player dot
- Dots update every frame while the map is open — no lag or drift
- All settings are configurable via the in-game Configuration Manager or the `BepInEx/config` folder

## Player Colours

| Slot | Player | Colour |
|------|--------|--------|
| 1 | You (local) | Yellow |
| 2 | Player 2 | Cyan |
| 3 | Player 3 | Orange |
| 4 | Player 4 | Lime |
| 5 | Player 5 | Purple |
| 6 | Player 6 | Pink |

## Requirements

- **BepInEx 5.x (x64)** for Sun Haven — [get the pack here](https://thunderstore.io/c/sun-haven/p/BepInEx/BepInExPack/)
- Sun Haven 3.0 or newer

## Installation

1. Install BepInEx 5 for Sun Haven if you haven't already.
2. Download `SunHavenMapDots.dll` from the [Releases](../../releases) page.
3. Copy it to:
   ```
   Sun Haven/BepInEx/plugins/SunHavenMapDots/SunHavenMapDots.dll
   ```
4. Launch the game. You should see a line in the BepInEx console confirming the mod loaded.

## Configuration

After the first launch a config file is created at:

```
Sun Haven/BepInEx/config/horo.sunhaven.mapdots.cfg
```

| Key | Default | Description |
|-----|---------|-------------|
| `DotSize` | `10` | Radius of each dot in map pixels (4–32) |
| `ShowLocalPlayer` | `true` | Show/hide your own dot |
| `ShowRemotePlayers` | `true` | Show/hide remote-player dots |
| `ShowPlayerNames` | `true` | Show name labels above remote dots |

If you have [ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) installed you can change these in-game with **F1**.

## Building from Source

Requirements: .NET SDK 4.8 + Sun Haven installed at the default Steam path.

```bash
git clone https://github.com/horohror/SunHavenMapDots.git
cd SunHavenMapDots/SunHavenMapDots
dotnet build -c Release
```

The post-build step automatically copies the DLL into `BepInEx/plugins/SunHavenMapDots/`.

If your game is installed elsewhere, pass the path:

```bash
dotnet build -c Release /p:GamePath="D:\Games\Sun Haven"
```

## How It Works

The plugin patches `Wish.Map.UpdatePlayerImagePosition` (the method the game calls every frame to move the local-player icon on the map). In the Postfix:

1. **Local player** — ensures the existing game-managed `Image` is visible and tinted yellow.
2. **Remote players** — iterates `NetworkLobbyManager.Instance.GamePlayers`, filters to the same scene, and calls the game's own `Map.GetPlayerPosition` + `Map.SetImagePosition` to place a coloured dot at the correct world-space coordinate. Dots are created as child `GameObject`s of the map's content `RectTransform` and are destroyed when players leave.

## License

MIT — do whatever you like, credit appreciated.

<h1 align="center">Buddy Beacon</h1>

<p align="center">
  <em>Find your friends. Join your party. Adventure together.</em>
</p>

<p align="center">
  <a href="https://mods.vintagestory.at/buddybeacon"><img src="https://img.shields.io/badge/Vintage%20Story-Mod%20DB-green" alt="Mod DB"></a>
  <img src="https://img.shields.io/badge/version-0.1.5-blue" alt="Version">
  <img src="https://img.shields.io/badge/multiplayer-required-orange" alt="Multiplayer">
</p>

---

A lightweight multiplayer mod for staying connected with friends. Originally built so couples and small groups can adventure together without constantly losing each other.

**Use what you need** - each feature works independently.

---

## Party System

<p align="center">
  <img src="docs/images/party-hud.png" alt="Party HUD showing two players">
</p>

Group up with friends and always know where they are, how they're doing, and which direction to go.

**The Party HUD shows:**
- **Distance** - How far away each member is
- **Direction Compass** - Arrow pointing toward each party member
- **Health Bar** - Monitor your friends' HP at a glance
- **Food Bar** - Know when someone needs to eat
- **Minimap Pins** - Party members appear on your minimap

### Size Options

Toggle between compact and expanded views with the `a` / `A` buttons.

<p align="center">
  <img src="docs/images/party-large.png" alt="Large party view" width="500">
</p>

### Party Management

Right-click a party member for options:

<p align="center">
  <img src="docs/images/party-options.png" alt="Party member options">
</p>

- **Change Color** - Customize each member's accent color
- **Kick** - Remove a member (leader only)
- **Make Lead** - Transfer leadership

<p align="center">
  <img src="docs/images/party-color.png" alt="Color picker">
</p>

### Invites

Click `+` to invite players. They'll receive a prompt with options to accept, decline, or temporarily silence invites.

<p align="center">
  <img src="docs/images/party-invite.png" alt="Party invite dialog">
</p>

### Offline Handling

Party persists when members disconnect - they'll show as offline until they return.

<p align="center">
  <img src="docs/images/party-offline.png" alt="Offline party member">
</p>

---

## Beacon Band + Map Tracking

A simple bracelet that links players together for map visibility.

- **Right-click** to set a beacon code (any word or phrase)
- Players with **matching codes** see each other on the **world map**
- Green markers show friend locations with **distance**
- **Ping locations** to share points of interest
- Just keep it in your inventory - no need to wear it

Great for larger groups or guilds where a formal party isn't needed.

---

## Server Admin Tools

Optional items for servers with distant spawn points (5k+ blocks apart). Useful for letting new players immediately join friends.

| Item | Effect |
|------|--------|
| **Wayfinder's Compass** | Teleport TO a friend (consumed on use) |
| **Hero's Call Stone** | Summon a friend to YOU (consumed on use) |

*Tip: Provide these at spawn along with a temporal gear so new players can immediately reach friends and bind nearby.*

---

## Installation

1. Download from [Mod DB](https://mods.vintagestory.at/buddybeacon)
2. Drop the `.zip` in your `VintagestoryData/Mods` folder
3. Restart the game

---

## Configuration

Server admins can customize in `ModConfig/VSBuddyBeacon.json`:

<details>
<summary><b>Config Options</b></summary>

| Option | Default | Description |
|--------|---------|-------------|
| `BeaconUpdateInterval` | 1.0 | Seconds between position updates |
| `MaxBeaconGroupSize` | 10 | Max players per beacon code (0 = unlimited) |
| `EnableMapPings` | true | Allow map ping feature |
| `EnableDistanceLod` | true | Reduce update frequency for distant players |
| `HealthDataMode` | OnChange | When to send health: `Always`, `OnChange`, `Never` |
| `HealthChangeThreshold` | 1.0 | Min HP change to trigger update |
| `SaturationChangeThreshold` | 25 | Min hunger change to trigger update |

</details>

---

<p align="center">
  <sub>Made for adventuring with the people you care about</sub>
</p>

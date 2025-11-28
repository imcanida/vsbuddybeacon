# VS Buddy Beacon - Project Context

## Project Overview

**VS Buddy Beacon** is a multiplayer mod for Vintage Story (v1.19.0+) that implements a player-to-player teleportation and tracking system.

### Core Features
- **Teleportation:** Players can teleport to friends or summon them with consent using consumable items
- **Beacon Tracking:** Players sharing a beacon code can see each other's positions on a compass HUD
- **Request/Response Protocol:** 30-second timeout system with proper consent validation

## Technology Stack

- **Language:** C# (.NET 8.0, C# 12)
- **Game Engine:** Vintage Story API (Client, Server, Common)
- **Networking:** Protocol Buffers (protobuf-net) for packet serialization
- **UI/Graphics:** Cairo graphics library (cairo-sharp) for custom HUD rendering
- **Build:** .NET 8.0 with auto-packaging to Vintage Story Mods folder

## Codebase Structure

```
src/
├── VSBuddyBeaconModSystem.cs    # Core mod system (~500 lines)
│                                 # Server: Request management, beacon tracking, validation
│                                 # Client: UI management, network handling
├── Items/
│   ├── ItemWayfinderCompass.cs  # Teleport TO player (consumable)
│   ├── ItemHerosCallStone.cs    # Summon player TO you (consumable)
│   └── ItemBeaconBand.cs        # Set beacon code (reusable)
├── Network/
│   ├── TeleportPackets.cs       # 6 packet types for teleportation protocol
│   └── BeaconPackets.cs         # 2 packet types for position sharing
└── GUI/
    ├── HudElementBuddyCompass.cs        # Persistent compass HUD (top-right)
    ├── GuiDialogPlayerSelect.cs         # Player selection dropdown
    ├── GuiDialogTeleportPrompt.cs       # Accept/decline prompt
    └── GuiDialogBeaconCode.cs           # Beacon code entry dialog

assets/vsbuddybeacon/
├── itemtypes/        # JSON definitions for items
├── recipes/          # Crafting recipes
└── lang/en.json     # Localization strings

VSBuddyBeacon.csproj  # Build config with post-build packaging
modinfo.json          # Mod metadata
```

## Key Components Deep Dive

### VSBuddyBeaconModSystem.cs
**The orchestrator** - Handles both server and client logic:

**Server-side:**
- Manages `pendingTeleportRequests` dictionary (30s timeout)
- Tracks `playerBeaconCodes` for position sharing
- Broadcasts beacon positions every 1 second to linked players
- Validates inventory for teleport items before consumption
- Handles cleanup on player disconnect

**Client-side:**
- Manages all GUI dialog instances
- Processes network responses and shows chat messages
- Updates HudElementBuddyCompass with buddy positions

### Network Architecture
- Custom channel: `"vsbuddybeacon"`
- All packets use `[ProtoContract]` and `[ProtoMember]` attributes
- Bidirectional: Client→Server requests, Server→Client broadcasts
- Types: Request, Response, Result, Prompt, List, BeaconCode, BeaconPosition

### Item System
- Items extend `Item` base class from Vintage Story API
- Override `OnHeldInteractStart()` for right-click behavior
- Items must be in player inventory for server validation
- Consumable items have `maxstacksize: 64`, non-consumable have `maxstacksize: 1`

### GUI Patterns
- Dialogs extend `GuiDialog` from Vintage Story API
- Use `SingleComposer` pattern for UI building
- Modal dialogs with `EnumDialogType.Dialog`
- HUD uses `EnumDialogType.HUD` and Cairo rendering

## How to Get Context When Working Here

### For UI/Dialog Changes
**Read these files:**
```
src/GUI/GuiDialog[ComponentName].cs
assets/vsbuddybeacon/lang/en.json (for text labels)
```

### For Network Protocol Changes
**Read these files:**
```
src/Network/[Teleport|Beacon]Packets.cs
src/VSBuddyBeaconModSystem.cs (look for RegisterChannel and packet handlers)
```

### For Item Behavior Changes
**Read these files:**
```
src/Items/Item[Name].cs
assets/vsbuddybeacon/itemtypes/[itemname].json
assets/vsbuddybeacon/recipes/[itemname].json (if modifying crafting)
```

### For Core Teleportation Logic
**Read these files:**
```
src/VSBuddyBeaconModSystem.cs (HandleTeleportRequest, HandleTeleportResponse)
src/Network/TeleportPackets.cs (packet definitions)
```

### For Beacon Tracking System
**Read these files:**
```
src/VSBuddyBeaconModSystem.cs (HandleBeaconCodeSet, BroadcastBeaconPositions)
src/GUI/HudElementBuddyCompass.cs (rendering logic)
src/Network/BeaconPackets.cs
```

## Important Conventions

### Code Style
- Use Vintage Story API naming conventions (PascalCase for public members)
- Server-side validation is REQUIRED for all player actions
- Always check inventory before consuming items
- Clean up resources on player disconnect

### Network Protocol
- All packets must be ProtoContract serializable
- Server is authoritative - never trust client data
- Always send feedback messages to clients (success/failure)
- Timeout long-running requests (current: 30 seconds)

### UI/UX
- Show clear feedback messages via `capi.ShowChatMessage()`
- Dialogs should be modal for important decisions
- Include timeout warnings in prompts
- Keep HUD elements non-intrusive (top-right, small text)

### Testing Approach
- Test with multiple players (minimum 2 for beacon features)
- Test edge cases: player disconnect mid-request, inventory full, etc.
- Verify item consumption happens only on success
- Check position updates are smooth and performant

## Common Tasks

### Adding a New Item Type
1. Create class in `src/Items/Item[Name].cs` extending `Item`
2. Add JSON definition in `assets/vsbuddybeacon/itemtypes/[name].json`
3. Add crafting recipe in `assets/vsbuddybeacon/recipes/[name].json`
4. Add localization strings in `assets/vsbuddybeacon/lang/en.json`
5. Register network handlers if needed in `VSBuddyBeaconModSystem.cs`

### Adding a New Network Packet
1. Define packet class in `src/Network/[Category]Packets.cs` with ProtoContract
2. Register channel handler in `VSBuddyBeaconModSystem.Start[Client|Server]Side()`
3. Implement handler method (server or client)
4. Send packet using `serverChannel.SendPacket()` or `clientChannel.SendPacket()`

### Adding a New Dialog
1. Create class in `src/GUI/GuiDialog[Name].cs` extending `GuiDialog`
2. Implement `ComposeDialog()` using `SingleComposer` pattern
3. Add callbacks for button actions
4. Instantiate in `VSBuddyBeaconModSystem` client-side
5. Add localization strings in `en.json`

## Build & Deployment

**Build command:**
```bash
dotnet build
```

**Post-build:** Automatically packages mod as ZIP and copies to:
```
%APPDATA%\VintagestoryData\Mods\VSBuddyBeacon.zip
```

**Manual packaging:**
The build creates a proper mod structure with `modinfo.json`, compiled DLL, and assets folder.

## Getting Started for New Agents

When working on this project:

1. **Understand the feature area** - Read the relevant section above
2. **Read the specific files** - Use the "How to Get Context" guide
3. **Check existing patterns** - Look at similar components for consistency
4. **Test thoroughly** - Especially server-client interactions and edge cases
5. **Validate on server** - Never trust client input, always validate server-side

For general exploration, use the Explore agent to understand code flow and dependencies.
For specific file searches, use Grep/Glob with the patterns from the structure above.

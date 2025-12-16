using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VSBuddyBeacon.Config;

namespace VSBuddyBeacon
{
    public class VSBuddyBeaconModSystem : ModSystem
    {
        public const string ChannelName = "vsbuddybeacon";
        private const float REQUEST_TIMEOUT_SECONDS = 30f;
        private const float BEACON_UPDATE_INTERVAL = 1.0f;
        private const float SILENCE_DURATION_MINUTES = 10f;

        // Server-side: Track pending requests
        private Dictionary<long, PendingTeleportRequest> pendingRequests = new();
        private long nextRequestId = 1;

        // Server-side: Track player beacon codes (PlayerUID -> BeaconCode)
        private Dictionary<string, string> playerBeaconCodes = new();

        // Server-side: Track silenced players (PlayerUID -> (SilencedUID -> ExpirationTime))
        private Dictionary<string, Dictionary<string, long>> silencedPlayers = new();

        // Server-side: Track request counts (TargetUID -> (RequesterUID -> Count))
        private Dictionary<string, Dictionary<string, int>> requestCounts = new();

        // Server-side: Track ping timestamps for rate limiting (PlayerUID -> List of ping times)
        private Dictionary<string, List<long>> pingTimestamps = new();
        private const int MAX_PINGS_PER_WINDOW = 3;
        private const float PING_WINDOW_SECONDS = 10f;

        // Server-side: Party tracking
        private Dictionary<long, Party> parties = new();
        private Dictionary<string, long> playerPartyMap = new();  // playerUid -> partyId
        private long nextPartyId = 1;
        private Dictionary<long, PendingPartyInvite> pendingPartyInvites = new();
        private long nextPartyInviteId = 1;
        private Dictionary<string, Dictionary<string, int>> partyInviteCounts = new();  // TargetUID -> (InviterUID -> Count)

        // Server-side: Track last-sent beacon data for delta compression
        // Key: "recipientUid:buddyUid", Value: last sent state
        private Dictionary<string, LastSentBuddyState> lastSentStates = new();
        // Players that need full state sync on next update (joined, reconnected, party changed)
        private HashSet<string> playersNeedingFullSync = new();

        // Client-side references
        private ICoreClientAPI capi;
        private GuiDialogPlayerSelect playerSelectDialog;
        private GuiDialogTeleportPrompt teleportPromptDialog;
        private GuiDialogBeaconCode beaconCodeDialog;
        private GuiDialogPartyInvitePrompt partyInvitePromptDialog;
        private HudElementPartyList partyListHud;
        private HudElementBuddyCompass buddyCompassHud;
        private BuddyMapLayer buddyMapLayer;

        // Client-side: Party state received from server
        private PartyStatePacket currentPartyState = null;

        // Client-side: Cached buddy positions for staleness fade-out
        // When someone goes offline, their position stays here and fades via staleness
        private Dictionary<string, BuddyPositionWithTimestamp> cachedBuddyPositions = new();

        // Test mode for party invites
        private bool partyTestMode = false;
        private string fakePartyMemberName = null;
        private string fakePartyMemberUid = null;

        // Fake buddy testing
        private bool fakeBuddiesActive = false;
        private List<Vec3d> fakeBuddyPositions = null;
        private long fakeBuddyTickId = 0;

        // Server-side reference
        private ICoreServerAPI sapi;

        // Server-side: Track fake buddy entities for cleanup
        private List<long> fakeBuddyEntityIds = new List<long>();

        // Configuration
        private ModConfig config;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // Load configuration
            config = LoadConfig(api);

            // Always register item classes (they check config internally for behavior)
            api.RegisterItemClass("ItemWayfinderCompass", typeof(ItemWayfinderCompass));
            api.RegisterItemClass("ItemHerosCallStone", typeof(ItemHerosCallStone));
            api.RegisterItemClass("ItemBeaconBand", typeof(ItemBeaconBand));
        }

        /// <summary>
        /// Check if an item is enabled in config (for use by item classes)
        /// </summary>
        public bool IsItemEnabled(string itemCode)
        {
            return config?.IsItemEnabled(itemCode) ?? true;
        }

        /// <summary>
        /// Check if verbose logging is enabled (for use by GUI classes)
        /// </summary>
        public bool IsVerboseLoggingEnabled()
        {
            return config?.VerboseLogging ?? false;
        }

        /// <summary>
        /// Log a debug message (only if VerboseLogging is enabled)
        /// </summary>
        private void LogDebug(ICoreAPI api, string message)
        {
            if (config?.VerboseLogging ?? false)
            {
                api.Logger.Debug(message);
            }
        }

        /// <summary>
        /// Log an info/notification message (only if VerboseLogging is enabled)
        /// </summary>
        private void LogInfo(ICoreAPI api, string message)
        {
            if (config?.VerboseLogging ?? false)
            {
                api.Logger.Notification(message);
            }
        }

        private ModConfig LoadConfig(ICoreAPI api)
        {
            try
            {
                var config = api.LoadModConfig<ModConfig>("vsbuddybeacon.json");
                if (config == null)
                {
                    api.Logger.Notification("[VSBuddyBeacon] Creating default configuration file");
                    config = new ModConfig();
                    api.StoreModConfig(config, "vsbuddybeacon.json");
                }
                return config;
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VSBuddyBeacon] Failed to load config: {ex.Message}. Using defaults.");
                return new ModConfig();
            }
        }

        public override void AssetsFinalize(ICoreAPI api)
        {
            base.AssetsFinalize(api);

            // Configure recipes after assets are loaded (server-side only)
            if (api.Side == EnumAppSide.Server)
            {
                var serverApi = api as ICoreServerAPI;
                if (serverApi != null)
                {
                    ConfigureRecipes(serverApi);
                }
            }
        }

        #region Recipe Configuration

        private void ConfigureRecipes(ICoreServerAPI serverApi)
        {
            if (config?.Items == null || serverApi?.World?.GridRecipes == null)
            {
                serverApi?.Logger.Warning("[VSBuddyBeacon] ConfigureRecipes skipped - config or recipes not available");
                return;
            }

            LogInfo(serverApi, $"[VSBuddyBeacon] ConfigureRecipes starting. Total recipes before: {serverApi.World.GridRecipes.Count}");

            foreach (var itemKvp in config.Items)
            {
                string itemCode = itemKvp.Key;
                var itemConfig = itemKvp.Value;

                LogInfo(serverApi, $"[VSBuddyBeacon] Processing {itemCode}: Enabled={itemConfig.Enabled}, AllowCrafting={itemConfig.AllowCrafting}, HasCustom={itemConfig.CustomRecipe != null}");

                if (!itemConfig.Enabled || !itemConfig.AllowCrafting)
                {
                    // Item disabled or crafting disabled - remove recipe
                    LogInfo(serverApi, $"[VSBuddyBeacon] Removing recipe for {itemCode} (disabled or crafting disabled)");
                    RemoveRecipe(serverApi, itemCode);
                }
                else if (itemConfig.CustomRecipe != null)
                {
                    // Remove default, add custom
                    LogInfo(serverApi, $"[VSBuddyBeacon] Replacing recipe for {itemCode} with custom recipe");
                    RemoveRecipe(serverApi, itemCode);
                    AddCustomRecipe(serverApi, itemCode, itemConfig.CustomRecipe);
                }
                else
                {
                    LogInfo(serverApi, $"[VSBuddyBeacon] Keeping default recipe for {itemCode}");
                }
            }

            LogInfo(serverApi, $"[VSBuddyBeacon] ConfigureRecipes complete. Total recipes after: {serverApi.World.GridRecipes.Count}");
        }

        private void RemoveRecipe(ICoreServerAPI serverApi, string itemCode)
        {
            if (serverApi?.World?.GridRecipes == null) return;

            try
            {
                int removed = serverApi.World.GridRecipes.RemoveAll(r =>
                {
                    if (r?.Output == null) return false;
                    var resolvedPath = r.Output.ResolvedItemstack?.Item?.Code?.Path;
                    var unresolvedPath = r.Output.Code?.Path;
                    return resolvedPath == itemCode || unresolvedPath == itemCode;
                });

                if (removed > 0)
                    LogInfo(serverApi, $"[VSBuddyBeacon] Removed {removed} recipe(s) for {itemCode}");
            }
            catch (Exception ex)
            {
                serverApi.Logger.Warning($"[VSBuddyBeacon] Error removing recipe for {itemCode}: {ex.Message}");
            }
        }

        private void AddCustomRecipe(ICoreServerAPI serverApi, string itemCode, CustomRecipe recipeConfig)
        {
            try
            {
                var recipe = ParseCustomRecipe(serverApi, itemCode, recipeConfig);
                serverApi.World.GridRecipes.Add(recipe);
                LogInfo(serverApi, $"[VSBuddyBeacon] Added custom recipe for {itemCode}");
            }
            catch (Exception ex)
            {
                serverApi.Logger.Error($"[VSBuddyBeacon] Error loading custom recipe for {itemCode}: {ex.Message}");
            }
        }

        private GridRecipe ParseCustomRecipe(ICoreServerAPI serverApi, string itemCode, CustomRecipe config)
        {
            var recipe = new GridRecipe();

            recipe.Name = new AssetLocation("vsbuddybeacon", $"custom_{itemCode}");
            recipe.IngredientPattern = config.IngredientPattern;
            recipe.Width = config.Width;
            recipe.Height = config.Height;

            // Set up ingredients dictionary
            recipe.Ingredients = new Dictionary<string, CraftingRecipeIngredient>();
            foreach (var kvp in config.Ingredients)
            {
                var code = new AssetLocation(kvp.Value.Code);
                recipe.Ingredients[kvp.Key] = new CraftingRecipeIngredient
                {
                    Type = kvp.Value.Type == "item" ? EnumItemClass.Item : EnumItemClass.Block,
                    Code = code,
                    Name = kvp.Key,  // Required for serialization
                    Quantity = kvp.Value.Quantity,
                    AllowedVariants = kvp.Value.AllowedVariants
                };
            }

            // Set output
            recipe.Output = new CraftingRecipeIngredient
            {
                Type = EnumItemClass.Item,
                Code = new AssetLocation($"vsbuddybeacon:{itemCode}"),
                Name = "output",
                Quantity = config.Output.Quantity
            };

            // Build resolved ingredients array from pattern
            var patternRows = config.IngredientPattern.Split(',');
            recipe.resolvedIngredients = new GridRecipeIngredient[config.Width * config.Height];

            for (int row = 0; row < config.Height; row++)
            {
                string rowPattern = patternRows[row];
                for (int col = 0; col < config.Width; col++)
                {
                    char key = rowPattern[col];
                    if (key == ' ') continue;

                    if (!config.Ingredients.TryGetValue(key.ToString(), out var ingredientDef))
                        continue;

                    var code = new AssetLocation(ingredientDef.Code);
                    var ingredient = new GridRecipeIngredient
                    {
                        Type = ingredientDef.Type == "item" ? EnumItemClass.Item : EnumItemClass.Block,
                        Code = code,
                        Name = key.ToString(),
                        PatternCode = key.ToString(),  // Required for serialization
                        Quantity = ingredientDef.Quantity,
                        AllowedVariants = ingredientDef.AllowedVariants
                    };

                    // Resolve the ingredient to get ResolvedItemstack
                    if (ingredientDef.Type == "item")
                    {
                        var item = serverApi.World.GetItem(code);
                        if (item != null)
                        {
                            ingredient.ResolvedItemstack = new ItemStack(item, ingredientDef.Quantity);
                        }
                    }
                    else
                    {
                        var block = serverApi.World.GetBlock(code);
                        if (block != null)
                        {
                            ingredient.ResolvedItemstack = new ItemStack(block, ingredientDef.Quantity);
                        }
                    }

                    recipe.resolvedIngredients[row * config.Width + col] = ingredient;
                }
            }

            // Resolve output
            var outputItem = serverApi.World.GetItem(recipe.Output.Code);
            if (outputItem != null)
            {
                recipe.Output.ResolvedItemstack = new ItemStack(outputItem, config.Output.Quantity);
            }

            return recipe;
        }

        #endregion

        #region First Join Item Spawning

        private void OnPlayerFirstJoin(IServerPlayer player)
        {
            // Mark player as needing full sync (they have no cached state on their client)
            playersNeedingFullSync.Add(player.PlayerUID);

            // Also mark all existing players to send full data to this new player
            // (so this player gets everyone's current health immediately)
            foreach (var p in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (p.PlayerUID != player.PlayerUID)
                {
                    // Clear any cached state for this pair so new player gets fresh data
                    var keysToRemove = lastSentStates.Keys
                        .Where(k => k.StartsWith($"{player.PlayerUID}:"))
                        .ToList();
                    foreach (var key in keysToRemove)
                    {
                        lastSentStates.Remove(key);
                    }
                }
            }

            // Check if player is in a party and broadcast their online status
            if (playerPartyMap.TryGetValue(player.PlayerUID, out long partyId) && parties.TryGetValue(partyId, out var party))
            {
                LogInfo(sapi, $"[VSBuddyBeacon] {player.PlayerName} reconnected to party {partyId}");

                // If the original leader reconnects and someone else is acting leader, restore their leadership
                if (party.OriginalLeaderUid == player.PlayerUID && party.LeaderUid != player.PlayerUID)
                {
                    party.LeaderUid = player.PlayerUID;
                    LogInfo(sapi, $"[VSBuddyBeacon] Original leader {player.PlayerName} reclaimed leadership");
                }

                // Small delay to ensure player is fully initialized
                sapi.Event.RegisterCallback((dt) =>
                {
                    if (parties.ContainsKey(partyId))  // Party still exists
                    {
                        BroadcastPartyState(party);
                    }
                }, 500);
            }

            const string HAS_JOINED_KEY = "vsbuddybeacon:hasJoinedBefore";

            // Check if player has joined before
            byte[] data = player.GetModData<byte[]>(HAS_JOINED_KEY);
            if (data != null) return; // Not first join

            // Mark as joined
            player.SetModData(HAS_JOINED_KEY, new byte[] { 1 });

            // Give configured starter items
            foreach (var itemKvp in config.Items)
            {
                string itemCode = itemKvp.Key;
                var itemConfig = itemKvp.Value;

                if (itemConfig.Enabled && itemConfig.GiveOnFirstJoin)
                {
                    GiveItem(player, itemCode);
                }
            }
        }

        private void GiveItem(IServerPlayer player, string itemCode)
        {
            var item = sapi.World.GetItem(new AssetLocation("vsbuddybeacon", itemCode));
            if (item == null)
            {
                sapi.Logger.Warning($"[VSBuddyBeacon] Could not find item: {itemCode}");
                return;
            }

            var stack = new ItemStack(item, 1);
            bool success = player.InventoryManager.TryGiveItemstack(stack);

            if (success)
            {
                LogDebug(sapi, $"[VSBuddyBeacon] Gave {itemCode} to {player.PlayerName}");
            }
            else
            {
                sapi.Logger.Warning($"[VSBuddyBeacon] Could not give {itemCode} to {player.PlayerName} (inventory full?)");
            }
        }

        #endregion

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            // Register network channel
            api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<TeleportRequestPacket>()
                .RegisterMessageType<TeleportResponsePacket>()
                .RegisterMessageType<TeleportPromptPacket>()
                .RegisterMessageType<TeleportResultPacket>()
                .RegisterMessageType<PlayerListRequestPacket>()
                .RegisterMessageType<PlayerListResponsePacket>()
                .RegisterMessageType<BeaconCodeSetPacket>()
                .RegisterMessageType<BeaconPositionPacket>()
                .RegisterMessageType<SilencePlayerPacket>()
                .RegisterMessageType<MapPingPacket>()
                .RegisterMessageType<FakeBuddySpawnPacket>()
                .RegisterMessageType<FakeBuddyClearPacket>()
                .RegisterMessageType<BuddyChatPacket>()
                // Party packets
                .RegisterMessageType<PartyInvitePacket>()
                .RegisterMessageType<PartyInvitePromptPacket>()
                .RegisterMessageType<PartyInviteResponsePacket>()
                .RegisterMessageType<PartyStatePacket>()
                .RegisterMessageType<PartyLeavePacket>()
                .RegisterMessageType<PartyKickPacket>()
                .RegisterMessageType<PartyMakeLeadPacket>()
                .RegisterMessageType<PartyDisbandedPacket>()
                .RegisterMessageType<PartyInviteResultPacket>()
                .SetMessageHandler<TeleportRequestPacket>(OnTeleportRequestReceived)
                .SetMessageHandler<TeleportResponsePacket>(OnTeleportResponseReceived)
                .SetMessageHandler<PlayerListRequestPacket>(OnPlayerListRequest)
                .SetMessageHandler<BeaconCodeSetPacket>(OnBeaconCodeSet)
                .SetMessageHandler<SilencePlayerPacket>(OnSilencePlayer)
                .SetMessageHandler<MapPingPacket>(OnMapPingReceived)
                .SetMessageHandler<FakeBuddySpawnPacket>(OnFakeBuddySpawn)
                .SetMessageHandler<FakeBuddyClearPacket>(OnFakeBuddyClear)
                .SetMessageHandler<BuddyChatPacket>(OnBuddyChatReceived)
                // Party handlers
                .SetMessageHandler<PartyInvitePacket>(OnPartyInviteReceived)
                .SetMessageHandler<PartyInviteResponsePacket>(OnPartyInviteResponseReceived)
                .SetMessageHandler<PartyLeavePacket>(OnPartyLeaveReceived)
                .SetMessageHandler<PartyKickPacket>(OnPartyKickReceived)
                .SetMessageHandler<PartyMakeLeadPacket>(OnPartyMakeLeadReceived);

            // Configure recipes after assets load - will be called via AssetsFinalize override

            // Register tick handlers
            api.Event.RegisterGameTickListener(CheckRequestTimeouts, 1000);  // Every 1000ms
            api.Event.RegisterGameTickListener(CheckPartyInviteTimeouts, 1000);  // Every 1000ms
            // Use config interval, clamped to safe range (0.5s - 10s)
            float beaconInterval = Math.Clamp(config.BeaconUpdateInterval, 0.5f, 10f);
            api.Event.RegisterGameTickListener(BroadcastBeaconPositions, (int)(beaconInterval * 1000));

            // Clean up beacon codes when player disconnects
            api.Event.PlayerDisconnect += OnPlayerDisconnect;

            // Register commands
            api.ChatCommands.Create("buddy")
                .WithDescription("Buddy beacon management commands")
                .BeginSubCommand("unsilence")
                    .WithDescription("Remove a player from your silence list")
                    .WithArgs(api.ChatCommands.Parsers.Word("playername"))
                    .HandleWith(OnUnsilenceCommand)
                .EndSubCommand();

            // Give starter items on first join
            api.Event.PlayerJoin += OnPlayerFirstJoin;

            api.Logger.Notification("[VSBuddyBeacon] Server-side initialized");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            // Register network channel
            api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<TeleportRequestPacket>()
                .RegisterMessageType<TeleportResponsePacket>()
                .RegisterMessageType<TeleportPromptPacket>()
                .RegisterMessageType<TeleportResultPacket>()
                .RegisterMessageType<PlayerListRequestPacket>()
                .RegisterMessageType<PlayerListResponsePacket>()
                .RegisterMessageType<BeaconCodeSetPacket>()
                .RegisterMessageType<BeaconPositionPacket>()
                .RegisterMessageType<SilencePlayerPacket>()
                .RegisterMessageType<MapPingPacket>()
                .RegisterMessageType<FakeBuddySpawnPacket>()
                .RegisterMessageType<FakeBuddyClearPacket>()
                .RegisterMessageType<BuddyChatPacket>()
                // Party packets
                .RegisterMessageType<PartyInvitePacket>()
                .RegisterMessageType<PartyInvitePromptPacket>()
                .RegisterMessageType<PartyInviteResponsePacket>()
                .RegisterMessageType<PartyStatePacket>()
                .RegisterMessageType<PartyLeavePacket>()
                .RegisterMessageType<PartyKickPacket>()
                .RegisterMessageType<PartyMakeLeadPacket>()
                .RegisterMessageType<PartyDisbandedPacket>()
                .RegisterMessageType<PartyInviteResultPacket>()
                .SetMessageHandler<TeleportPromptPacket>(OnTeleportPromptReceived)
                .SetMessageHandler<TeleportResultPacket>(OnTeleportResultReceived)
                .SetMessageHandler<PlayerListResponsePacket>(OnPlayerListReceived)
                .SetMessageHandler<BeaconPositionPacket>(OnBeaconPositionsReceived)
                .SetMessageHandler<MapPingPacket>(OnMapPingReceived)
                .SetMessageHandler<BuddyChatPacket>(OnBuddyChatReceived)
                // Party handlers
                .SetMessageHandler<PartyInvitePromptPacket>(OnPartyInvitePromptReceived)
                .SetMessageHandler<PartyStatePacket>(OnPartyStateReceived)
                .SetMessageHandler<PartyDisbandedPacket>(OnPartyDisbandedReceived)
                .SetMessageHandler<PartyInviteResultPacket>(OnPartyInviteResultReceived);

            // Create buddy compass HUD and register it properly
            buddyCompassHud = new HudElementBuddyCompass(capi);
            capi.Gui.LoadedGuis.Add(buddyCompassHud);

            // Register buddy map layer with the world map
            var worldMapManager = api.ModLoader.GetModSystem<WorldMapManager>();
            if (worldMapManager != null)
            {
                buddyMapLayer = new BuddyMapLayer(api, worldMapManager);
                buddyMapLayer.PingsEnabled = config.EnableMapPings;
                worldMapManager.MapLayers.Add(buddyMapLayer);
                api.Logger.Notification("[VSBuddyBeacon] Buddy map layer registered");
            }

            // Create party list HUD and register hotkey (if enabled)
            if (config.EnablePartyList)
            {
                partyListHud = new HudElementPartyList(capi);

                // Load persisted settings
                partyListHud.LoadSettings(
                    config.PartyListScale,
                    new Dictionary<string, int>(config.PinnedPlayers),
                    (scale, pins) =>
                    {
                        config.PartyListScale = scale;
                        config.PinnedPlayers = pins;
                        capi.StoreModConfig(config, "vsbuddybeacon.json");
                    }
                );

                capi.Gui.LoadedGuis.Add(partyListHud);

                // Connect pinned players changes to map layer
                partyListHud.OnPinnedPlayersChanged = () =>
                {
                    var pinnedColors = partyListHud.GetPinnedPlayersWithColors();
                    buddyMapLayer?.UpdatePinnedPlayers(pinnedColors);
                };

                // Initial sync of persisted pins to map layer
                var initialPinnedColors = partyListHud.GetPinnedPlayersWithColors();
                buddyMapLayer?.UpdatePinnedPlayers(initialPinnedColors);

                capi.Input.RegisterHotKey("partylist", "Toggle Party List", GlKeys.P, HotkeyType.GUIOrOtherControls);
                capi.Input.SetHotKeyHandler("partylist", (keyComb) =>
                {
                    partyListHud.ToggleVisibility();
                    string status = partyListHud.IsVisible ? "shown" : "hidden";
                    capi.ShowChatMessage($"[BuddyBeacon] Party list {status}");
                    return true;
                });
            }

            // Register /psay and /gsay chat commands for beacon chat
            api.RegisterCommand("psay", "Send a message to pinned party members", "[message]", (int groupId, CmdArgs args) =>
            {
                string message = args.PopAll();
                if (string.IsNullOrWhiteSpace(message))
                {
                    capi.ShowChatMessage("Usage: /psay <message>");
                    return;
                }

                var pinnedNames = partyListHud?.GetPinnedPlayersWithColors()?.Keys.ToArray();
                if (pinnedNames == null || pinnedNames.Length == 0)
                {
                    capi.ShowChatMessage("No party members pinned. Pin buddies in the party list first (press P).");
                    return;
                }

                capi.Network.GetChannel(ChannelName).SendPacket(new BuddyChatPacket
                {
                    SenderName = capi.World.Player.PlayerName,
                    Message = message,
                    TargetNames = pinnedNames,
                    IsPartyChat = true
                });

                capi.ShowChatMessage($"<strong>[Party] {capi.World.Player.PlayerName}:</strong> {message}");
            });

            api.RegisterCommand("gsay", "Send a message to all beacon group members", "[message]", (int groupId, CmdArgs args) =>
            {
                string message = args.PopAll();
                if (string.IsNullOrWhiteSpace(message))
                {
                    capi.ShowChatMessage("Usage: /gsay <message>");
                    return;
                }

                capi.Network.GetChannel(ChannelName).SendPacket(new BuddyChatPacket
                {
                    SenderName = capi.World.Player.PlayerName,
                    Message = message,
                    TargetNames = null,
                    IsPartyChat = false
                });

                capi.ShowChatMessage($"<strong>[Group] {capi.World.Player.PlayerName}:</strong> {message}");
            });

            // Register test command to simulate teleport requests (for testing dialog z-order)
            api.RegisterCommand("buddytest", "Test buddy beacon dialog", "", (int groupId, CmdArgs args) =>
            {
                // Simulate a teleport prompt
                var testPacket = new TeleportPromptPacket
                {
                    RequesterName = "TestPlayer",
                    RequestType = TeleportRequestType.TeleportTo,
                    RequestId = 9999,
                    RequestCount = 1,
                    RequesterUid = "test-uid-12345",
                    RequestTimestamp = capi.World.ElapsedMilliseconds
                };
                OnTeleportPromptReceived(testPacket);
                capi.ShowChatMessage("[BuddyTest] Teleport prompt opened for testing");
            });

            // Register test command to simulate fake buddies for party list testing
            api.RegisterCommand("buddyfake", "Add fake buddies for testing (persists until .buddyclear)", "", (int groupId, CmdArgs args) =>
            {
                var playerPos = capi.World.Player.Entity.Pos.XYZ;
                var random = new Random();

                string[] names = { "FakePlayer1", "FakePlayer2", "FakePlayer3" };

                // Store fixed positions for fake buddies
                fakeBuddyPositions = new List<Vec3d>
                {
                    new Vec3d(playerPos.X + random.Next(-500, 500), playerPos.Y + random.Next(-20, 20), playerPos.Z + random.Next(-500, 500)),
                    new Vec3d(playerPos.X + random.Next(-1000, 1000), playerPos.Y + random.Next(-50, 50), playerPos.Z + random.Next(-1000, 1000)),
                    new Vec3d(playerPos.X + random.Next(-2000, 2000), playerPos.Y + random.Next(-100, 100), playerPos.Z + random.Next(-2000, 2000))
                };
                fakeBuddiesActive = true;

                // Send spawn request to server for visual entities
                capi.Network.GetChannel(ChannelName).SendPacket(new FakeBuddySpawnPacket
                {
                    PosX = fakeBuddyPositions.Select(p => p.X).ToArray(),
                    PosY = fakeBuddyPositions.Select(p => p.Y).ToArray(),
                    PosZ = fakeBuddyPositions.Select(p => p.Z).ToArray(),
                    Names = names
                });

                // Start refresh timer if not already running
                if (fakeBuddyTickId == 0)
                {
                    fakeBuddyTickId = capi.Event.RegisterGameTickListener(RefreshFakeBuddies, 1000);
                }

                RefreshFakeBuddies(0);
                capi.ShowChatMessage("[BuddyTest] Added 3 persistent fake buddies with markers. Use .buddyclear to remove.");
            });

            // Command to clear fake buddies
            api.RegisterCommand("buddyclear", "Clear fake buddies", "", (int groupId, CmdArgs args) =>
            {
                fakeBuddiesActive = false;
                fakeBuddyPositions = null;
                if (fakeBuddyTickId != 0)
                {
                    capi.Event.UnregisterGameTickListener(fakeBuddyTickId);
                    fakeBuddyTickId = 0;
                }

                // Send clear request to server to despawn entities
                capi.Network.GetChannel(ChannelName).SendPacket(new FakeBuddyClearPacket());

                buddyCompassHud?.UpdateBuddyPositions(new List<BuddyPositionWithTimestamp>());
                buddyMapLayer?.UpdateBuddyPositions(new List<BuddyPositionWithTimestamp>());
                partyListHud?.UpdateBuddyPositions(new List<BuddyPositionWithTimestamp>());
                capi.ShowChatMessage("[BuddyTest] Fake buddies cleared.");
            });

            // Command to test receiving chat messages
            api.RegisterCommand("buddychattest", "Simulate receiving group/party chat", "", (int groupId, CmdArgs args) =>
            {
                // Simulate receiving a group message
                OnBuddyChatReceived(new BuddyChatPacket
                {
                    SenderName = "FakePlayer1",
                    Message = "Hey! Found some copper ore over here!",
                    IsPartyChat = false
                });

                // Small delay then party message
                capi.Event.RegisterCallback((dt) =>
                {
                    OnBuddyChatReceived(new BuddyChatPacket
                    {
                        SenderName = "FakePlayer2",
                        Message = "On my way! Wait for me.",
                        IsPartyChat = true
                    });
                }, 500);

                // Another group message
                capi.Event.RegisterCallback((dt) =>
                {
                    OnBuddyChatReceived(new BuddyChatPacket
                    {
                        SenderName = "FakePlayer3",
                        Message = "Nice find! I'll bring the pickaxes.",
                        IsPartyChat = false
                    });
                }, 1200);

                capi.ShowChatMessage("[BuddyTest] Simulating incoming chat messages...");
            });

            // Command to test party as LEADER (you can kick/make lead)
            api.RegisterCommand("partytest", "Test party as leader (you can kick/make lead)", "", (int groupId, CmdArgs args) =>
            {
                partyTestMode = true;

                // Create party directly without prompt - you're the leader
                var myName = capi?.World?.Player?.PlayerName ?? "You";
                var myUid = capi?.World?.Player?.PlayerUID ?? "your-uid";

                var fakeState = new PartyStatePacket
                {
                    PartyId = 99999,
                    LeaderUid = myUid,  // You are the leader
                    LeaderName = myName,
                    MemberUids = new[] { myUid, "fake-member-uid" },
                    MemberNames = new[] { myName, "TestMember" }
                };

                OnPartyStateReceived(fakeState);
                AddFakePartyMember("TestMember", "fake-member-uid");
                capi.ShowChatMessage("[PartyTest] Created test party as LEADER. You can Kick/Make Lead on TestMember. Use .partyclear to reset.");
            });

            // Command to test party as MEMBER (you can only leave)
            api.RegisterCommand("partytestmember", "Test party as member (you can only leave)", "", (int groupId, CmdArgs args) =>
            {
                partyTestMode = true;

                // Create party directly - TestLeader is the leader, you're a member
                var myName = capi?.World?.Player?.PlayerName ?? "You";
                var myUid = capi?.World?.Player?.PlayerUID ?? "your-uid";

                var fakeState = new PartyStatePacket
                {
                    PartyId = 99999,
                    LeaderUid = "fake-leader-uid",  // TestLeader is the leader
                    LeaderName = "TestLeader",
                    MemberUids = new[] { "fake-leader-uid", myUid },
                    MemberNames = new[] { "TestLeader", myName }
                };

                OnPartyStateReceived(fakeState);
                AddFakePartyMember("TestLeader", "fake-leader-uid");
                capi.ShowChatMessage("[PartyTest] Joined test party as MEMBER. TestLeader is the leader. You can only Leave. Use .partyclear to reset.");
            });

            // Command to clear test party state
            api.RegisterCommand("partyclear", "Clear test party state", "", (int groupId, CmdArgs args) =>
            {
                partyTestMode = false;
                fakePartyMemberName = null;
                fakePartyMemberUid = null;
                currentPartyState = null;
                partyListHud?.ClearPartyState();
                capi.ShowChatMessage("[PartyTest] Test party cleared.");
            });

            api.Logger.Notification("[VSBuddyBeacon] Client-side initialized");
        }

        /// <summary>
        /// Refreshes fake buddy data with fresh timestamps so they don't expire
        /// </summary>
        private void RefreshFakeBuddies(float dt)
        {
            if (!fakeBuddiesActive || fakeBuddyPositions == null || capi?.World?.Player == null)
                return;

            // Use combined update to include party test member if active
            UpdateAllFakeBuddies();
        }

        /// <summary>
        /// Builds combined list of all fake buddies (from .buddyfake and .partytest) and updates HUDs
        /// </summary>
        private void UpdateAllFakeBuddies()
        {
            if (capi?.World?.Player == null) return;

            long currentTime = capi.World.ElapsedMilliseconds;
            var playerPos = capi.World.Player.Entity?.Pos?.XYZ;
            var allFakeBuddies = new List<BuddyPositionWithTimestamp>();

            // Add fake buddies from .buddyfake
            if (fakeBuddiesActive && fakeBuddyPositions != null)
            {
                allFakeBuddies.Add(new BuddyPositionWithTimestamp
                {
                    Name = "FakePlayer1",
                    PlayerUid = "fake-player1-uid",
                    Position = fakeBuddyPositions[0],
                    ServerTimestamp = currentTime,
                    ClientReceivedTime = currentTime,
                    Health = 18f,
                    MaxHealth = 20f,
                    Saturation = 1100f,
                    MaxSaturation = 1200f
                });
                allFakeBuddies.Add(new BuddyPositionWithTimestamp
                {
                    Name = "FakePlayer2",
                    PlayerUid = "fake-player2-uid",
                    Position = fakeBuddyPositions[1],
                    ServerTimestamp = currentTime,
                    ClientReceivedTime = currentTime,
                    Health = 12f,
                    MaxHealth = 25f,
                    Saturation = 400f,
                    MaxSaturation = 1200f
                });
                allFakeBuddies.Add(new BuddyPositionWithTimestamp
                {
                    Name = "FakePlayer3",
                    PlayerUid = "fake-player3-uid",
                    Position = fakeBuddyPositions[2],
                    ServerTimestamp = currentTime,
                    ClientReceivedTime = currentTime,
                    Health = 18f,
                    MaxHealth = 22f,
                    Saturation = 1100f,
                    MaxSaturation = 1200f
                });
            }

            // Add fake party member from .partytest
            if (partyTestMode && !string.IsNullOrEmpty(fakePartyMemberName) && playerPos != null)
            {
                allFakeBuddies.Add(new BuddyPositionWithTimestamp
                {
                    Name = fakePartyMemberName,
                    PlayerUid = fakePartyMemberUid,
                    Position = new Vec3d(playerPos.X + 10, playerPos.Y + 15, playerPos.Z + 10),
                    ServerTimestamp = currentTime,
                    ClientReceivedTime = currentTime,
                    Health = 15f,
                    MaxHealth = 15f,
                    Saturation = 1000f,
                    MaxSaturation = 1200f
                });
            }

            if (allFakeBuddies.Count > 0)
            {
                buddyCompassHud?.UpdateBuddyPositions(allFakeBuddies);
                buddyMapLayer?.UpdateBuddyPositions(allFakeBuddies);
                partyListHud?.UpdateBuddyPositions(allFakeBuddies);
            }
        }

        #region Server-side Beacon Handlers

        private void OnBeaconCodeSet(IServerPlayer fromPlayer, BeaconCodeSetPacket packet)
        {
            string code = packet.BeaconCode?.Trim() ?? "";

            if (string.IsNullOrEmpty(code))
            {
                // Remove beacon
                playerBeaconCodes.Remove(fromPlayer.PlayerUID);
                LogInfo(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} cleared their beacon code");

                // Also clear from item if found
                UpdateBeaconBandItem(fromPlayer, "");
            }
            else
            {
                // Set beacon
                playerBeaconCodes[fromPlayer.PlayerUID] = code;
                LogInfo(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} set beacon code to \"{code}\"");

                // Also save to item for persistence across disconnects
                UpdateBeaconBandItem(fromPlayer, code);
            }
        }

        /// <summary>
        /// Updates the beacon code on the server's copy of the beacon band item
        /// This ensures persistence across disconnects/reconnects
        /// When setting a code, only ONE beacon band gets the code - all others are cleared
        /// This prevents conflicts from multiple beacon bands
        /// </summary>
        private void UpdateBeaconBandItem(IServerPlayer player, string code)
        {
            var inv = player.InventoryManager;
            if (inv == null) return;

            var hotbar = inv.GetOwnInventory("hotbar");
            var backpack = inv.GetOwnInventory("backpack");
            var character = inv.GetOwnInventory("character");

            bool foundFirst = false;
            int updatedCount = 0;

            foreach (var inventory in new[] { hotbar, backpack, character })
            {
                if (inventory == null) continue;

                try
                {
                    foreach (var slot in inventory)
                    {
                        if (slot?.Itemstack?.Item is ItemBeaconBand)
                        {
                            if (!foundFirst && !string.IsNullOrEmpty(code))
                            {
                                // First beacon band found - set the code
                                slot.Itemstack.Attributes.SetString("beaconCode", code);
                                slot.MarkDirty();
                                foundFirst = true;
                                updatedCount++;
                                LogDebug(sapi, $"[VSBuddyBeacon] Set beacon code \"{code}\" on first beacon band for {player.PlayerName}");
                            }
                            else
                            {
                                // Clear any other beacon bands to prevent conflicts
                                if (slot.Itemstack.Attributes.HasAttribute("beaconCode"))
                                {
                                    slot.Itemstack.Attributes.RemoveAttribute("beaconCode");
                                    slot.MarkDirty();
                                    updatedCount++;
                                    LogDebug(sapi, $"[VSBuddyBeacon] Cleared beacon code from extra beacon band for {player.PlayerName}");
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Skip inventories that can't be enumerated safely
                }
            }

            if (updatedCount == 0)
            {
                LogDebug(sapi, $"[VSBuddyBeacon] No beacon band found in inventory for {player.PlayerName} - code only saved to memory");
            }
            else
            {
                LogInfo(sapi, $"[VSBuddyBeacon] Updated {updatedCount} beacon band(s) for {player.PlayerName}");
            }
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            playerBeaconCodes.Remove(player.PlayerUID);

            // Clear silence lists for this player
            silencedPlayers.Remove(player.PlayerUID);

            // Clear request counts for this player (as target)
            requestCounts.Remove(player.PlayerUID);

            // Clear request counts where this player was the requester
            foreach (var counts in requestCounts.Values)
            {
                counts.Remove(player.PlayerUID);
            }

            // Clear ping rate limit tracking
            pingTimestamps.Remove(player.PlayerUID);

            // Cancel pending party invites from this player
            var invitesToCancel = pendingPartyInvites
                .Where(kvp => kvp.Value.InviterUid == player.PlayerUID)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var id in invitesToCancel)
            {
                pendingPartyInvites.Remove(id);
            }

            // Clear party invite counts for this player
            partyInviteCounts.Remove(player.PlayerUID);
            foreach (var counts in partyInviteCounts.Values)
            {
                counts.Remove(player.PlayerUID);
            }

            // Handle party membership - broadcast offline status but keep in party
            if (playerPartyMap.TryGetValue(player.PlayerUID, out long partyId) && parties.TryGetValue(partyId, out var party))
            {
                LogInfo(sapi, $"[VSBuddyBeacon] {player.PlayerName} disconnected from party {partyId} (staying in party)");

                // If the current leader (original or acting) disconnects, promote next online member
                if (party.LeaderUid == player.PlayerUID)
                {
                    var nextOnlineMember = party.MemberUids
                        .Where(uid => uid != player.PlayerUID)
                        .FirstOrDefault(uid => sapi.World.AllOnlinePlayers.Any(p => p.PlayerUID == uid));

                    if (nextOnlineMember != null)
                    {
                        party.LeaderUid = nextOnlineMember;  // Acting leader (OriginalLeaderUid stays unchanged)
                        var newLeader = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == nextOnlineMember);
                        LogInfo(sapi, $"[VSBuddyBeacon] Acting leadership transferred to {newLeader?.PlayerName ?? nextOnlineMember}");
                    }
                }

                // Pass disconnecting player UID so they're marked offline immediately
                // (they may still be in AllOnlinePlayers at this point)
                BroadcastPartyState(party, disconnectingPlayerUid: player.PlayerUID);
            }
        }

        /// <summary>
        /// Searches player's hotbar and backpack for a beacon band with a code
        /// </summary>
        private string GetPlayerBeaconCode(IServerPlayer player)
        {
            var inv = player.InventoryManager;
            if (inv == null) return null;

            // Check hotbar, backpack, and character equipment - avoid creative/other special inventories
            var hotbar = inv.GetOwnInventory("hotbar");
            var backpack = inv.GetOwnInventory("backpack");
            var character = inv.GetOwnInventory("character");

            foreach (var inventory in new[] { hotbar, backpack, character })
            {
                if (inventory == null) continue;

                try
                {
                    foreach (var slot in inventory)
                    {
                        if (slot?.Itemstack?.Item is ItemBeaconBand)
                        {
                            string code = slot.Itemstack.Attributes.GetString("beaconCode", "");
                            if (!string.IsNullOrEmpty(code))
                            {
                                return code;
                            }
                        }
                    }
                }
                catch
                {
                    // Skip inventories that can't be enumerated safely
                }
            }

            return null;
        }

        private float GetPlayerHealth(IServerPlayer player)
        {
            var healthBehavior = player.Entity?.GetBehavior<EntityBehaviorHealth>();
            return healthBehavior?.Health ?? 0f;
        }

        private float GetPlayerMaxHealth(IServerPlayer player)
        {
            var healthBehavior = player.Entity?.GetBehavior<EntityBehaviorHealth>();
            return healthBehavior?.MaxHealth ?? 15f;
        }

        private float GetPlayerSaturation(IServerPlayer player)
        {
            var hungerBehavior = player.Entity?.GetBehavior<EntityBehaviorHunger>();
            return hungerBehavior?.Saturation ?? 0f;
        }

        private float GetPlayerMaxSaturation(IServerPlayer player)
        {
            var hungerBehavior = player.Entity?.GetBehavior<EntityBehaviorHunger>();
            return hungerBehavior?.MaxSaturation ?? 1200f;
        }

        private void BroadcastBeaconPositions(float dt)
        {
            long currentTime = sapi.World.ElapsedMilliseconds;

            // Group players by beacon code (from worn/inventory items OR manual setting)
            var codeGroups = new Dictionary<string, List<IServerPlayer>>();

            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player?.Entity == null) continue;

                // Priority: playerBeaconCodes (instant) over item attributes (slow sync)
                string code = null;
                if (playerBeaconCodes.TryGetValue(player.PlayerUID, out string manualCode))
                {
                    code = manualCode;
                }

                if (string.IsNullOrEmpty(code))
                {
                    code = GetPlayerBeaconCode(player);
                }

                if (string.IsNullOrEmpty(code)) continue;

                if (!codeGroups.ContainsKey(code))
                    codeGroups[code] = new List<IServerPlayer>();

                codeGroups[code].Add(player);
            }

            // Config settings
            int maxGroupSize = config.MaxBeaconGroupSize;
            float posThreshold = config.PositionChangeThreshold;
            var healthMode = config.HealthDataMode;
            float healthThreshold = config.HealthChangeThreshold;
            float satThreshold = config.SaturationChangeThreshold;
            bool enableLod = config.EnableDistanceLod;
            float lodNear = config.LodNearDistance;
            float lodMid = config.LodMidDistance;
            float baseIntervalMs = config.BeaconUpdateInterval * 1000f;

            // For each group with 2+ players, send positions to all members
            foreach (var group in codeGroups.Values)
            {
                if (group.Count < 2) continue;

                // Apply max group size limit if configured
                var effectiveGroup = maxGroupSize > 0 && group.Count > maxGroupSize
                    ? group.Take(maxGroupSize).ToList()
                    : group;

                foreach (var recipient in effectiveGroup)
                {
                    bool needsFullSync = playersNeedingFullSync.Contains(recipient.PlayerUID);

                    // Get other players in same group (not self)
                    var others = effectiveGroup.Where(p => p.PlayerUID != recipient.PlayerUID).ToList();

                    // Build packet with delta compression
                    var names = new List<string>();
                    var uids = new List<string>();
                    var posX = new List<double>();
                    var posY = new List<double>();
                    var posZ = new List<double>();
                    var timestamps = new List<long>();
                    var health = new List<float>();
                    var maxHealth = new List<float>();
                    var saturation = new List<float>();
                    var maxSaturation = new List<float>();

                    var recipientPos = recipient.Entity.Pos;

                    foreach (var buddy in others)
                    {
                        string stateKey = $"{recipient.PlayerUID}:{buddy.PlayerUID}";
                        var buddyPos = buddy.Entity.Pos;
                        float buddyHealth = GetPlayerHealth(buddy);
                        float buddyMaxHealth = GetPlayerMaxHealth(buddy);
                        float buddySat = GetPlayerSaturation(buddy);
                        float buddyMaxSat = GetPlayerMaxSaturation(buddy);

                        // Check if we need to send this buddy's data
                        bool sendPosition = needsFullSync;
                        bool sendHealth = needsFullSync && healthMode != Config.HealthDataMode.Never;

                        if (!needsFullSync)
                        {
                            // Distance-based LOD: check if enough time has passed based on distance tier
                            if (enableLod && lastSentStates.TryGetValue(stateKey, out var lodState))
                            {
                                double dx = buddyPos.X - recipientPos.X;
                                double dz = buddyPos.Z - recipientPos.Z;
                                double distance = Math.Sqrt(dx * dx + dz * dz);

                                // Determine minimum interval based on distance tier
                                float minIntervalMs;
                                if (distance < lodNear)
                                {
                                    minIntervalMs = baseIntervalMs;        // Full rate (1x)
                                }
                                else if (distance < lodMid)
                                {
                                    minIntervalMs = baseIntervalMs * 2f;   // Half rate (2x interval)
                                }
                                else
                                {
                                    minIntervalMs = baseIntervalMs * 4f;   // Quarter rate (4x interval)
                                }

                                // Skip this buddy if not enough time has passed
                                float timeSinceLastSent = currentTime - lodState.LastSentTime;
                                if (timeSinceLastSent < minIntervalMs)
                                {
                                    continue;  // Skip this buddy entirely for this update cycle
                                }
                            }

                            if (lastSentStates.TryGetValue(stateKey, out var lastState))
                            {
                                // Check position threshold
                                sendPosition = lastState.HasPositionChanged(buddyPos.X, buddyPos.Y, buddyPos.Z, posThreshold);

                                // Check health based on mode
                                if (healthMode == Config.HealthDataMode.Always)
                                {
                                    sendHealth = true;
                                }
                                else if (healthMode == Config.HealthDataMode.OnChange)
                                {
                                    sendHealth = lastState.HasHealthChanged(buddyHealth, buddyMaxHealth, healthThreshold)
                                              || lastState.HasSaturationChanged(buddySat, buddyMaxSat, satThreshold);
                                }
                            }
                            else
                            {
                                // No last state = first time seeing this buddy, send everything
                                sendPosition = true;
                                sendHealth = healthMode != Config.HealthDataMode.Never;
                            }
                        }

                        // Only include buddy if something changed (or forced full sync)
                        if (sendPosition || sendHealth)
                        {
                            names.Add(buddy.PlayerName);
                            uids.Add(buddy.PlayerUID);
                            posX.Add(buddyPos.X);
                            posY.Add(buddyPos.Y);
                            posZ.Add(buddyPos.Z);
                            timestamps.Add(currentTime);

                            if (sendHealth)
                            {
                                health.Add(buddyHealth);
                                maxHealth.Add(buddyMaxHealth);
                                saturation.Add(buddySat);
                                maxSaturation.Add(buddyMaxSat);
                            }
                            else
                            {
                                // Use sentinel values to indicate "no update"
                                health.Add(-1f);
                                maxHealth.Add(-1f);
                                saturation.Add(-1f);
                                maxSaturation.Add(-1f);
                            }

                            // Update last sent state
                            if (!lastSentStates.TryGetValue(stateKey, out var state))
                            {
                                state = new LastSentBuddyState();
                                lastSentStates[stateKey] = state;
                            }

                            // Only update health/saturation baseline when we actually sent those values
                            // Otherwise slow saturation drift never accumulates enough to trigger an update
                            if (sendHealth)
                            {
                                state.Update(buddyPos.X, buddyPos.Y, buddyPos.Z, buddyHealth, buddyMaxHealth, buddySat, buddyMaxSat, currentTime);
                            }
                            else
                            {
                                state.UpdatePositionOnly(buddyPos.X, buddyPos.Y, buddyPos.Z, currentTime);
                            }
                        }
                    }

                    // Only send packet if there's data to send
                    if (names.Count > 0)
                    {
                        var packet = new BeaconPositionPacket
                        {
                            PlayerNames = names.ToArray(),
                            PlayerUids = uids.ToArray(),
                            PosX = posX.ToArray(),
                            PosY = posY.ToArray(),
                            PosZ = posZ.ToArray(),
                            Timestamps = timestamps.ToArray(),
                            Health = health.ToArray(),
                            MaxHealth = maxHealth.ToArray(),
                            Saturation = saturation.ToArray(),
                            MaxSaturation = maxSaturation.ToArray()
                        };

                        sapi.Network.GetChannel(ChannelName).SendPacket(packet, recipient);
                    }

                    // Clear full sync flag after processing
                    if (needsFullSync)
                    {
                        playersNeedingFullSync.Remove(recipient.PlayerUID);
                    }
                }
            }
        }

        private void OnMapPingReceived(IServerPlayer fromPlayer, MapPingPacket packet)
        {
            // Check if pings are enabled
            if (!config.EnableMapPings)
            {
                LogDebug(sapi, $"[VSBuddyBeacon] Ping from {fromPlayer.PlayerName} rejected - pings disabled in config");
                return;
            }

            long currentTime = sapi.World.ElapsedMilliseconds;

            // Rate limiting: max 3 pings per 10 seconds
            if (!pingTimestamps.TryGetValue(fromPlayer.PlayerUID, out var timestamps))
            {
                timestamps = new List<long>();
                pingTimestamps[fromPlayer.PlayerUID] = timestamps;
            }

            // Remove timestamps older than the window
            long windowStart = currentTime - (long)(PING_WINDOW_SECONDS * 1000);
            timestamps.RemoveAll(t => t < windowStart);

            if (timestamps.Count >= MAX_PINGS_PER_WINDOW)
            {
                // Rate limited - silently ignore
                LogDebug(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} rate limited on pings ({timestamps.Count} in window)");
                return;
            }

            // Record this ping
            timestamps.Add(currentTime);

            // Find sender's beacon code
            string senderCode = null;
            if (playerBeaconCodes.TryGetValue(fromPlayer.PlayerUID, out string manualCode))
            {
                senderCode = manualCode;
            }
            if (string.IsNullOrEmpty(senderCode))
            {
                senderCode = GetPlayerBeaconCode(fromPlayer);
            }

            if (string.IsNullOrEmpty(senderCode))
            {
                // Player has no beacon code, can't ping
                LogDebug(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} tried to ping but has no beacon code");
                return;
            }

            // Find all players in the same beacon group
            var groupMembers = new List<IServerPlayer>();
            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player?.Entity == null) continue;

                string playerCode = null;
                if (playerBeaconCodes.TryGetValue(player.PlayerUID, out string code))
                {
                    playerCode = code;
                }
                if (string.IsNullOrEmpty(playerCode))
                {
                    playerCode = GetPlayerBeaconCode(player);
                }

                if (playerCode == senderCode)
                {
                    groupMembers.Add(player);
                }
            }

            // Broadcast ping to all group members (including sender so they see their own ping)
            var pingPacket = new MapPingPacket
            {
                SenderName = fromPlayer.PlayerName,
                PosX = packet.PosX,
                PosZ = packet.PosZ,
                Timestamp = sapi.World.ElapsedMilliseconds
            };

            foreach (var member in groupMembers)
            {
                sapi.Network.GetChannel(ChannelName).SendPacket(pingPacket, member);
            }

            LogDebug(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} pinged at ({packet.PosX:F0}, {packet.PosZ:F0}) - sent to {groupMembers.Count} group members");
        }

        private void OnFakeBuddySpawn(IServerPlayer fromPlayer, FakeBuddySpawnPacket packet)
        {
            // First clear any existing fake buddies
            ClearFakeBuddyEntities();

            if (packet.PosX == null || packet.Names == null) return;

            // Spawn armor stands (or straw dummies) at each position
            for (int i = 0; i < packet.PosX.Length && i < packet.Names.Length; i++)
            {
                try
                {
                    // Try to spawn a straw dummy (training target) - looks like a person
                    var entityType = sapi.World.GetEntityType(new AssetLocation("game:strawdummy"));
                    if (entityType == null)
                    {
                        // Fallback to a chicken if straw dummy doesn't exist
                        entityType = sapi.World.GetEntityType(new AssetLocation("game:chicken-rooster"));
                    }

                    if (entityType != null)
                    {
                        var entity = sapi.World.ClassRegistry.CreateEntity(entityType);
                        entity.ServerPos.SetPos(packet.PosX[i], packet.PosY[i], packet.PosZ[i]);
                        entity.Pos.SetFrom(entity.ServerPos);

                        sapi.World.SpawnEntity(entity);
                        fakeBuddyEntityIds.Add(entity.EntityId);

                        LogDebug(sapi, $"[VSBuddyBeacon] Spawned fake buddy entity '{packet.Names[i]}' at ({packet.PosX[i]:F0}, {packet.PosY[i]:F0}, {packet.PosZ[i]:F0})");
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Warning($"[VSBuddyBeacon] Failed to spawn fake buddy entity: {ex.Message}");
                }
            }

            LogInfo(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} spawned {fakeBuddyEntityIds.Count} fake buddy entities");
        }

        private void OnFakeBuddyClear(IServerPlayer fromPlayer, FakeBuddyClearPacket packet)
        {
            int count = ClearFakeBuddyEntities();
            LogInfo(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} cleared {count} fake buddy entities");
        }

        private int ClearFakeBuddyEntities()
        {
            int count = 0;
            foreach (var entityId in fakeBuddyEntityIds)
            {
                var entity = sapi.World.GetEntityById(entityId);
                if (entity != null)
                {
                    entity.Die(EnumDespawnReason.Removed);
                    count++;
                }
            }
            fakeBuddyEntityIds.Clear();
            return count;
        }

        private void OnBuddyChatReceived(IServerPlayer fromPlayer, BuddyChatPacket packet)
        {
            if (string.IsNullOrWhiteSpace(packet.Message)) return;

            // Find sender's beacon code
            string senderCode = null;
            if (playerBeaconCodes.TryGetValue(fromPlayer.PlayerUID, out string manualCode))
            {
                senderCode = manualCode;
            }
            if (string.IsNullOrEmpty(senderCode))
            {
                senderCode = GetPlayerBeaconCode(fromPlayer);
            }

            if (string.IsNullOrEmpty(senderCode))
            {
                // Sender has no beacon code - can't send group/party chat
                SendResult(fromPlayer, false, "You need an active beacon band to use group/party chat.");
                return;
            }

            // Find all players in the same beacon group
            var groupMembers = new List<IServerPlayer>();
            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player?.Entity == null || player.PlayerUID == fromPlayer.PlayerUID) continue;

                string playerCode = null;
                if (playerBeaconCodes.TryGetValue(player.PlayerUID, out string code))
                {
                    playerCode = code;
                }
                if (string.IsNullOrEmpty(playerCode))
                {
                    playerCode = GetPlayerBeaconCode(player);
                }

                if (playerCode == senderCode)
                {
                    groupMembers.Add(player);
                }
            }

            // Determine recipients based on chat type
            List<IServerPlayer> recipients;
            if (packet.IsPartyChat && packet.TargetNames != null && packet.TargetNames.Length > 0)
            {
                // Party chat - only send to specified targets that are in the group
                var targetSet = new HashSet<string>(packet.TargetNames, StringComparer.OrdinalIgnoreCase);
                recipients = groupMembers.Where(p => targetSet.Contains(p.PlayerName)).ToList();

                if (recipients.Count == 0)
                {
                    SendResult(fromPlayer, false, "None of your pinned party members are in your beacon group.");
                    return;
                }
            }
            else
            {
                // Group chat - send to all group members
                recipients = groupMembers;

                if (recipients.Count == 0)
                {
                    SendResult(fromPlayer, false, "No other players in your beacon group.");
                    return;
                }
            }

            // Create outgoing packet (use sender's name from server for security)
            var outPacket = new BuddyChatPacket
            {
                SenderName = fromPlayer.PlayerName,
                Message = packet.Message,
                IsPartyChat = packet.IsPartyChat
            };

            // Send to all recipients
            foreach (var recipient in recipients)
            {
                sapi.Network.GetChannel(ChannelName).SendPacket(outPacket, recipient);
            }

            string chatType = packet.IsPartyChat ? "party" : "group";
            LogDebug(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} sent {chatType} message to {recipients.Count} players");
        }

        #endregion

        #region Server-side Party Handlers

        private void OnPartyInviteReceived(IServerPlayer fromPlayer, PartyInvitePacket packet)
        {
            var targetPlayer = sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerUID == packet.TargetPlayerUid) as IServerPlayer;

            if (targetPlayer == null)
            {
                SendPartyInviteResult(fromPlayer, false, "Target player is not online.");
                return;
            }

            // Check if target is silenced by the inviter
            if (IsPlayerSilenced(targetPlayer.PlayerUID, fromPlayer.PlayerUID))
            {
                SendPartyInviteResult(fromPlayer, false, "That player is not accepting requests right now.");
                return;
            }

            // Check if inviter can invite (must be leader of their party or have no party)
            if (playerPartyMap.TryGetValue(fromPlayer.PlayerUID, out long inviterPartyId))
            {
                var inviterParty = parties[inviterPartyId];
                if (inviterParty.LeaderUid != fromPlayer.PlayerUID)
                {
                    SendPartyInviteResult(fromPlayer, false, "Only the party leader can invite new members.");
                    return;
                }
            }

            // Check if target is already in a party
            if (playerPartyMap.ContainsKey(targetPlayer.PlayerUID))
            {
                SendPartyInviteResult(fromPlayer, false, $"{targetPlayer.PlayerName} is already in a party.");
                return;
            }

            // Track invite count for repeat detection
            int inviteCount = IncrementPartyInviteCount(targetPlayer.PlayerUID, fromPlayer.PlayerUID);
            long requestTime = sapi.World.ElapsedMilliseconds;

            var invite = new PendingPartyInvite
            {
                InviteId = nextPartyInviteId++,
                InviterUid = fromPlayer.PlayerUID,
                TargetUid = targetPlayer.PlayerUID,
                PartyId = playerPartyMap.TryGetValue(fromPlayer.PlayerUID, out long pid) ? pid : 0,
                RequestTime = requestTime
            };

            pendingPartyInvites[invite.InviteId] = invite;

            var prompt = new PartyInvitePromptPacket
            {
                InviterName = fromPlayer.PlayerName,
                InviteId = invite.InviteId,
                InviterUid = fromPlayer.PlayerUID,
                RequestTimestamp = requestTime,
                RequestCount = inviteCount
            };

            sapi.Network.GetChannel(ChannelName).SendPacket(prompt, targetPlayer);
            SendPartyInviteResult(fromPlayer, true, $"Invite sent to {targetPlayer.PlayerName}. Waiting for response...");

            LogInfo(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} invited {targetPlayer.PlayerName} to party (count: {inviteCount})");
        }

        private void OnPartyInviteResponseReceived(IServerPlayer fromPlayer, PartyInviteResponsePacket packet)
        {
            if (!pendingPartyInvites.TryGetValue(packet.InviteId, out var invite))
            {
                SendPartyInviteResult(fromPlayer, false, "Invite has expired.");
                return;
            }

            if (invite.TargetUid != fromPlayer.PlayerUID)
                return;

            pendingPartyInvites.Remove(packet.InviteId);

            var inviter = sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerUID == invite.InviterUid) as IServerPlayer;

            if (inviter == null)
            {
                SendPartyInviteResult(fromPlayer, false, "Inviter is no longer online.");
                return;
            }

            if (!packet.Accepted)
            {
                SendPartyInviteResult(inviter, false, $"{fromPlayer.PlayerName} declined your party invite.");
                SendPartyInviteResult(fromPlayer, true, "Invite declined.");
                return;
            }

            // Check if target joined another party while waiting
            if (playerPartyMap.ContainsKey(fromPlayer.PlayerUID))
            {
                SendPartyInviteResult(inviter, false, $"{fromPlayer.PlayerName} is already in a party.");
                SendPartyInviteResult(fromPlayer, false, "You are already in a party.");
                return;
            }

            // Create or join party
            Party party;
            if (invite.PartyId != 0 && parties.TryGetValue(invite.PartyId, out party))
            {
                // Join existing party
            }
            else if (playerPartyMap.TryGetValue(inviter.PlayerUID, out long existingPartyId) && parties.TryGetValue(existingPartyId, out party))
            {
                // Inviter created/joined a party since invite was sent
            }
            else
            {
                // Create new party with inviter as leader
                party = new Party
                {
                    PartyId = nextPartyId++,
                    OriginalLeaderUid = inviter.PlayerUID,
                    LeaderUid = inviter.PlayerUID,
                    MemberUids = new List<string> { inviter.PlayerUID },
                    MemberNames = new Dictionary<string, string> { { inviter.PlayerUID, inviter.PlayerName } },
                    CreatedTime = sapi.World.ElapsedMilliseconds
                };
                parties[party.PartyId] = party;
                playerPartyMap[inviter.PlayerUID] = party.PartyId;
                LogInfo(sapi, $"[VSBuddyBeacon] Created new party {party.PartyId} with leader {inviter.PlayerName}");
            }

            // Add target to party
            party.MemberUids.Add(fromPlayer.PlayerUID);
            party.MemberNames[fromPlayer.PlayerUID] = fromPlayer.PlayerName;
            playerPartyMap[fromPlayer.PlayerUID] = party.PartyId;

            LogInfo(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} joined party {party.PartyId}");

            // Mark all party members (including new joiner) for full health sync
            // so everyone gets fresh data about the new member and vice versa
            foreach (var memberUid in party.MemberUids)
            {
                playersNeedingFullSync.Add(memberUid);
            }

            // Broadcast updated party state to all members
            BroadcastPartyState(party);

            SendPartyInviteResult(inviter, true, $"{fromPlayer.PlayerName} joined your party!");
            SendPartyInviteResult(fromPlayer, true, $"You joined {inviter.PlayerName}'s party!");
        }

        private void OnPartyLeaveReceived(IServerPlayer fromPlayer, PartyLeavePacket packet)
        {
            HandlePlayerLeaveParty(fromPlayer.PlayerUID, "left");
        }

        private void OnPartyKickReceived(IServerPlayer fromPlayer, PartyKickPacket packet)
        {
            if (!playerPartyMap.TryGetValue(fromPlayer.PlayerUID, out long partyId))
            {
                SendPartyInviteResult(fromPlayer, false, "You are not in a party.");
                return;
            }

            var party = parties[partyId];
            if (party.LeaderUid != fromPlayer.PlayerUID)
            {
                SendPartyInviteResult(fromPlayer, false, "Only the party leader can kick members.");
                return;
            }

            if (!party.MemberUids.Contains(packet.TargetPlayerUid))
            {
                SendPartyInviteResult(fromPlayer, false, "That player is not in your party.");
                return;
            }

            if (packet.TargetPlayerUid == fromPlayer.PlayerUID)
            {
                SendPartyInviteResult(fromPlayer, false, "You cannot kick yourself. Use Leave instead.");
                return;
            }

            // Remove target from party
            party.MemberUids.Remove(packet.TargetPlayerUid);
            playerPartyMap.Remove(packet.TargetPlayerUid);

            // Find target player and notify them
            var targetPlayer = sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerUID == packet.TargetPlayerUid) as IServerPlayer;

            if (targetPlayer != null)
            {
                sapi.Network.GetChannel(ChannelName).SendPacket(new PartyDisbandedPacket
                {
                    Reason = "kicked"
                }, targetPlayer);
            }

            string targetName = targetPlayer?.PlayerName ?? "Unknown";
            LogInfo(sapi, $"[VSBuddyBeacon] {targetName} was kicked from party {partyId} by {fromPlayer.PlayerName}");

            // If only leader remains, disband the party
            if (party.MemberUids.Count <= 1)
            {
                DisbandParty(party, "Party disbanded (only you remained).");
                SendPartyInviteResult(fromPlayer, true, $"Kicked {targetName}. Party disbanded.");
            }
            else
            {
                // Broadcast updated state to remaining members
                BroadcastPartyState(party);
                SendPartyInviteResult(fromPlayer, true, $"Kicked {targetName} from the party.");
            }
        }

        private void OnPartyMakeLeadReceived(IServerPlayer fromPlayer, PartyMakeLeadPacket packet)
        {
            if (!playerPartyMap.TryGetValue(fromPlayer.PlayerUID, out long partyId))
            {
                SendPartyInviteResult(fromPlayer, false, "You are not in a party.");
                return;
            }

            var party = parties[partyId];
            if (party.LeaderUid != fromPlayer.PlayerUID)
            {
                SendPartyInviteResult(fromPlayer, false, "Only the party leader can transfer leadership.");
                return;
            }

            if (!party.MemberUids.Contains(packet.TargetPlayerUid))
            {
                SendPartyInviteResult(fromPlayer, false, "That player is not in your party.");
                return;
            }

            if (packet.TargetPlayerUid == fromPlayer.PlayerUID)
            {
                SendPartyInviteResult(fromPlayer, false, "You are already the leader.");
                return;
            }

            // Transfer leadership permanently (both original and current)
            party.OriginalLeaderUid = packet.TargetPlayerUid;
            party.LeaderUid = packet.TargetPlayerUid;

            var newLeader = sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerUID == packet.TargetPlayerUid);
            string newLeaderName = newLeader?.PlayerName ?? "Unknown";

            LogInfo(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} permanently transferred party {partyId} leadership to {newLeaderName}");

            // Broadcast updated state to all members
            BroadcastPartyState(party);
            SendPartyInviteResult(fromPlayer, true, $"Transferred leadership to {newLeaderName}.");
        }

        private void HandlePlayerLeaveParty(string playerUid, string reason)
        {
            if (!playerPartyMap.TryGetValue(playerUid, out long partyId))
                return;

            var party = parties[partyId];
            party.MemberUids.Remove(playerUid);
            playerPartyMap.Remove(playerUid);

            // Find the leaving player
            var leavingPlayer = sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerUID == playerUid) as IServerPlayer;
            string leavingName = leavingPlayer?.PlayerName ?? "Unknown";

            // Notify the leaving player
            if (leavingPlayer != null && reason != "disconnected")
            {
                sapi.Network.GetChannel(ChannelName).SendPacket(new PartyDisbandedPacket
                {
                    Reason = reason
                }, leavingPlayer);
            }

            LogInfo(sapi, $"[VSBuddyBeacon] {leavingName} left party {partyId}");

            // If 0 or 1 member remains, disband the party (solo party doesn't make sense)
            if (party.MemberUids.Count <= 1)
            {
                if (party.MemberUids.Count == 1)
                {
                    // Notify the remaining solo member that party is disbanded
                    DisbandParty(party, "Party disbanded (other member left).");
                }
                else
                {
                    // Party empty, just clean up
                    parties.Remove(partyId);
                }
                LogInfo(sapi, $"[VSBuddyBeacon] Party {partyId} dissolved");
                return;
            }

            if (party.LeaderUid == playerUid)
            {
                // Transfer leadership to first remaining member
                party.LeaderUid = party.MemberUids[0];
                var newLeader = sapi.World.AllOnlinePlayers
                    .FirstOrDefault(p => p.PlayerUID == party.LeaderUid);
                string newLeaderName = newLeader?.PlayerName ?? "Unknown";
                LogInfo(sapi, $"[VSBuddyBeacon] Party {partyId} leadership transferred to {newLeaderName} (previous leader left)");
            }

            // Notify remaining members
            BroadcastPartyState(party);
        }

        private void DisbandParty(Party party, string reason)
        {
            // Notify all remaining members that the party is disbanded
            foreach (var uid in party.MemberUids.ToList())
            {
                var player = sapi.World.AllOnlinePlayers
                    .FirstOrDefault(p => p.PlayerUID == uid) as IServerPlayer;
                if (player != null)
                {
                    sapi.Network.GetChannel(ChannelName).SendPacket(new PartyDisbandedPacket
                    {
                        Reason = reason
                    }, player);
                }
                playerPartyMap.Remove(uid);
            }

            parties.Remove(party.PartyId);
            LogInfo(sapi, $"[VSBuddyBeacon] Party {party.PartyId} disbanded: {reason}");
        }

        private void BroadcastPartyState(Party party, string disconnectingPlayerUid = null)
        {
            var memberNames = new List<string>();
            var memberUids = new List<string>();
            var memberOnline = new List<bool>();

            // Include ALL members (online and offline)
            foreach (var uid in party.MemberUids)
            {
                var player = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == uid);
                // Player is online if in list AND not the one currently disconnecting
                bool isOnline = player != null && uid != disconnectingPlayerUid;

                // Get name from online player or stored name
                string name = (player != null) ? player.PlayerName :
                    (party.MemberNames.TryGetValue(uid, out string storedName) ? storedName : "Unknown");

                memberUids.Add(uid);
                memberNames.Add(name);
                memberOnline.Add(isOnline);
            }

            var leaderPlayer = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == party.LeaderUid);
            string leaderName = leaderPlayer?.PlayerName ??
                (party.MemberNames.TryGetValue(party.LeaderUid, out string storedLeaderName) ? storedLeaderName : "Unknown");

            // Get original leader name
            var originalLeaderPlayer = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == party.OriginalLeaderUid);
            string originalLeaderName = originalLeaderPlayer?.PlayerName ??
                (party.MemberNames.TryGetValue(party.OriginalLeaderUid, out string storedOriginalName) ? storedOriginalName : "Unknown");

            var statePacket = new PartyStatePacket
            {
                PartyId = party.PartyId,
                LeaderUid = party.LeaderUid,
                LeaderName = leaderName,
                OriginalLeaderUid = party.OriginalLeaderUid,
                OriginalLeaderName = originalLeaderName,
                MemberUids = memberUids.ToArray(),
                MemberNames = memberNames.ToArray(),
                MemberOnline = memberOnline.ToArray()
            };

            // Only send to online members
            foreach (var uid in party.MemberUids)
            {
                var player = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == uid) as IServerPlayer;
                if (player != null)
                {
                    sapi.Network.GetChannel(ChannelName).SendPacket(statePacket, player);
                }
            }
        }

        private int IncrementPartyInviteCount(string targetUid, string inviterUid)
        {
            if (!partyInviteCounts.TryGetValue(targetUid, out var counts))
            {
                counts = new Dictionary<string, int>();
                partyInviteCounts[targetUid] = counts;
            }

            if (!counts.TryGetValue(inviterUid, out int count))
                count = 0;

            count++;
            counts[inviterUid] = count;
            return count;
        }

        private void SendPartyInviteResult(IServerPlayer player, bool success, string message)
        {
            sapi.Network.GetChannel(ChannelName).SendPacket(
                new PartyInviteResultPacket { Success = success, Message = message }, player);
        }

        private void CheckPartyInviteTimeouts(float dt)
        {
            long now = sapi.World.ElapsedMilliseconds;
            var expiredIds = pendingPartyInvites
                .Where(kvp => (now - kvp.Value.RequestTime) / 1000f > REQUEST_TIMEOUT_SECONDS)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in expiredIds)
            {
                var invite = pendingPartyInvites[id];
                pendingPartyInvites.Remove(id);

                var inviter = sapi.World.AllOnlinePlayers
                    .FirstOrDefault(p => p.PlayerUID == invite.InviterUid) as IServerPlayer;

                if (inviter != null)
                    SendPartyInviteResult(inviter, false, "Party invite timed out.");
            }
        }

        #endregion

        #region Server-side Teleport Handlers

        private void OnPlayerListRequest(IServerPlayer fromPlayer, PlayerListRequestPacket packet)
        {
            var onlinePlayers = sapi.World.AllOnlinePlayers
                .Where(p => p.PlayerUID != fromPlayer.PlayerUID)
                .ToArray();

            var response = new PlayerListResponsePacket
            {
                PlayerNames = onlinePlayers.Select(p => p.PlayerName).ToArray(),
                PlayerUids = onlinePlayers.Select(p => p.PlayerUID).ToArray()
            };

            sapi.Network.GetChannel(ChannelName).SendPacket(response, fromPlayer);
        }

        private void OnTeleportRequestReceived(IServerPlayer fromPlayer, TeleportRequestPacket packet)
        {
            var targetPlayer = sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerUID == packet.TargetPlayerUid) as IServerPlayer;

            if (targetPlayer == null)
            {
                SendResult(fromPlayer, false, "Target player is not online.");
                return;
            }

            // Check if requester is silenced by the target
            if (IsPlayerSilenced(targetPlayer.PlayerUID, fromPlayer.PlayerUID))
            {
                SendResult(fromPlayer, false, "That player is not accepting requests right now.");
                return;
            }

            if (!HasTeleportItem(fromPlayer, packet.RequestType))
            {
                string itemName = packet.RequestType == TeleportRequestType.TeleportTo
                    ? "Wayfinder's Compass"
                    : "Hero's Call Stone";
                SendResult(fromPlayer, false, $"You need a {itemName} to do this.");
                return;
            }

            // Track request count for this requester -> target pair
            int requestCount = IncrementRequestCount(targetPlayer.PlayerUID, fromPlayer.PlayerUID);
            LogInfo(sapi, $"[VSBuddyBeacon] Request count for {fromPlayer.PlayerName} -> {targetPlayer.PlayerName}: {requestCount}");

            long requestTime = sapi.World.ElapsedMilliseconds;

            var request = new PendingTeleportRequest
            {
                RequestId = nextRequestId++,
                RequesterUid = fromPlayer.PlayerUID,
                TargetUid = targetPlayer.PlayerUID,
                RequestType = packet.RequestType,
                RequestTime = requestTime
            };

            pendingRequests[request.RequestId] = request;

            var prompt = new TeleportPromptPacket
            {
                RequesterName = fromPlayer.PlayerName,
                RequestType = packet.RequestType,
                RequestId = request.RequestId,
                RequestCount = requestCount,
                RequesterUid = fromPlayer.PlayerUID,
                RequestTimestamp = requestTime
            };

            sapi.Network.GetChannel(ChannelName).SendPacket(prompt, targetPlayer);
            SendResult(fromPlayer, true, $"Request sent to {targetPlayer.PlayerName}. Waiting for response...");

            LogInfo(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} requested to {(packet.RequestType == TeleportRequestType.TeleportTo ? "teleport to" : "summon")} {targetPlayer.PlayerName} (count: {requestCount})");
        }

        private void OnTeleportResponseReceived(IServerPlayer fromPlayer, TeleportResponsePacket packet)
        {
            if (!pendingRequests.TryGetValue(packet.RequestId, out var request))
            {
                SendResult(fromPlayer, false, "Request has expired.");
                return;
            }

            if (request.TargetUid != fromPlayer.PlayerUID)
                return;

            pendingRequests.Remove(packet.RequestId);

            var requester = sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerUID == request.RequesterUid) as IServerPlayer;

            if (requester == null)
            {
                SendResult(fromPlayer, false, "Requester is no longer online.");
                return;
            }

            if (!packet.Accepted)
            {
                SendResult(requester, false, $"{fromPlayer.PlayerName} declined your request.");
                SendResult(fromPlayer, true, "Request declined.");
                return;
            }

            if (!HasTeleportItem(requester, request.RequestType))
            {
                SendResult(requester, false, "You no longer have the teleport item.");
                SendResult(fromPlayer, false, "Teleport failed - requester lacks item.");
                return;
            }

            ConsumeTeleportItem(requester, request.RequestType);
            ExecuteTeleport(requester, fromPlayer, request.RequestType);
        }

        private void ExecuteTeleport(IServerPlayer requester, IServerPlayer target, TeleportRequestType requestType)
        {
            EntityPlayer requesterEntity = requester.Entity;
            EntityPlayer targetEntity = target.Entity;

            if (requesterEntity == null || targetEntity == null)
            {
                SendResult(requester, false, "Teleport failed - player entity not found.");
                return;
            }

            Vec3d teleportPos;
            IServerPlayer playerToMove;

            if (requestType == TeleportRequestType.TeleportTo)
            {
                playerToMove = requester;
                teleportPos = targetEntity.Pos.XYZ.Clone();
            }
            else
            {
                playerToMove = target;
                teleportPos = requesterEntity.Pos.XYZ.Clone();
            }

            teleportPos.X += 1;
            playerToMove.Entity.TeleportTo(teleportPos);

            string requesterMsg = requestType == TeleportRequestType.TeleportTo
                ? $"Teleported to {target.PlayerName}!"
                : $"Summoned {target.PlayerName} to you!";

            string targetMsg = requestType == TeleportRequestType.TeleportTo
                ? $"{requester.PlayerName} teleported to you."
                : $"You were summoned to {requester.PlayerName}.";

            SendResult(requester, true, requesterMsg);
            SendResult(target, true, targetMsg);

            LogInfo(sapi, $"[VSBuddyBeacon] Teleport executed: {playerToMove.PlayerName} -> {(requestType == TeleportRequestType.TeleportTo ? target : requester).PlayerName}");
        }

        private bool HasTeleportItem(IServerPlayer player, TeleportRequestType requestType)
        {
            string itemCode = requestType == TeleportRequestType.TeleportTo
                ? "wayfindercompass"
                : "heroscallstone";

            // Check if item is enabled in config
            if (!config.IsItemEnabled(itemCode))
            {
                return false; // Item disabled
            }

            var inventory = player.InventoryManager.GetOwnInventory("hotbar");
            var backpack = player.InventoryManager.GetOwnInventory("backpack");

            foreach (var inv in new[] { inventory, backpack })
            {
                if (inv == null) continue;
                foreach (var slot in inv)
                {
                    if (slot.Itemstack?.Collectible.Code.Path == itemCode)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void ConsumeTeleportItem(IServerPlayer player, TeleportRequestType requestType)
        {
            string itemCode = requestType == TeleportRequestType.TeleportTo
                ? "wayfindercompass"
                : "heroscallstone";

            var inventory = player.InventoryManager.GetOwnInventory("hotbar");
            var backpack = player.InventoryManager.GetOwnInventory("backpack");

            foreach (var inv in new[] { inventory, backpack })
            {
                if (inv == null) continue;
                foreach (var slot in inv)
                {
                    if (slot.Itemstack?.Collectible.Code.Path == itemCode)
                    {
                        slot.TakeOut(1);
                        slot.MarkDirty();
                        return;
                    }
                }
            }
        }

        private void SendResult(IServerPlayer player, bool success, string message)
        {
            sapi.Network.GetChannel(ChannelName).SendPacket(
                new TeleportResultPacket { Success = success, Message = message }, player);
        }

        private void CheckRequestTimeouts(float dt)
        {
            long now = sapi.World.ElapsedMilliseconds;
            var expiredIds = pendingRequests
                .Where(kvp => (now - kvp.Value.RequestTime) / 1000f > REQUEST_TIMEOUT_SECONDS)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in expiredIds)
            {
                var request = pendingRequests[id];
                pendingRequests.Remove(id);

                var requester = sapi.World.AllOnlinePlayers
                    .FirstOrDefault(p => p.PlayerUID == request.RequesterUid) as IServerPlayer;
                var target = sapi.World.AllOnlinePlayers
                    .FirstOrDefault(p => p.PlayerUID == request.TargetUid) as IServerPlayer;

                // Notify both players about the timeout
                if (requester != null)
                    SendResult(requester, false, "Teleport request timed out.");

                // Note: Target player's dialog will auto-close via client-side countdown
                // But we can send a message as backup in case of clock sync issues
                if (target != null)
                    SendResult(target, false, "Teleport request expired.");
            }
        }

        private bool IsPlayerSilenced(string targetUid, string requesterUid)
        {
            if (!silencedPlayers.TryGetValue(targetUid, out var silenced))
                return false;

            if (!silenced.TryGetValue(requesterUid, out long expirationTime))
                return false;

            long now = sapi.World.ElapsedMilliseconds;
            if (now > expirationTime)
            {
                // Silence has expired, remove it
                silenced.Remove(requesterUid);
                if (silenced.Count == 0)
                    silencedPlayers.Remove(targetUid);
                return false;
            }

            return true;
        }

        private int IncrementRequestCount(string targetUid, string requesterUid)
        {
            if (!requestCounts.TryGetValue(targetUid, out var counts))
            {
                counts = new Dictionary<string, int>();
                requestCounts[targetUid] = counts;
                LogDebug(sapi, $"[VSBuddyBeacon] Created new request count tracker for target {targetUid}");
            }

            if (!counts.TryGetValue(requesterUid, out int count))
                count = 0;

            count++;
            counts[requesterUid] = count;
            LogDebug(sapi, $"[VSBuddyBeacon] Incremented request count: requester={requesterUid} -> target={targetUid}, new count={count}");
            return count;
        }

        private void OnSilencePlayer(IServerPlayer fromPlayer, SilencePlayerPacket packet)
        {
            string playerUidToSilence = packet.PlayerUidToSilence;
            long expirationTime = sapi.World.ElapsedMilliseconds + (long)(SILENCE_DURATION_MINUTES * 60 * 1000);

            if (!silencedPlayers.TryGetValue(fromPlayer.PlayerUID, out var silenced))
            {
                silenced = new Dictionary<string, long>();
                silencedPlayers[fromPlayer.PlayerUID] = silenced;
            }

            silenced[playerUidToSilence] = expirationTime;

            // Find the player name for logging
            var silencedPlayer = sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerUID == playerUidToSilence);
            string silencedName = silencedPlayer?.PlayerName ?? "Unknown";

            LogInfo(sapi, $"[VSBuddyBeacon] {fromPlayer.PlayerName} silenced {silencedName} for {SILENCE_DURATION_MINUTES} minutes");
            SendResult(fromPlayer, true, $"You will not receive requests from {silencedName} for {SILENCE_DURATION_MINUTES} minutes.");
        }

        private TextCommandResult OnUnsilenceCommand(TextCommandCallingArgs args)
        {
            string playerName = args[0] as string;
            var caller = args.Caller.Player as IServerPlayer;

            if (caller == null)
                return TextCommandResult.Error("This command can only be used by players.");

            // Find the player by name
            var targetPlayer = sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerName.Equals(playerName, System.StringComparison.OrdinalIgnoreCase));

            if (targetPlayer == null)
                return TextCommandResult.Error($"Player '{playerName}' not found or not online.");

            // Remove from silence list
            if (silencedPlayers.TryGetValue(caller.PlayerUID, out var silenced))
            {
                if (silenced.Remove(targetPlayer.PlayerUID))
                {
                    if (silenced.Count == 0)
                        silencedPlayers.Remove(caller.PlayerUID);

                    return TextCommandResult.Success($"You will now receive requests from {targetPlayer.PlayerName}.");
                }
            }

            return TextCommandResult.Error($"{targetPlayer.PlayerName} was not silenced.");
        }

        #endregion

        #region Client-side Handlers

        private void OnPlayerListReceived(PlayerListResponsePacket packet)
        {
            if (playerSelectDialog?.IsOpened() == true)
                playerSelectDialog.UpdatePlayerList(packet.PlayerNames, packet.PlayerUids);
        }

        private void OnTeleportPromptReceived(TeleportPromptPacket packet)
        {
            string message = packet.RequestType == TeleportRequestType.TeleportTo
                ? $"{packet.RequesterName} wants to teleport to you."
                : $"{packet.RequesterName} wants to summon you.";

            teleportPromptDialog = new GuiDialogTeleportPrompt(capi, message, packet.RequestId, packet.RequestCount, packet.RequesterUid, packet.RequestTimestamp);
            teleportPromptDialog.TryOpen();
        }

        private void OnTeleportResultReceived(TeleportResultPacket packet)
        {
            capi.ShowChatMessage($"[BuddyBeacon] {packet.Message}");
            playerSelectDialog?.TryClose();
        }

        private void OnBeaconPositionsReceived(BeaconPositionPacket packet)
        {
            long clientReceiveTime = capi.World.ElapsedMilliseconds;  // Capture immediately

            // Update cache with fresh positions from packet
            if (packet.PlayerNames != null)
            {
                for (int i = 0; i < packet.PlayerNames.Length; i++)
                {
                    string name = packet.PlayerNames[i];
                    long serverTimestamp = packet.Timestamps != null && i < packet.Timestamps.Length
                        ? packet.Timestamps[i]
                        : clientReceiveTime;

                    // Get packet health values (-1 means "unchanged, keep previous")
                    float packetHealth = packet.Health != null && i < packet.Health.Length ? packet.Health[i] : -1f;
                    float packetMaxHealth = packet.MaxHealth != null && i < packet.MaxHealth.Length ? packet.MaxHealth[i] : -1f;
                    float packetSat = packet.Saturation != null && i < packet.Saturation.Length ? packet.Saturation[i] : -1f;
                    float packetMaxSat = packet.MaxSaturation != null && i < packet.MaxSaturation.Length ? packet.MaxSaturation[i] : -1f;

                    // Check for existing cached entry to preserve health if sentinel
                    cachedBuddyPositions.TryGetValue(name, out var existingEntry);

                    var pos = new BuddyPositionWithTimestamp
                    {
                        Name = name,
                        PlayerUid = packet.PlayerUids != null && i < packet.PlayerUids.Length ? packet.PlayerUids[i] : null,
                        Position = new Vec3d(packet.PosX[i], packet.PosY[i], packet.PosZ[i]),
                        ServerTimestamp = serverTimestamp,
                        ClientReceivedTime = clientReceiveTime,
                        // Use new value if provided, otherwise keep cached value, otherwise use default
                        Health = packetHealth >= 0 ? packetHealth : (existingEntry?.Health ?? 15f),
                        MaxHealth = packetMaxHealth >= 0 ? packetMaxHealth : (existingEntry?.MaxHealth ?? 15f),
                        Saturation = packetSat >= 0 ? packetSat : (existingEntry?.Saturation ?? 1200f),
                        MaxSaturation = packetMaxSat >= 0 ? packetMaxSat : (existingEntry?.MaxSaturation ?? 1200f)
                    };

                    cachedBuddyPositions[pos.Name] = pos;
                }
            }

            // Remove expired entries from cache (>60s old)
            var expiredNames = cachedBuddyPositions
                .Where(kvp => kvp.Value.GetStalenessLevel(clientReceiveTime) == StalenessLevel.Expired)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var name in expiredNames)
            {
                cachedBuddyPositions.Remove(name);
            }

            // Build combined list from cache (includes both fresh and stale entries)
            var positions = cachedBuddyPositions.Values.ToList();

            buddyCompassHud?.UpdateBuddyPositions(positions);
            buddyMapLayer?.UpdateBuddyPositions(positions);
            partyListHud?.UpdateBuddyPositions(positions);
        }

        private void OnMapPingReceived(MapPingPacket packet)
        {
            // Don't show pings if disabled in config
            if (!config.EnableMapPings) return;

            long clientTime = capi.World.ElapsedMilliseconds;
            buddyMapLayer?.AddPing(packet.SenderName, packet.PosX, packet.PosZ, clientTime);
        }

        private void OnBuddyChatReceived(BuddyChatPacket packet)
        {
            string chatType = packet.IsPartyChat ? "Party" : "Group";
            capi.ShowChatMessage($"<strong>[{chatType}] {packet.SenderName}:</strong> {packet.Message}");
        }

        private void OnPartyInvitePromptReceived(PartyInvitePromptPacket packet)
        {
            partyInvitePromptDialog = new GuiDialogPartyInvitePrompt(
                capi, packet.InviterName, packet.InviteId,
                packet.InviterUid, packet.RequestTimestamp, packet.RequestCount);
            partyInvitePromptDialog.TryOpen();
        }

        private void OnPartyStateReceived(PartyStatePacket packet)
        {
            currentPartyState = packet;
            partyListHud?.UpdatePartyState(packet);
        }

        private void OnPartyDisbandedReceived(PartyDisbandedPacket packet)
        {
            currentPartyState = null;
            partyListHud?.ClearPartyState();

            string message = packet.Reason switch
            {
                "kicked" => "You were kicked from the party.",
                "left" => "You left the party.",
                "leader_left" => "The party leader left. Party disbanded.",
                "disbanded" => "The party was disbanded.",
                _ => "You are no longer in a party."
            };
            capi.ShowChatMessage($"[BuddyBeacon] {message}");
        }

        private void OnPartyInviteResultReceived(PartyInviteResultPacket packet)
        {
            capi.ShowChatMessage($"[BuddyBeacon] {packet.Message}");
        }

        #endregion

        #region Public API for Items

        public void OpenPlayerSelectDialog(TeleportRequestType requestType)
        {
            if (capi == null) return;

            playerSelectDialog = new GuiDialogPlayerSelect(capi, requestType);
            playerSelectDialog.TryOpen();
            capi.Network.GetChannel(ChannelName).SendPacket(new PlayerListRequestPacket());
        }

        public void OpenBeaconCodeDialog(ItemSlot slot, string currentCode)
        {
            if (capi == null) return;

            beaconCodeDialog = new GuiDialogBeaconCode(capi, slot, currentCode);
            beaconCodeDialog.TryOpen();
        }

        public void SendTeleportRequest(string targetUid, TeleportRequestType requestType)
        {
            capi?.Network.GetChannel(ChannelName).SendPacket(new TeleportRequestPacket
            {
                RequesterPlayerUid = capi.World.Player.PlayerUID,
                TargetPlayerUid = targetUid,
                RequestType = requestType
            });
        }

        public void SendTeleportResponse(long requestId, bool accepted)
        {
            capi?.Network.GetChannel(ChannelName).SendPacket(new TeleportResponsePacket
            {
                RequestId = requestId,
                Accepted = accepted,
                ResponderPlayerUid = capi.World.Player.PlayerUID
            });
        }

        public void SendSilencePlayer(string playerUidToSilence)
        {
            capi?.Network.GetChannel(ChannelName).SendPacket(new SilencePlayerPacket
            {
                PlayerUidToSilence = playerUidToSilence
            });
        }

        public void SendBeaconCodeUpdate(string beaconCode)
        {
            capi?.Network.GetChannel(ChannelName).SendPacket(new BeaconCodeSetPacket
            {
                BeaconCode = beaconCode
            });
        }

        public void SendMapPing(double posX, double posZ)
        {
            capi?.Network.GetChannel(ChannelName).SendPacket(new MapPingPacket
            {
                PosX = posX,
                PosZ = posZ
            });
        }

        // Party API methods

        public void SendPartyInvite(string targetUid)
        {
            capi?.Network.GetChannel(ChannelName).SendPacket(new PartyInvitePacket
            {
                TargetPlayerUid = targetUid
            });
        }

        public void SendPartyInviteResponse(long inviteId, bool accepted)
        {
            capi?.Network.GetChannel(ChannelName).SendPacket(new PartyInviteResponsePacket
            {
                InviteId = inviteId,
                Accepted = accepted
            });
        }

        private void AddFakePartyMember(string name, string uid)
        {
            if (capi?.World?.Player?.Entity?.Pos == null) return;

            fakePartyMemberName = name;
            fakePartyMemberUid = uid;

            // Register a tick handler to keep updating the fake buddy
            capi.Event.RegisterGameTickListener(UpdateFakePartyMember, 1000);
        }

        private void UpdateFakePartyMember(float dt)
        {
            if (!partyTestMode || capi?.World?.Player?.Entity?.Pos == null || string.IsNullOrEmpty(fakePartyMemberName))
            {
                return;
            }

            // Use combined update to include .buddyfake buddies if active
            UpdateAllFakeBuddies();
        }

        public void SendPartyLeave()
        {
            capi?.Network.GetChannel(ChannelName).SendPacket(new PartyLeavePacket());
        }

        public void SendPartyKick(string targetUid)
        {
            capi?.Network.GetChannel(ChannelName).SendPacket(new PartyKickPacket
            {
                TargetPlayerUid = targetUid
            });
        }

        public void SendPartyMakeLead(string targetUid)
        {
            capi?.Network.GetChannel(ChannelName).SendPacket(new PartyMakeLeadPacket
            {
                TargetPlayerUid = targetUid
            });
        }

        public PartyStatePacket GetCurrentPartyState() => currentPartyState;

        public bool IsPartyLeader()
        {
            if (currentPartyState == null || capi?.World?.Player == null)
                return false;
            return currentPartyState.LeaderUid == capi.World.Player.PlayerUID;
        }

        public bool IsInParty() => currentPartyState != null;

        public string GetPartyMemberUid(string playerName)
        {
            if (currentPartyState == null) return null;
            int index = Array.IndexOf(currentPartyState.MemberNames, playerName);
            if (index >= 0 && index < currentPartyState.MemberUids.Length)
                return currentPartyState.MemberUids[index];
            return null;
        }

        #endregion
    }

    public class PendingTeleportRequest
    {
        public long RequestId { get; set; }
        public string RequesterUid { get; set; }
        public string TargetUid { get; set; }
        public TeleportRequestType RequestType { get; set; }
        public long RequestTime { get; set; }
    }

    /// <summary>
    /// Tracks last-sent buddy state for delta compression
    /// </summary>
    public class LastSentBuddyState
    {
        public double PosX { get; set; }
        public double PosY { get; set; }
        public double PosZ { get; set; }
        public float Health { get; set; }
        public float MaxHealth { get; set; }
        public float Saturation { get; set; }
        public float MaxSaturation { get; set; }
        public long LastSentTime { get; set; }

        /// <summary>
        /// Check if position has changed beyond threshold
        /// </summary>
        public bool HasPositionChanged(double x, double y, double z, float threshold)
        {
            if (threshold <= 0) return true;  // Always send if threshold is 0
            double dx = x - PosX;
            double dy = y - PosY;
            double dz = z - PosZ;
            double distSq = dx * dx + dy * dy + dz * dz;
            return distSq >= threshold * threshold;
        }

        /// <summary>
        /// Check if health has changed beyond threshold
        /// </summary>
        public bool HasHealthChanged(float health, float maxHealth, float threshold)
        {
            return Math.Abs(health - Health) >= threshold || Math.Abs(maxHealth - MaxHealth) >= 0.1f;
        }

        /// <summary>
        /// Check if saturation has changed beyond threshold
        /// </summary>
        public bool HasSaturationChanged(float saturation, float maxSaturation, float threshold)
        {
            return Math.Abs(saturation - Saturation) >= threshold || Math.Abs(maxSaturation - MaxSaturation) >= 0.1f;
        }

        public void Update(double x, double y, double z, float health, float maxHealth, float saturation, float maxSaturation, long time)
        {
            PosX = x;
            PosY = y;
            PosZ = z;
            Health = health;
            MaxHealth = maxHealth;
            Saturation = saturation;
            MaxSaturation = maxSaturation;
            LastSentTime = time;
        }

        /// <summary>
        /// Update only position and time - use when health/saturation wasn't sent
        /// to avoid resetting the change detection baseline for those values
        /// </summary>
        public void UpdatePositionOnly(double x, double y, double z, long time)
        {
            PosX = x;
            PosY = y;
            PosZ = z;
            LastSentTime = time;
        }
    }
}

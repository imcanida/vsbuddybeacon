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

        // Client-side references
        private ICoreClientAPI capi;
        private GuiDialogPlayerSelect playerSelectDialog;
        private GuiDialogTeleportPrompt teleportPromptDialog;
        private GuiDialogBeaconCode beaconCodeDialog;
        private HudElementBuddyCompass buddyCompassHud;
        private BuddyMapLayer buddyMapLayer;

        // Server-side reference
        private ICoreServerAPI sapi;

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
                .SetMessageHandler<TeleportRequestPacket>(OnTeleportRequestReceived)
                .SetMessageHandler<TeleportResponsePacket>(OnTeleportResponseReceived)
                .SetMessageHandler<PlayerListRequestPacket>(OnPlayerListRequest)
                .SetMessageHandler<BeaconCodeSetPacket>(OnBeaconCodeSet)
                .SetMessageHandler<SilencePlayerPacket>(OnSilencePlayer);

            // Configure recipes after assets load - will be called via AssetsFinalize override

            // Register tick handlers
            api.Event.RegisterGameTickListener(CheckRequestTimeouts, 1000);  // Every 1000ms
            api.Event.RegisterGameTickListener(BroadcastBeaconPositions, (int)(BEACON_UPDATE_INTERVAL * 1000));

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
                .SetMessageHandler<TeleportPromptPacket>(OnTeleportPromptReceived)
                .SetMessageHandler<TeleportResultPacket>(OnTeleportResultReceived)
                .SetMessageHandler<PlayerListResponsePacket>(OnPlayerListReceived)
                .SetMessageHandler<BeaconPositionPacket>(OnBeaconPositionsReceived);

            // Create buddy compass HUD and register it properly
            buddyCompassHud = new HudElementBuddyCompass(capi);
            capi.Gui.LoadedGuis.Add(buddyCompassHud);

            // Register buddy map layer with the world map
            var worldMapManager = api.ModLoader.GetModSystem<WorldMapManager>();
            if (worldMapManager != null)
            {
                buddyMapLayer = new BuddyMapLayer(api, worldMapManager);
                worldMapManager.MapLayers.Add(buddyMapLayer);
                api.Logger.Notification("[VSBuddyBeacon] Buddy map layer registered");
            }

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

            api.Logger.Notification("[VSBuddyBeacon] Client-side initialized");
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

        private void BroadcastBeaconPositions(float dt)
        {
            long currentTime = sapi.World.ElapsedMilliseconds;  // Capture timestamp once for consistency

            // Group players by beacon code (from worn/inventory items OR manual setting)
            var codeGroups = new Dictionary<string, List<IServerPlayer>>();

            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player?.Entity == null) continue;

                // Priority: playerBeaconCodes (instant) over item attributes (slow sync)
                // This fixes race condition where code changes take 1-2s to sync via item attributes
                string code = null;
                if (playerBeaconCodes.TryGetValue(player.PlayerUID, out string manualCode))
                {
                    code = manualCode;  // Manual code takes precedence
                }

                // Fall back to beacon band in inventory if no manual code set
                if (string.IsNullOrEmpty(code))
                {
                    code = GetPlayerBeaconCode(player);
                }

                if (string.IsNullOrEmpty(code)) continue;

                if (!codeGroups.ContainsKey(code))
                    codeGroups[code] = new List<IServerPlayer>();

                codeGroups[code].Add(player);
            }

            // Debug: Log groups found
            foreach (var kvp in codeGroups)
            {
                if (kvp.Value.Count >= 2)
                {
                    LogDebug(sapi, $"[VSBuddyBeacon] Beacon group '{kvp.Key}': {string.Join(", ", kvp.Value.Select(p => p.PlayerName))}");
                }
            }

            // For each group with 2+ players, send positions to all members
            foreach (var group in codeGroups.Values)
            {
                if (group.Count < 2) continue;

                foreach (var recipient in group)
                {
                    // Get other players in same group (not self)
                    var others = group.Where(p => p.PlayerUID != recipient.PlayerUID).ToList();

                    var packet = new BeaconPositionPacket
                    {
                        PlayerNames = others.Select(p => p.PlayerName).ToArray(),
                        PosX = others.Select(p => p.Entity.Pos.X).ToArray(),
                        PosY = others.Select(p => p.Entity.Pos.Y).ToArray(),
                        PosZ = others.Select(p => p.Entity.Pos.Z).ToArray(),
                        Timestamps = others.Select(_ => currentTime).ToArray()
                    };

                    LogDebug(sapi, $"[VSBuddyBeacon] Sending {others.Count} buddy positions to {recipient.PlayerName}");
                    sapi.Network.GetChannel(ChannelName).SendPacket(packet, recipient);
                }
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

            if (packet.PlayerNames == null || packet.PlayerNames.Length == 0)
            {
                buddyCompassHud?.UpdateBuddyPositions(new List<BuddyPositionWithTimestamp>());
                buddyMapLayer?.UpdateBuddyPositions(new List<BuddyPositionWithTimestamp>());
                return;
            }

            var positions = new List<BuddyPositionWithTimestamp>();
            for (int i = 0; i < packet.PlayerNames.Length; i++)
            {
                long serverTimestamp = packet.Timestamps != null && i < packet.Timestamps.Length
                    ? packet.Timestamps[i]
                    : clientReceiveTime;  // Fallback for backward compatibility

                positions.Add(new BuddyPositionWithTimestamp
                {
                    Name = packet.PlayerNames[i],
                    Position = new Vec3d(packet.PosX[i], packet.PosY[i], packet.PosZ[i]),
                    ServerTimestamp = serverTimestamp,
                    ClientReceivedTime = clientReceiveTime
                });
            }

            buddyCompassHud?.UpdateBuddyPositions(positions);
            buddyMapLayer?.UpdateBuddyPositions(positions);
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
}

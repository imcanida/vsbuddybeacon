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

        // Server-side: Track pending requests
        private Dictionary<long, PendingTeleportRequest> pendingRequests = new();
        private long nextRequestId = 1;

        // Server-side: Track player beacon codes (PlayerUID -> BeaconCode)
        private Dictionary<string, string> playerBeaconCodes = new();

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

            // Register items ONLY if enabled
            if (config.IsItemEnabled("wayfindercompass"))
                api.RegisterItemClass("ItemWayfinderCompass", typeof(ItemWayfinderCompass));

            if (config.IsItemEnabled("heroscallstone"))
                api.RegisterItemClass("ItemHerosCallStone", typeof(ItemHerosCallStone));

            if (config.IsItemEnabled("beaconband"))
                api.RegisterItemClass("ItemBeaconBand", typeof(ItemBeaconBand));
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

            // Configure recipes after assets are loaded
            if (api.Side == EnumAppSide.Server)
            {
                ConfigureRecipes();
            }
        }

        #region Recipe Configuration

        private void ConfigureRecipes()
        {
            foreach (var itemKvp in config.Items)
            {
                string itemCode = itemKvp.Key;
                var itemConfig = itemKvp.Value;

                if (!itemConfig.Enabled)
                    continue; // Item disabled, skip recipe config

                if (!itemConfig.AllowCrafting)
                {
                    // Remove default recipe
                    RemoveRecipe(itemCode);
                }
                else if (itemConfig.CustomRecipe != null)
                {
                    // Remove default, add custom
                    RemoveRecipe(itemCode);
                    AddCustomRecipe(itemCode, itemConfig.CustomRecipe);
                }
                // else: Keep default recipe
            }
        }

        private void RemoveRecipe(string itemCode)
        {
            int removed = sapi.World.GridRecipes.RemoveAll(r =>
                r.Output?.ResolvedItemstack?.Item?.Code?.Path == itemCode);

            if (removed > 0)
                sapi.Logger.Notification($"[VSBuddyBeacon] Removed {removed} recipe(s) for {itemCode}");
        }

        private void AddCustomRecipe(string itemCode, CustomRecipe recipeConfig)
        {
            try
            {
                var recipe = ParseCustomRecipe(itemCode, recipeConfig);
                sapi.World.GridRecipes.Add(recipe);
                sapi.Logger.Notification($"[VSBuddyBeacon] Added custom recipe for {itemCode}");
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"[VSBuddyBeacon] Error loading custom recipe for {itemCode}: {ex.Message}");
            }
        }

        private GridRecipe ParseCustomRecipe(string itemCode, CustomRecipe config)
        {
            var recipe = new GridRecipe();

            recipe.Width = config.Width;
            recipe.Height = config.Height;
            var ingredients = new GridRecipeIngredient[config.Width * config.Height];

            // Parse pattern string (e.g., "GCG,ITI,GRG")
            var patternRows = config.IngredientPattern.Split(',');

            for (int row = 0; row < config.Height; row++)
            {
                string rowPattern = patternRows[row];

                for (int col = 0; col < config.Width; col++)
                {
                    char key = rowPattern[col];

                    if (key == ' ') continue; // Empty slot

                    if (!config.Ingredients.TryGetValue(key.ToString(), out var ingredientDef))
                    {
                        sapi.Logger.Warning($"[VSBuddyBeacon] No ingredient defined for key '{key}'");
                        continue;
                    }

                    ingredients[row * config.Width + col] = new GridRecipeIngredient
                    {
                        Type = ingredientDef.Type == "item" ? EnumItemClass.Item : EnumItemClass.Block,
                        Code = AssetLocation.Create(ingredientDef.Code, "game"),
                        Quantity = ingredientDef.Quantity,
                        AllowedVariants = ingredientDef.AllowedVariants
                    };
                }
            }

            recipe.resolvedIngredients = ingredients;

            // Set output
            recipe.Output = new CraftingRecipeIngredient
            {
                Type = EnumItemClass.Item,
                Code = new AssetLocation($"vsbuddybeacon:{itemCode}"),
                Quantity = config.Output.Quantity
            };

            recipe.Name = AssetLocation.Create($"custom_{itemCode}", "vsbuddybeacon");

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
                sapi.Logger.Debug($"[VSBuddyBeacon] Gave {itemCode} to {player.PlayerName}");
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
                .SetMessageHandler<TeleportRequestPacket>(OnTeleportRequestReceived)
                .SetMessageHandler<TeleportResponsePacket>(OnTeleportResponseReceived)
                .SetMessageHandler<PlayerListRequestPacket>(OnPlayerListRequest)
                .SetMessageHandler<BeaconCodeSetPacket>(OnBeaconCodeSet);

            // Configure recipes after assets load - will be called via AssetsFinalize override

            // Register tick handlers
            api.Event.Timer(CheckRequestTimeouts, 1.0);
            api.Event.Timer(BroadcastBeaconPositions, BEACON_UPDATE_INTERVAL);

            // Clean up beacon codes when player disconnects
            api.Event.PlayerDisconnect += OnPlayerDisconnect;

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
                sapi.Logger.Notification($"[VSBuddyBeacon] {fromPlayer.PlayerName} cleared their beacon code");
            }
            else
            {
                // Set beacon
                playerBeaconCodes[fromPlayer.PlayerUID] = code;
                sapi.Logger.Notification($"[VSBuddyBeacon] {fromPlayer.PlayerName} set beacon code to \"{code}\"");
            }
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            playerBeaconCodes.Remove(player.PlayerUID);
        }

        /// <summary>
        /// Searches player's hotbar and backpack for a beacon band with a code
        /// </summary>
        private string GetPlayerBeaconCode(IServerPlayer player)
        {
            var inv = player.InventoryManager;
            if (inv == null) return null;

            // Only check hotbar and backpack - avoid creative/other special inventories
            var hotbar = inv.GetOwnInventory("hotbar");
            var backpack = inv.GetOwnInventory("backpack");

            foreach (var inventory in new[] { hotbar, backpack })
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

        private void BroadcastBeaconPositions()
        {
            // Group players by beacon code (from worn/inventory items OR manual setting)
            var codeGroups = new Dictionary<string, List<IServerPlayer>>();

            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player?.Entity == null) continue;

                // First check if player has a beacon band equipped or in inventory
                string code = GetPlayerBeaconCode(player);

                // Fall back to manually set beacon code if no band found
                if (string.IsNullOrEmpty(code) && playerBeaconCodes.TryGetValue(player.PlayerUID, out string manualCode))
                {
                    code = manualCode;
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
                    sapi.Logger.Debug($"[VSBuddyBeacon] Beacon group '{kvp.Key}': {string.Join(", ", kvp.Value.Select(p => p.PlayerName))}");
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
                        PosZ = others.Select(p => p.Entity.Pos.Z).ToArray()
                    };

                    sapi.Logger.Debug($"[VSBuddyBeacon] Sending {others.Count} buddy positions to {recipient.PlayerName}");
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

            if (!HasTeleportItem(fromPlayer, packet.RequestType))
            {
                string itemName = packet.RequestType == TeleportRequestType.TeleportTo
                    ? "Wayfinder's Compass"
                    : "Hero's Call Stone";
                SendResult(fromPlayer, false, $"You need a {itemName} to do this.");
                return;
            }

            var request = new PendingTeleportRequest
            {
                RequestId = nextRequestId++,
                RequesterUid = fromPlayer.PlayerUID,
                TargetUid = targetPlayer.PlayerUID,
                RequestType = packet.RequestType,
                RequestTime = sapi.World.ElapsedMilliseconds
            };

            pendingRequests[request.RequestId] = request;

            var prompt = new TeleportPromptPacket
            {
                RequesterName = fromPlayer.PlayerName,
                RequestType = packet.RequestType,
                RequestId = request.RequestId
            };

            sapi.Network.GetChannel(ChannelName).SendPacket(prompt, targetPlayer);
            SendResult(fromPlayer, true, $"Request sent to {targetPlayer.PlayerName}. Waiting for response...");

            sapi.Logger.Notification($"[VSBuddyBeacon] {fromPlayer.PlayerName} requested to {(packet.RequestType == TeleportRequestType.TeleportTo ? "teleport to" : "summon")} {targetPlayer.PlayerName}");
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

            sapi.Logger.Notification($"[VSBuddyBeacon] Teleport executed: {playerToMove.PlayerName} -> {(requestType == TeleportRequestType.TeleportTo ? target : requester).PlayerName}");
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

        private void CheckRequestTimeouts()
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

                if (requester != null)
                    SendResult(requester, false, "Teleport request timed out.");
            }
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

            teleportPromptDialog = new GuiDialogTeleportPrompt(capi, message, packet.RequestId);
            teleportPromptDialog.TryOpen();
        }

        private void OnTeleportResultReceived(TeleportResultPacket packet)
        {
            capi.ShowChatMessage($"[BuddyBeacon] {packet.Message}");
            playerSelectDialog?.TryClose();
        }

        private void OnBeaconPositionsReceived(BeaconPositionPacket packet)
        {
            if (packet.PlayerNames == null || packet.PlayerNames.Length == 0)
            {
                buddyCompassHud?.UpdateBuddyPositions(new List<BuddyPosition>());
                buddyMapLayer?.UpdateBuddyPositions(new List<BuddyPosition>());
                return;
            }

            var positions = new List<BuddyPosition>();
            for (int i = 0; i < packet.PlayerNames.Length; i++)
            {
                positions.Add(new BuddyPosition
                {
                    Name = packet.PlayerNames[i],
                    Position = new Vec3d(packet.PosX[i], packet.PosY[i], packet.PosZ[i])
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

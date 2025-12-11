using System;
using System.Collections.Generic;
using System.Text;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VSBuddyBeacon
{
    public class BuddyMapLayer : MapLayer
    {
        private Dictionary<string, BuddyMapComponent> buddyComponents = new Dictionary<string, BuddyMapComponent>();
        private Dictionary<string, PingMapComponent> activePings = new Dictionary<string, PingMapComponent>();  // Key = sender name, only 1 ping per person
        private ICoreClientAPI capi;
        private Dictionary<StalenessLevel, LoadedTexture> buddyTextures = new Dictionary<StalenessLevel, LoadedTexture>();
        private Dictionary<string, LoadedTexture> pinnedTextures = new Dictionary<string, LoadedTexture>();  // Custom colored textures for pinned buddies
        private Dictionary<string, (double r, double g, double b)> pinnedPlayers = new Dictionary<string, (double r, double g, double b)>();

        public override string Title => "Buddy Beacons";
        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
        public override string LayerGroupCode => "buddybeacons";

        /// <summary>
        /// Controls whether map pinging is enabled (middle-click to ping)
        /// </summary>
        public bool PingsEnabled { get; set; } = true;

        public void UpdatePinnedPlayers(Dictionary<string, (double r, double g, double b)> pinned)
        {
            // Dispose old pinned textures
            foreach (var tex in pinnedTextures.Values)
            {
                tex?.Dispose();
            }
            pinnedTextures.Clear();

            pinnedPlayers = pinned ?? new Dictionary<string, (double r, double g, double b)>();

            // Create textures for pinned players
            int size = (int)GuiElement.scaled(24);
            foreach (var kvp in pinnedPlayers)
            {
                pinnedTextures[kvp.Key] = CreateColoredTexture(size, kvp.Value.r, kvp.Value.g, kvp.Value.b);
            }
        }

        private LoadedTexture CreateColoredTexture(int size, double r, double g, double b)
        {
            ImageSurface surface = new ImageSurface(Format.Argb32, size, size);
            Context ctx = new Context(surface);

            ctx.SetSourceRGBA(0, 0, 0, 0);
            ctx.Paint();

            double cx = size / 2.0;
            double cy = size / 2.0;
            double radius = size / 2.0 - 2;

            // White border
            ctx.SetSourceRGBA(1, 1, 1, 1);
            ctx.Arc(cx, cy, radius, 0, 2 * Math.PI);
            ctx.Fill();

            // Colored fill
            ctx.SetSourceRGBA(r, g, b, 1);
            ctx.Arc(cx, cy, radius - 2, 0, 2 * Math.PI);
            ctx.Fill();

            var texture = new LoadedTexture(capi, capi.Gui.LoadCairoTexture(surface, false), size / 2, size / 2);

            ctx.Dispose();
            surface.Dispose();

            return texture;
        }

        public BuddyMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
        {
            capi = api as ICoreClientAPI;
        }

        private void EnsureTexturesCreated()
        {
            if (capi == null || buddyTextures.Count > 0) return;

            int size = (int)GuiElement.scaled(24);

            // Create texture for each staleness level
            CreateTextureForLevel(StalenessLevel.Fresh, size, 0.2, 0.8, 0.2);      // Green
            CreateTextureForLevel(StalenessLevel.Aging, size, 0.9, 0.9, 0.2);      // Yellow
            CreateTextureForLevel(StalenessLevel.Stale, size, 1.0, 0.6, 0.0);      // Orange
            CreateTextureForLevel(StalenessLevel.VeryStale, size, 0.9, 0.1, 0.1);  // Red
        }

        private void CreateTextureForLevel(StalenessLevel level, int size, double r, double g, double b)
        {
            ImageSurface surface = new ImageSurface(Format.Argb32, size, size);
            Context ctx = new Context(surface);

            // Clear
            ctx.SetSourceRGBA(0, 0, 0, 0);
            ctx.Paint();

            // Draw a circle with white border and colored fill
            double cx = size / 2.0;
            double cy = size / 2.0;
            double radius = size / 2.0 - 2;

            // White border
            ctx.SetSourceRGBA(1, 1, 1, 1);
            ctx.Arc(cx, cy, radius, 0, 2 * Math.PI);
            ctx.Fill();

            // Colored fill (based on staleness level)
            ctx.SetSourceRGBA(r, g, b, 1);
            ctx.Arc(cx, cy, radius - 2, 0, 2 * Math.PI);
            ctx.Fill();

            buddyTextures[level] = new LoadedTexture(capi, capi.Gui.LoadCairoTexture(surface, false), size / 2, size / 2);

            ctx.Dispose();
            surface.Dispose();
        }

        public override void OnMapOpenedClient()
        {
            EnsureTexturesCreated();

            // Clear existing components
            foreach (var comp in buddyComponents.Values)
            {
                comp?.Dispose();
            }
            buddyComponents.Clear();
        }

        public void UpdateBuddyPositions(List<BuddyPositionWithTimestamp> positions)
        {
            if (capi == null) return;
            EnsureTexturesCreated();
            if (buddyTextures.Count == 0) return;

            long currentTime = capi.World.ElapsedMilliseconds;

            // Update or create components for each buddy
            HashSet<string> currentBuddies = new HashSet<string>();

            foreach (var buddy in positions)
            {
                // Skip expired (already too old)
                var staleness = buddy.GetStalenessLevel(currentTime);
                if (staleness == StalenessLevel.Expired)
                    continue;

                currentBuddies.Add(buddy.Name);

                // Check if this buddy is pinned and has a custom texture
                LoadedTexture pinnedTexture = null;
                pinnedTextures.TryGetValue(buddy.Name, out pinnedTexture);

                if (buddyComponents.TryGetValue(buddy.Name, out var existing))
                {
                    // Update existing - pass the received time so it can track staleness
                    existing.UpdatePosition(buddy.Position, buddy.ClientReceivedTime);
                    existing.CustomTexture = pinnedTexture;  // Update custom texture (may be null)
                }
                else
                {
                    // Create new - pass received time instead of staleness level
                    var comp = new BuddyMapComponent(capi, buddyTextures, buddy.Name, buddy.Position, buddy.ClientReceivedTime);
                    comp.CustomTexture = pinnedTexture;
                    buddyComponents[buddy.Name] = comp;
                }
            }

            // Remove buddies that are no longer in the list
            List<string> toRemove = new List<string>();
            foreach (var name in buddyComponents.Keys)
            {
                if (!currentBuddies.Contains(name))
                {
                    toRemove.Add(name);
                }
            }
            foreach (var name in toRemove)
            {
                buddyComponents[name]?.Dispose();
                buddyComponents.Remove(name);
            }
        }

        public override void Render(GuiElementMap mapElem, float dt)
        {
            if (!Active) return;

            foreach (var comp in buddyComponents.Values)
            {
                comp.Render(mapElem, dt);
            }

            // Render pings and clean up expired ones
            var expiredPings = new List<string>();
            foreach (var kvp in activePings)
            {
                if (kvp.Value.IsExpired())
                {
                    kvp.Value.Dispose();
                    expiredPings.Add(kvp.Key);
                }
                else
                {
                    kvp.Value.Render(mapElem, dt);
                }
            }
            foreach (var key in expiredPings)
            {
                activePings.Remove(key);
            }
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            if (!Active) return;

            foreach (var comp in buddyComponents.Values)
            {
                comp.OnMouseMove(args, mapElem, hoverText);
            }

            // Check ping hover
            foreach (var ping in activePings.Values)
            {
                ping.OnMouseMove(args, mapElem, hoverText);
            }
        }

        public override void OnMouseUpClient(MouseEvent args, GuiElementMap mapElem)
        {
            if (!Active) return;

            // Middle click to ping (if enabled)
            if (args.Button == EnumMouseButton.Middle && PingsEnabled)
            {
                // Convert view position to world position
                float relX = (float)(args.X - mapElem.Bounds.renderX);
                float relY = (float)(args.Y - mapElem.Bounds.renderY);

                var bounds = mapElem.CurrentBlockViewBounds;
                double boundsWidth = bounds.X2 - bounds.X1;
                double boundsHeight = bounds.Z2 - bounds.Z1;

                double worldX = bounds.X1 + (relX / mapElem.Bounds.OuterWidth) * boundsWidth;
                double worldZ = bounds.Z1 + (relY / mapElem.Bounds.OuterHeight) * boundsHeight;

                // Send ping to server
                var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
                modSystem?.SendMapPing(worldX, worldZ);

                args.Handled = true;
            }
        }

        public void AddPing(string senderName, double posX, double posZ, long timestamp)
        {
            // Replace existing ping from same sender (only 1 ping per person visible)
            if (activePings.TryGetValue(senderName, out var existingPing))
            {
                existingPing.Dispose();
            }

            var ping = new PingMapComponent(capi, senderName, posX, posZ, timestamp);
            activePings[senderName] = ping;
        }

        public override void Dispose()
        {
            foreach (var comp in buddyComponents.Values)
            {
                comp?.Dispose();
            }
            buddyComponents.Clear();

            foreach (var ping in activePings.Values)
            {
                ping?.Dispose();
            }
            activePings.Clear();

            foreach (var texture in buddyTextures.Values)
            {
                texture?.Dispose();
            }
            buddyTextures.Clear();

            foreach (var texture in pinnedTextures.Values)
            {
                texture?.Dispose();
            }
            pinnedTextures.Clear();

            base.Dispose();
        }
    }
}

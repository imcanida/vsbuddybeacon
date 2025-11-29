using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VSBuddyBeacon
{
    public class BuddyMapComponent
    {
        public string PlayerName { get; set; }
        public Vec3d Position { get; set; }
        public long ClientReceivedTime { get; set; }  // Track when we last received an update

        private ICoreClientAPI capi;
        private System.Collections.Generic.Dictionary<StalenessLevel, LoadedTexture> textures;
        private Vec2f viewPos = new Vec2f();

        public BuddyMapComponent(ICoreClientAPI capi, System.Collections.Generic.Dictionary<StalenessLevel, LoadedTexture> textures, string playerName, Vec3d position, long clientReceivedTime)
        {
            this.capi = capi;
            this.textures = textures;
            this.PlayerName = playerName;
            this.Position = position;
            this.ClientReceivedTime = clientReceivedTime;
        }

        public void UpdatePosition(Vec3d newPosition, long newReceivedTime)
        {
            Position = newPosition;
            ClientReceivedTime = newReceivedTime;
        }

        /// <summary>
        /// Calculate staleness based on current time
        /// </summary>
        private StalenessLevel GetCurrentStaleness()
        {
            long currentTime = capi.World.ElapsedMilliseconds;
            float age = (currentTime - ClientReceivedTime) / 1000f;

            if (age < 3f) return StalenessLevel.Fresh;
            if (age < 10f) return StalenessLevel.Aging;
            if (age < 30f) return StalenessLevel.Stale;
            if (age < 60f) return StalenessLevel.VeryStale;
            return StalenessLevel.Expired;
        }

        public void Render(GuiElementMap mapElem, float dt)
        {
            if (Position == null) return;

            // Calculate current staleness based on time
            var staleness = GetCurrentStaleness();

            // Don't render if expired
            if (staleness == StalenessLevel.Expired)
                return;

            // Get texture for current staleness level
            if (!textures.TryGetValue(staleness, out var currentTexture))
                return;

            mapElem.TranslateWorldPosToViewPos(Position, ref viewPos);

            float x = viewPos.X;
            float y = viewPos.Y;

            // Check if position is visible on map
            if (x < -10 || y < -10 || x > mapElem.Bounds.OuterWidth + 10 || y > mapElem.Bounds.OuterHeight + 10)
            {
                return;
            }

            float size = (float)GuiElement.scaled(16);

            capi.Render.GlToggleBlend(true);

            // Render the marker texture (high z-depth to render on top of map tiles)
            capi.Render.Render2DTexturePremultipliedAlpha(
                currentTexture.TextureId,
                (float)(mapElem.Bounds.renderX + x - size/2),
                (float)(mapElem.Bounds.renderY + y - size/2),
                size,
                size,
                500f
            );
        }

        public void OnMouseMove(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            if (Position == null) return;

            mapElem.TranslateWorldPosToViewPos(Position, ref viewPos);

            float x = (float)(mapElem.Bounds.renderX + viewPos.X);
            float y = (float)(mapElem.Bounds.renderY + viewPos.Y);

            double mouseX = args.X;
            double mouseY = args.Y;

            double hitboxSize = GuiElement.scaled(8);

            if (Math.Abs(mouseX - x) < hitboxSize && Math.Abs(mouseY - y) < hitboxSize)
            {
                // Calculate distance from local player
                var localPlayer = capi.World.Player?.Entity;
                if (localPlayer != null)
                {
                    double distance = Position.DistanceTo(localPlayer.Pos.XYZ);
                    string distanceStr = distance < 1000 ? $"{(int)distance}m" : $"{distance / 1000:F1}km";
                    hoverText.AppendLine($"{PlayerName} ({distanceStr})");
                }
                else
                {
                    hoverText.AppendLine(PlayerName);
                }
            }
        }

        public void Dispose()
        {
            // Don't dispose texture here - it's shared
        }
    }
}

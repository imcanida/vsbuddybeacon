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

        private ICoreClientAPI capi;
        private LoadedTexture texture;
        private Vec2f viewPos = new Vec2f();

        public BuddyMapComponent(ICoreClientAPI capi, LoadedTexture texture, string playerName, Vec3d position)
        {
            this.capi = capi;
            this.texture = texture;
            this.PlayerName = playerName;
            this.Position = position;
        }

        public void UpdatePosition(Vec3d newPosition)
        {
            Position = newPosition;
        }

        public void Render(GuiElementMap mapElem, float dt)
        {
            if (Position == null) return;

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

            // Render the marker texture
            capi.Render.Render2DTexturePremultipliedAlpha(
                texture.TextureId,
                (float)(mapElem.Bounds.renderX + x - size/2),
                (float)(mapElem.Bounds.renderY + y - size/2),
                size,
                size,
                50f
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

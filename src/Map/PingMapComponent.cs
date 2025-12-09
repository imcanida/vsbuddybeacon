using System;
using System.Text;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VSBuddyBeacon
{
    public class PingMapComponent
    {
        private const float PING_DURATION_SECONDS = 10f;
        private const float PULSE_CYCLE_SECONDS = 1.5f;

        public string SenderName { get; set; }
        public Vec3d Position { get; set; }
        public long CreatedTime { get; set; }

        private ICoreClientAPI capi;
        private Vec2f viewPos = new Vec2f();
        private LoadedTexture pingTexture;

        public PingMapComponent(ICoreClientAPI capi, string senderName, double posX, double posZ, long createdTime)
        {
            this.capi = capi;
            this.SenderName = senderName;
            this.Position = new Vec3d(posX, 0, posZ);
            this.CreatedTime = createdTime;
            CreateTexture();
        }

        private void CreateTexture()
        {
            int size = (int)GuiElement.scaled(24);
            ImageSurface surface = new ImageSurface(Format.Argb32, size, size);
            Context ctx = new Context(surface);

            // Clear
            ctx.SetSourceRGBA(0, 0, 0, 0);
            ctx.Paint();

            // Draw a blue circle with white border
            double cx = size / 2.0;
            double cy = size / 2.0;
            double radius = size / 2.0 - 2;

            // White border
            ctx.SetSourceRGBA(1, 1, 1, 1);
            ctx.Arc(cx, cy, radius, 0, 2 * Math.PI);
            ctx.Fill();

            // Blue fill
            ctx.SetSourceRGBA(0.3, 0.6, 1.0, 1);
            ctx.Arc(cx, cy, radius - 2, 0, 2 * Math.PI);
            ctx.Fill();

            pingTexture = new LoadedTexture(capi, capi.Gui.LoadCairoTexture(surface, false), size / 2, size / 2);

            ctx.Dispose();
            surface.Dispose();
        }

        public bool IsExpired()
        {
            long currentTime = capi.World.ElapsedMilliseconds;
            float age = (currentTime - CreatedTime) / 1000f;
            return age >= PING_DURATION_SECONDS;
        }

        public void Render(GuiElementMap mapElem, float dt)
        {
            if (Position == null || IsExpired() || pingTexture == null) return;

            mapElem.TranslateWorldPosToViewPos(Position, ref viewPos);

            float x = viewPos.X;
            float y = viewPos.Y;

            // Check if position is visible on map
            if (x < -50 || y < -50 || x > mapElem.Bounds.OuterWidth + 50 || y > mapElem.Bounds.OuterHeight + 50)
            {
                return;
            }

            long currentTime = capi.World.ElapsedMilliseconds;
            float age = (currentTime - CreatedTime) / 1000f;

            // Calculate animation phase (0 to 1, repeating)
            float pulsePhase = (age % PULSE_CYCLE_SECONDS) / PULSE_CYCLE_SECONDS;

            // Calculate overall fade (1.0 at start, 0.0 at end)
            float overallFade = 1.0f - (age / PING_DURATION_SECONDS);

            // Pulsing size effect
            float pulseScale = 1.0f + 0.3f * (float)Math.Sin(pulsePhase * Math.PI * 2);
            float size = (float)GuiElement.scaled(16) * pulseScale;

            // Render position on map
            float renderX = (float)(mapElem.Bounds.renderX + x - size / 2);
            float renderY = (float)(mapElem.Bounds.renderY + y - size / 2);

            capi.Render.GlToggleBlend(true);

            // Apply alpha based on fade
            float alpha = overallFade;

            // Render the ping texture
            capi.Render.Render2DTexturePremultipliedAlpha(
                pingTexture.TextureId,
                renderX,
                renderY,
                size,
                size,
                500f,
                new Vec4f(1, 1, 1, alpha)
            );
        }

        public void OnMouseMove(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            if (Position == null || IsExpired()) return;

            mapElem.TranslateWorldPosToViewPos(Position, ref viewPos);

            float x = (float)(mapElem.Bounds.renderX + viewPos.X);
            float y = (float)(mapElem.Bounds.renderY + viewPos.Y);

            double mouseX = args.X;
            double mouseY = args.Y;

            double hitboxSize = GuiElement.scaled(20); // Larger hitbox for pings

            if (Math.Abs(mouseX - x) < hitboxSize && Math.Abs(mouseY - y) < hitboxSize)
            {
                hoverText.AppendLine($"{SenderName} pinged here");
            }
        }

        public void Dispose()
        {
            pingTexture?.Dispose();
            pingTexture = null;
        }
    }
}

using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace VSBuddyBeacon
{
    public class BuddyPosition
    {
        public string Name { get; set; }
        public Vec3d Position { get; set; }
    }

    public class HudElementBuddyCompass : HudElement
    {
        private List<BuddyPosition> buddyPositions = new();
        private long tickListenerId;
        private bool isComposed = false;

        public override double InputOrder => 1.0;

        public HudElementBuddyCompass(ICoreClientAPI capi) : base(capi)
        {
            // Register tick listener for updates
            tickListenerId = capi.Event.RegisterGameTickListener(OnTick, 500);
        }

        public override void OnOwnPlayerDataReceived()
        {
            base.OnOwnPlayerDataReceived();

            if (!isComposed)
            {
                ComposeGuis();
                isComposed = true;
            }
        }

        public void UpdateBuddyPositions(List<BuddyPosition> positions)
        {
            buddyPositions = positions ?? new List<BuddyPosition>();
        }

        private void OnTick(float dt)
        {
            // Trigger redraw if we have buddies
            if (isComposed && buddyPositions.Count > 0)
            {
                SingleComposer?.GetCustomDraw("compassDraw")?.Redraw();
            }
        }

        private void ComposeGuis()
        {
            // Small HUD area in top-right for compass indicators
            ElementBounds dialogBounds = new ElementBounds()
            {
                Alignment = EnumDialogArea.RightTop,
                BothSizing = ElementSizing.Fixed,
                fixedWidth = 200,
                fixedHeight = 150,
                fixedOffsetX = -10,
                fixedOffsetY = 100
            };

            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, 200, 150);

            SingleComposer = capi.Gui
                .CreateCompo("vsbuddybeacon-compass", dialogBounds)
                .AddDynamicCustomDraw(bgBounds, OnDraw, "compassDraw")
                .Compose();
        }

        private void OnDraw(Context ctx, ImageSurface surface, ElementBounds currentBounds)
        {
            if (buddyPositions.Count == 0)
                return;

            var player = capi.World?.Player?.Entity;
            if (player == null) return;

            Vec3d playerPos = player.Pos.XYZ;
            float playerYaw = player.Pos.Yaw;

            double y = 5;
            foreach (var buddy in buddyPositions)
            {
                // Calculate direction and distance
                double dx = buddy.Position.X - playerPos.X;
                double dz = buddy.Position.Z - playerPos.Z;
                double distance = Math.Sqrt(dx * dx + dz * dz);

                // Calculate angle to buddy (world space)
                double angleToTarget = Math.Atan2(dx, dz);

                // Relative angle (how much to turn to face them)
                double relativeAngle = angleToTarget - playerYaw;

                // Normalize to -PI to PI
                while (relativeAngle > Math.PI) relativeAngle -= 2 * Math.PI;
                while (relativeAngle < -Math.PI) relativeAngle += 2 * Math.PI;

                // Direction indicator
                string direction = GetDirectionArrow(relativeAngle);
                string distanceStr = distance < 1000 ? $"{(int)distance}m" : $"{distance / 1000:F1}km";

                // Draw background
                ctx.SetSourceRGBA(0, 0, 0, 0.5);
                ctx.Rectangle(0, y - 2, 195, 22);
                ctx.Fill();

                // Draw text
                ctx.SetSourceRGBA(0.4, 0.9, 0.4, 1.0); // Green for buddies
                ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Normal);
                ctx.SetFontSize(12);

                string text = $"{direction} {buddy.Name} - {distanceStr}";
                ctx.MoveTo(5, y + 14);
                ctx.ShowText(text);

                y += 26;

                // Limit to 5 buddies shown
                if (y > 130) break;
            }
        }

        private string GetDirectionArrow(double relativeAngle)
        {
            double degrees = relativeAngle * 180 / Math.PI;

            if (degrees >= -22.5 && degrees < 22.5) return "^";
            if (degrees >= 22.5 && degrees < 67.5) return "/";
            if (degrees >= 67.5 && degrees < 112.5) return ">";
            if (degrees >= 112.5 && degrees < 157.5) return "\\";
            if (degrees >= 157.5 || degrees < -157.5) return "v";
            if (degrees >= -157.5 && degrees < -112.5) return "/";
            if (degrees >= -112.5 && degrees < -67.5) return "<";
            if (degrees >= -67.5 && degrees < -22.5) return "\\";

            return "?";
        }

        public override void Dispose()
        {
            base.Dispose();
            if (tickListenerId != 0)
            {
                capi.Event.UnregisterGameTickListener(tickListenerId);
            }
        }
    }
}

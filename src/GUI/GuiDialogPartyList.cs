using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace VSBuddyBeacon
{
    public class GuiDialogPartyList : GuiDialog
    {
        private List<BuddyPositionWithTimestamp> buddyPositions = new();
        private HashSet<string> pinnedPlayers = new();
        private long tickListenerId;

        public override string ToggleKeyCombinationCode => "partylist";
        public override double DrawOrder => 0.5;

        public GuiDialogPartyList(ICoreClientAPI capi) : base(capi)
        {
            tickListenerId = capi.Event.RegisterGameTickListener(OnTick, 250);
        }

        public void UpdateBuddyPositions(List<BuddyPositionWithTimestamp> positions)
        {
            buddyPositions = positions ?? new List<BuddyPositionWithTimestamp>();

            if (IsOpened())
            {
                ComposeDialog();
            }
        }

        private void OnTick(float dt)
        {
            if (!IsOpened()) return;

            long currentTime = capi.World.ElapsedMilliseconds;
            buddyPositions.RemoveAll(b => b.GetStalenessLevel(currentTime) == StalenessLevel.Expired);

            ComposeDialog();
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            ComposeDialog();
        }

        private void ComposeDialog()
        {
            int pinnedCount = pinnedPlayers.Count(p => buddyPositions.Any(b => b.Name == p));
            int availableCount = buddyPositions.Count(b => !pinnedPlayers.Contains(b.Name));

            // Calculate heights
            int pinnedRowHeight = 70; // RPG-style frame with bars
            int availableRowHeight = 26;
            int baseHeight = 50;
            int pinnedHeight = pinnedCount > 0 ? 25 + (pinnedCount * pinnedRowHeight) : 0;
            int availableHeight = 25 + Math.Max(availableCount * availableRowHeight, 20);
            int totalHeight = Math.Min(500, baseHeight + pinnedHeight + availableHeight);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.LeftMiddle);
            dialogBounds.fixedOffsetX = 10;

            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, 300, totalHeight);

            var composer = capi.Gui
                .CreateCompo("vsbuddybeacon-partylist", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Party List (P)", () => TryClose())
                .BeginChildElements(bgBounds);

            double y = 35;
            var player = capi.World?.Player?.Entity;
            Vec3d playerPos = player?.Pos.XYZ ?? new Vec3d();
            float playerYaw = player?.Pos.Yaw ?? 0f;
            long currentTime = capi.World.ElapsedMilliseconds;

            // Pinned section - RPG style frames
            var pinnedBuddies = buddyPositions.Where(b => pinnedPlayers.Contains(b.Name)).ToList();
            if (pinnedBuddies.Count > 0)
            {
                composer.AddStaticText("Pinned", CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold),
                    ElementBounds.Fixed(10, y, 280, 20));
                y += 22;

                foreach (var buddy in pinnedBuddies)
                {
                    // Draw RPG frame using custom draw
                    string buddyName = buddy.Name;
                    var buddyData = buddy; // Capture for lambda

                    composer.AddDynamicCustomDraw(
                        ElementBounds.Fixed(10, y, 280, pinnedRowHeight - 5),
                        (ctx, surface, bounds) => DrawPinnedBuddyFrame(ctx, surface, bounds, buddyData, playerPos, playerYaw, currentTime),
                        $"pinned_{buddyName}"
                    );

                    // Unpin button
                    composer.AddSmallButton("X", () => { UnpinPlayer(buddyName); return true; },
                        ElementBounds.Fixed(260, y + 5, 25, 20), EnumButtonStyle.Small);

                    y += pinnedRowHeight;
                }

                y += 5;
            }

            // Available buddies section
            var availableBuddies = buddyPositions.Where(b => !pinnedPlayers.Contains(b.Name)).ToList();

            composer.AddStaticText("Available", CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold),
                ElementBounds.Fixed(10, y, 280, 20));
            y += 22;

            if (availableBuddies.Count == 0 && pinnedBuddies.Count == 0)
            {
                composer.AddStaticText("No buddies online",
                    CairoFont.WhiteSmallText().WithColor(new double[] { 0.6, 0.6, 0.6, 1 }),
                    ElementBounds.Fixed(10, y, 280, 20));
            }
            else if (availableBuddies.Count == 0)
            {
                composer.AddStaticText("All buddies pinned",
                    CairoFont.WhiteSmallText().WithColor(new double[] { 0.6, 0.6, 0.6, 1 }),
                    ElementBounds.Fixed(10, y, 280, 20));
            }
            else
            {
                foreach (var buddy in availableBuddies)
                {
                    string distanceStr = GetDistanceString(playerPos, buddy.Position);
                    string heightDiff = GetHeightDiffString(playerPos.Y, buddy.Position.Y);
                    var staleness = buddy.GetStalenessLevel(currentTime);
                    double[] color = GetStalenessColorArray(staleness);

                    composer.AddStaticText($"{buddy.Name} - {distanceStr} {heightDiff}",
                        CairoFont.WhiteSmallText().WithColor(color),
                        ElementBounds.Fixed(10, y, 200, 20));

                    string buddyName = buddy.Name;
                    composer.AddSmallButton("Pin", () => { PinPlayer(buddyName); return true; },
                        ElementBounds.Fixed(220, y - 2, 50, 22), EnumButtonStyle.Small);

                    y += availableRowHeight;
                }
            }

            SingleComposer = composer.EndChildElements().Compose();
        }

        private void DrawPinnedBuddyFrame(Context ctx, ImageSurface surface, ElementBounds bounds,
            BuddyPositionWithTimestamp buddy, Vec3d playerPos, float playerYaw, long currentTime)
        {
            double w = bounds.OuterWidth;
            double h = bounds.OuterHeight;

            // Background
            ctx.SetSourceRGBA(0, 0, 0, 0.4);
            ctx.Rectangle(0, 0, w, h);
            ctx.Fill();

            // Border
            ctx.SetSourceRGBA(0.4, 0.4, 0.4, 0.8);
            ctx.Rectangle(0, 0, w, h);
            ctx.Stroke();

            // Calculate direction
            double dx = buddy.Position.X - playerPos.X;
            double dz = buddy.Position.Z - playerPos.Z;
            double distance = Math.Sqrt(dx * dx + dz * dz);
            double angleToTarget = Math.Atan2(dx, dz);
            double relativeAngle = angleToTarget - playerYaw;
            while (relativeAngle > Math.PI) relativeAngle -= 2 * Math.PI;
            while (relativeAngle < -Math.PI) relativeAngle += 2 * Math.PI;

            string direction = GetDirectionArrow(relativeAngle);
            string distanceStr = distance < 1000 ? $"{(int)distance}m" : $"{distance / 1000:F1}km";
            string heightDiff = GetHeightDiffString(playerPos.Y, buddy.Position.Y);

            var staleness = buddy.GetStalenessLevel(currentTime);
            var color = GetStalenessColor(staleness);

            // Name and stats line
            ctx.SetSourceRGBA(color.r, color.g, color.b, 1);
            ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
            ctx.SetFontSize(13);
            ctx.MoveTo(5, 15);
            ctx.ShowText($"{direction} {buddy.Name}");

            // Distance and height
            ctx.SetSourceRGBA(0.8, 0.8, 0.8, 1);
            ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Normal);
            ctx.SetFontSize(11);
            ctx.MoveTo(5, 28);
            ctx.ShowText($"{distanceStr} {heightDiff}");

            // HP Bar
            double barX = 5;
            double barY = 35;
            double barW = w - 40;
            double barH = 12;

            // HP background
            ctx.SetSourceRGBA(0.3, 0.1, 0.1, 0.8);
            ctx.Rectangle(barX, barY, barW, barH);
            ctx.Fill();

            // HP fill
            float hpPercent = buddy.MaxHealth > 0 ? buddy.Health / buddy.MaxHealth : 0;
            ctx.SetSourceRGBA(0.8, 0.2, 0.2, 1);
            ctx.Rectangle(barX, barY, barW * hpPercent, barH);
            ctx.Fill();

            // HP text
            ctx.SetSourceRGBA(1, 1, 1, 1);
            ctx.SetFontSize(9);
            ctx.MoveTo(barX + 3, barY + 9);
            ctx.ShowText($"HP: {buddy.Health:F0}/{buddy.MaxHealth:F0}");

            // Hunger Bar
            barY = 50;

            // Hunger background
            ctx.SetSourceRGBA(0.2, 0.15, 0.1, 0.8);
            ctx.Rectangle(barX, barY, barW, barH);
            ctx.Fill();

            // Hunger fill
            float hungerPercent = buddy.MaxSaturation > 0 ? buddy.Saturation / buddy.MaxSaturation : 0;
            ctx.SetSourceRGBA(0.7, 0.5, 0.2, 1);
            ctx.Rectangle(barX, barY, barW * hungerPercent, barH);
            ctx.Fill();

            // Hunger text
            ctx.SetSourceRGBA(1, 1, 1, 1);
            ctx.MoveTo(barX + 3, barY + 9);
            ctx.ShowText($"Food: {(hungerPercent * 100):F0}%");
        }

        private void PinPlayer(string playerName)
        {
            pinnedPlayers.Add(playerName);
            ComposeDialog();
        }

        private void UnpinPlayer(string playerName)
        {
            pinnedPlayers.Remove(playerName);
            ComposeDialog();
        }

        private string GetDistanceString(Vec3d from, Vec3d to)
        {
            double dx = to.X - from.X;
            double dz = to.Z - from.Z;
            double distance = Math.Sqrt(dx * dx + dz * dz);
            return distance < 1000 ? $"{(int)distance}m" : $"{distance / 1000:F1}km";
        }

        private string GetHeightDiffString(double fromY, double toY)
        {
            double diff = toY - fromY;
            if (Math.Abs(diff) < 3) return "";
            return diff > 0 ? $"(↑{(int)diff})" : $"(↓{(int)Math.Abs(diff)})";
        }

        private string GetDirectionArrow(double relativeAngle)
        {
            double degrees = relativeAngle * 180 / Math.PI;

            if (degrees >= -22.5 && degrees < 22.5) return "↑";
            if (degrees >= 22.5 && degrees < 67.5) return "↗";
            if (degrees >= 67.5 && degrees < 112.5) return "→";
            if (degrees >= 112.5 && degrees < 157.5) return "↘";
            if (degrees >= 157.5 || degrees < -157.5) return "↓";
            if (degrees >= -157.5 && degrees < -112.5) return "↙";
            if (degrees >= -112.5 && degrees < -67.5) return "←";
            if (degrees >= -67.5 && degrees < -22.5) return "↖";

            return "?";
        }

        private double[] GetStalenessColorArray(StalenessLevel level)
        {
            switch (level)
            {
                case StalenessLevel.Fresh: return new double[] { 0.2, 0.9, 0.2, 1 };
                case StalenessLevel.Aging: return new double[] { 0.9, 0.9, 0.2, 1 };
                case StalenessLevel.Stale: return new double[] { 1.0, 0.6, 0.0, 1 };
                case StalenessLevel.VeryStale: return new double[] { 0.9, 0.1, 0.1, 1 };
                default: return new double[] { 0.5, 0.5, 0.5, 1 };
            }
        }

        private (double r, double g, double b) GetStalenessColor(StalenessLevel level)
        {
            switch (level)
            {
                case StalenessLevel.Fresh: return (0.2, 0.9, 0.2);
                case StalenessLevel.Aging: return (0.9, 0.9, 0.2);
                case StalenessLevel.Stale: return (1.0, 0.6, 0.0);
                case StalenessLevel.VeryStale: return (0.9, 0.1, 0.1);
                default: return (0.5, 0.5, 0.5);
            }
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

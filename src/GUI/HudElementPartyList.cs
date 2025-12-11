using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VSBuddyBeacon.Config;
using VSBuddyBeacon.GUI;

namespace VSBuddyBeacon
{
    public class HudElementPartyList : HudElement
    {
        private static readonly (double r, double g, double b)[] PinColors = new[]
        {
            (0.2, 0.6, 1.0),   // Blue
            (1.0, 0.4, 0.4),   // Red
            (0.4, 1.0, 0.4),   // Green
            (1.0, 0.8, 0.2),   // Yellow
            (1.0, 0.4, 1.0),   // Magenta
            (0.4, 1.0, 1.0),   // Cyan
            (1.0, 0.6, 0.2),   // Orange
            (0.8, 0.4, 1.0),   // Purple
        };

        private List<BuddyPositionWithTimestamp> buddyPositions = new();
        private Dictionary<string, int> pinnedPlayers = new();
        private List<string> pinnedOrder = new();  // Track order for move up/down
        private long tickListenerId;
        private bool isReady = false;
        private bool isAddingBuddy = false;
        private string selectedBuddyName = null;
        private int selectedColorIndex = 0;
        private float uiScale = 1.0f;
        private bool lastMouseUnlocked = false;
        private Action<float, Dictionary<string, int>> onSettingsChanged;
        private GuiDialogSlotMenu openMenu = null;
        private string colorSelectingPlayer = null;  // Player currently selecting color

        public Dictionary<string, (double r, double g, double b)> GetPinnedPlayersWithColors()
        {
            var result = new Dictionary<string, (double r, double g, double b)>();
            foreach (var kvp in pinnedPlayers)
            {
                result[kvp.Key] = PinColors[kvp.Value % PinColors.Length];
            }
            return result;
        }

        public HudElementPartyList(ICoreClientAPI capi) : base(capi)
        {
        }

        public void LoadSettings(float scale, Dictionary<string, int> pins, Action<float, Dictionary<string, int>> saveCallback)
        {
            uiScale = Math.Clamp(scale, 0.9f, 1.5f);
            pinnedPlayers = pins ?? new Dictionary<string, int>();
            pinnedOrder = new List<string>(pinnedPlayers.Keys);  // Initialize order from pins
            onSettingsChanged = saveCallback;
        }

        private void SaveSettings()
        {
            onSettingsChanged?.Invoke(uiScale, new Dictionary<string, int>(pinnedPlayers));
        }

        public override void OnOwnPlayerDataReceived()
        {
            base.OnOwnPlayerDataReceived();
            tickListenerId = capi.Event.RegisterGameTickListener(OnTick, 250);
            isReady = true;
        }

        public void UpdateBuddyPositions(List<BuddyPositionWithTimestamp> positions)
        {
            var oldNames = buddyPositions.Select(b => b.Name).ToHashSet();
            buddyPositions = positions ?? new List<BuddyPositionWithTimestamp>();
            var newNames = buddyPositions.Select(b => b.Name).ToHashSet();

            // Only recompose if a pinned buddy appeared or disappeared
            if (isReady && IsOpened())
            {
                bool pinnedChanged = pinnedPlayers.Keys.Any(p => oldNames.Contains(p) != newNames.Contains(p));
                if (pinnedChanged)
                {
                    ComposeDialog();
                }
                // No Redraw() - drawing function looks up current data by name
            }
        }

        public void ToggleVisibility()
        {
            if (!isReady) return;

            if (IsOpened())
            {
                TryClose();
            }
            else
            {
                ComposeDialog();
                TryOpen();
            }
        }

        public bool IsVisible => IsOpened();

        private bool IsMouseOverWindow()
        {
            if (SingleComposer == null) return false;

            // Get mouse position
            int mouseX = capi.Input.MouseX;
            int mouseY = capi.Input.MouseY;

            // Get dialog bounds
            var bounds = SingleComposer.Bounds;
            if (bounds == null) return false;

            double x = bounds.absX;
            double y = bounds.absY;
            double w = bounds.OuterWidth;
            double h = bounds.OuterHeight;

            // Check if mouse is within bounds
            return mouseX >= x && mouseX <= x + w && mouseY >= y && mouseY <= y + h;
        }

        private void OnTick(float dt)
        {
            // Redraw pinned buddy frames to update distance/compass as player moves
            if (!isReady || !IsOpened()) return;

            // Check if mouse is hovering over window - show/hide buttons accordingly
            bool mouseOver = IsMouseOverWindow();
            if (mouseOver != lastMouseUnlocked)
            {
                lastMouseUnlocked = mouseOver;
                if (isAddingBuddy && !mouseOver)
                {
                    // Close add mode when mouse leaves window
                    isAddingBuddy = false;
                }
                ComposeDialog();
            }

            foreach (var name in pinnedPlayers.Keys)
            {
                SingleComposer?.GetCustomDraw($"pinned_{name}")?.Redraw();
            }
        }

        private void ComposeDialog()
        {
            if (capi?.World?.Player == null) return;

            try
            {
                SingleComposer?.Dispose();

                int pinnedCount = pinnedPlayers.Keys.Count(p => buddyPositions.Any(b => b.Name == p));
                int pinnedRowHeight = (int)(50 * uiScale);  // Tighter slot height
                int baseHeight = (int)(60 * uiScale);
                int windowWidth = (int)(280 * uiScale);
                bool showButtons = lastMouseUnlocked;

                int addSectionHeight = (isAddingBuddy && showButtons) ? (int)(85 * uiScale) : 0;
                int contentHeight = baseHeight + (pinnedCount * pinnedRowHeight) + addSectionHeight;

                ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                    .WithAlignment(EnumDialogArea.LeftTop)
                    .WithFixedPosition(10, 100);

                ElementBounds bgBounds = ElementBounds.Fixed(0, 0, windowWidth, contentHeight);

                var composer = capi.Gui
                    .CreateCompo("vsbuddybeacon-partyhud", dialogBounds)
                    .AddShadedDialogBG(bgBounds)
                    .AddDialogTitleBar("Party", () => TryClose())
                    .BeginChildElements(bgBounds);

                double s = uiScale;

                // Scale buttons - only show when mouse unlocked (UI mode)
                if (showButtons)
                {
                    composer.AddSmallButton("a", () => { DecreaseScale(); return true; },
                        ElementBounds.Fixed(windowWidth - 140, 5, 20, 20), EnumButtonStyle.Small);
                    composer.AddSmallButton("A", () => { IncreaseScale(); return true; },
                        ElementBounds.Fixed(windowWidth - 117, 5, 20, 20), EnumButtonStyle.Small);

                    // Add buddy button
                    string addBtnText = isAddingBuddy ? "-" : "+";
                    composer.AddSmallButton(addBtnText, () => { ToggleAddMode(); return true; },
                        ElementBounds.Fixed(windowWidth - 94, 5, 20, 20), EnumButtonStyle.Small);
                }

                double y = 35 * s;

                // Pinned buddies with custom draw
                // Get pinned buddies in the saved order
                var pinnedBuddies = pinnedOrder
                    .Where(name => buddyPositions.Any(b => b.Name == name))
                    .Select(name => buddyPositions.First(b => b.Name == name))
                    .ToList();
                foreach (var buddy in pinnedBuddies)
                {
                    if (buddy == null || string.IsNullOrEmpty(buddy.Name)) continue;

                    string buddyName = buddy.Name;
                    bool isSelectingColor = colorSelectingPlayer == buddyName;

                    composer.AddDynamicCustomDraw(
                        ElementBounds.Fixed(5 * s, y, windowWidth - 10 * s, pinnedRowHeight - 5 * s),
                        (ctx, surface, bounds) => DrawPinnedBuddyFrame(ctx, surface, bounds, buddyName),
                        $"pinned_{buddyName}"
                    );

                    // Menu button (≡) - only show when mouse unlocked (UI mode), positioned on left
                    if (showButtons && !isSelectingColor)
                    {
                        double menuButtonSize = 24;
                        double menuButtonY = ((pinnedRowHeight - 5 * s) - menuButtonSize) / 2;
                        int colorIdx = pinnedPlayers.TryGetValue(buddyName, out int idx) ? idx : 0;
                        composer.AddSmallButton("≡", () => { OpenSlotMenu(buddyName, colorIdx); return true; },
                            ElementBounds.Fixed(8, y + menuButtonY, menuButtonSize, menuButtonSize), EnumButtonStyle.Small);
                    }

                    y += pinnedRowHeight;

                    // Color selection row - show when this player is selecting color
                    if (isSelectingColor && showButtons)
                    {
                        double colorRowHeight = 30 * s;
                        double swatchSize = 22 * s;
                        double swatchSpacing = 4 * s;
                        double startX = 10 * s;
                        int currentColor = pinnedPlayers.TryGetValue(buddyName, out int ci) ? ci : 0;

                        // Draw color swatches
                        for (int i = 0; i < PinColors.Length; i++)
                        {
                            int colorIndex = i;
                            var color = PinColors[i];
                            bool isCurrentColor = i == currentColor;

                            composer.AddDynamicCustomDraw(
                                ElementBounds.Fixed(startX + i * (swatchSize + swatchSpacing), y, swatchSize, swatchSize),
                                (ctx, surface, bounds) => DrawColorSwatchSimple(ctx, bounds, color, isCurrentColor),
                                $"colorswatch_{buddyName}_{i}"
                            );

                            // Invisible button overlay for click
                            composer.AddSmallButton("", () => { SelectColorForPlayer(buddyName, colorIndex); return true; },
                                ElementBounds.Fixed(startX + colorIndex * (swatchSize + swatchSpacing), y, swatchSize, swatchSize),
                                EnumButtonStyle.None);
                        }

                        // Cancel button
                        composer.AddSmallButton("✕", () => { CancelColorSelection(); return true; },
                            ElementBounds.Fixed(windowWidth - 30 * s, y + 2, 22, 22), EnumButtonStyle.Small);

                        y += colorRowHeight;
                    }
                }

                if (pinnedBuddies.Count == 0 && !isAddingBuddy && showButtons)
                {
                    composer.AddStaticText("No buddies pinned. Click + to add.",
                        CairoFont.WhiteDetailText().WithColor(new double[] { 0.5, 0.5, 0.5, 1 }).WithFontSize((float)(14 * s)),
                        ElementBounds.Fixed(10 * s, y, windowWidth - 20 * s, 25 * s));
                }

                // Inline add section - only when mouse unlocked
                if (isAddingBuddy && showButtons)
                {
                    var availableBuddies = buddyPositions.Where(b => !pinnedPlayers.ContainsKey(b.Name)).ToList();
                    var availableNames = availableBuddies.Select(b => b.Name).ToArray();

                    y += 5 * s;

                    if (availableNames.Length > 0)
                    {
                        int selectedIndex = Array.IndexOf(availableNames, selectedBuddyName);
                        if (selectedIndex < 0)
                        {
                            selectedIndex = 0;
                            selectedBuddyName = availableNames[0];
                        }

                        composer.AddDropDown(
                            availableNames,
                            availableNames,
                            selectedIndex,
                            OnBuddyDropdownChanged,
                            ElementBounds.Fixed(10 * s, y, windowWidth - 20 * s, 25 * s),
                            CairoFont.WhiteSmallText().WithFontSize((float)(14 * s)),
                            "buddyDropdown"
                        );
                    }
                    else
                    {
                        composer.AddStaticText("No buddies available",
                            CairoFont.WhiteSmallText().WithColor(new double[] { 0.6, 0.6, 0.6, 1 }).WithFontSize((float)(12 * s)),
                            ElementBounds.Fixed(10 * s, y + 3 * s, windowWidth - 20 * s, 20 * s));
                    }
                    y += 30 * s;

                    // Color selection
                    var usedColors = new HashSet<int>(pinnedPlayers.Values);
                    double colorX = 10 * s;
                    double colorSize = 28 * s;
                    double colorSpacing = 32 * s;

                    for (int i = 0; i < PinColors.Length; i++)
                    {
                        int colorIdx = i;
                        bool isUsed = usedColors.Contains(i);

                        composer.AddDynamicCustomDraw(
                            ElementBounds.Fixed(colorX + (i * colorSpacing), y, colorSize, colorSize),
                            (ctx, surface, bounds) => DrawColorSwatch(ctx, bounds, colorIdx, usedColors),
                            $"addcolor_{i}"
                        );

                        if (!isUsed)
                        {
                            composer.AddSmallButton("",
                                () => { SelectColor(colorIdx); return true; },
                                ElementBounds.Fixed(colorX + (i * colorSpacing), y, colorSize, colorSize),
                                EnumButtonStyle.None);
                        }
                    }
                    y += 35 * s;

                    if (availableNames.Length > 0)
                    {
                        composer.AddSmallButton("Pin",
                            () => { ConfirmPin(); return true; },
                            ElementBounds.Fixed(windowWidth / 2 - 40 * s, y, 80 * s, 22 * s), EnumButtonStyle.Normal);
                    }
                }

                SingleComposer = composer.EndChildElements().Compose();
            }
            catch (Exception ex)
            {
                capi?.Logger?.Error($"[VSBuddyBeacon] ComposeDialog error: {ex}");
            }
        }

        private void DrawColorSwatch(Context ctx, ElementBounds bounds, int colorIdx, HashSet<int> usedColors)
        {
            double w = bounds.OuterWidth;
            double h = bounds.OuterHeight;
            var color = PinColors[colorIdx];
            bool isUsed = usedColors.Contains(colorIdx);
            bool isSelected = selectedColorIndex == colorIdx && !isUsed;

            ctx.SetSourceRGBA(color.r, color.g, color.b, isUsed ? 0.3 : 1.0);
            ctx.Rectangle(2, 2, w - 4, h - 4);
            ctx.Fill();

            if (isSelected)
            {
                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.LineWidth = 2;
            }
            else
            {
                ctx.SetSourceRGBA(0.3, 0.3, 0.3, 1);
                ctx.LineWidth = 1;
            }
            ctx.Rectangle(2, 2, w - 4, h - 4);
            ctx.Stroke();

            if (isUsed)
            {
                ctx.SetSourceRGBA(0.4, 0.4, 0.4, 0.8);
                ctx.LineWidth = 2;
                ctx.MoveTo(5, 5);
                ctx.LineTo(w - 5, h - 5);
                ctx.MoveTo(w - 5, 5);
                ctx.LineTo(5, h - 5);
                ctx.Stroke();
            }
        }

        private void DrawColorSwatchSimple(Context ctx, ElementBounds bounds, (double r, double g, double b) color, bool isSelected)
        {
            double w = bounds.OuterWidth;
            double h = bounds.OuterHeight;

            // Color fill
            ctx.SetSourceRGBA(color.r, color.g, color.b, 1.0);
            ctx.Rectangle(2, 2, w - 4, h - 4);
            ctx.Fill();

            // Border - highlight if selected
            if (isSelected)
            {
                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.LineWidth = 2;
            }
            else
            {
                ctx.SetSourceRGBA(0.3, 0.3, 0.3, 1);
                ctx.LineWidth = 1;
            }
            ctx.Rectangle(2, 2, w - 4, h - 4);
            ctx.Stroke();
        }

        private void DrawPinnedBuddyFrame(Context ctx, ImageSurface surface, ElementBounds bounds, string buddyName)
        {
            // Look up current buddy data by name
            var buddy = buddyPositions.FirstOrDefault(b => b.Name == buddyName);
            if (buddy == null) return;
            if (capi?.World?.Player?.Entity?.Pos == null) return;

            try
            {
                double w = bounds.OuterWidth;
                double h = bounds.OuterHeight;
                double s = uiScale;

                var playerPos = capi.World.Player.Entity.Pos.XYZ;
                double playerYaw = capi.World.Player.Entity.Pos.Yaw;

                // Background
                ctx.SetSourceRGBA(0.08, 0.08, 0.08, 0.9);
                ctx.Rectangle(0, 0, w, h);
                ctx.Fill();

                // Calculate direction
                double dx = buddy.Position.X - playerPos.X;
                double dz = buddy.Position.Z - playerPos.Z;
                double distance = Math.Sqrt(dx * dx + dz * dz);
                double angleToTarget = Math.Atan2(dx, dz);
                double relativeAngle = angleToTarget - playerYaw;
                while (relativeAngle > Math.PI) relativeAngle -= 2 * Math.PI;
                while (relativeAngle < -Math.PI) relativeAngle += 2 * Math.PI;

                string distanceStr = distance < 1000 ? $"{(int)distance}m" : $"{distance / 1000:F1}km";
                string heightArrow = GetHeightArrowString(playerPos.Y, buddy.Position.Y);

                (double r, double g, double b) color = pinnedPlayers.TryGetValue(buddy.Name, out int colorIndex)
                    ? PinColors[colorIndex % PinColors.Length]
                    : (0.5, 0.5, 0.5);

                // Layout: percentage-based
                // Top row = 45% of height (name, distance, compass) - room for compass
                // Bars below with small gap
                double topRowHeight = h * 0.45;
                double barAreaTop = topRowHeight + 2;  // Small gap after top row
                double barHeight = h * 0.22;  // Bars
                double barGap = h * 0.04;

                // Colored accent line on left edge
                ctx.SetSourceRGBA(color.r, color.g, color.b, 1);
                ctx.Rectangle(0, 0, 4, h);
                ctx.Fill();

                double nameStartX = 10;
                double barX = 10;
                double barRightMargin = 10;

                // When focused, shift name/bars right to make room for menu button
                if (lastMouseUnlocked)
                {
                    nameStartX = 32 * s;  // Shift name right for menu button
                    barX = 32 * s;  // Bars also shift right
                }

                // Compass - fixed size, constrained to top row (bottom edge above bars)
                double compassRadius = 14 * s;
                double compassX = w - compassRadius - 8;  // Same position focused or not (no X button)
                double compassY = barAreaTop - compassRadius - 2;  // Bottom of compass stops before bars

                ctx.SetSourceRGBA(0.15, 0.15, 0.15, 0.9);
                ctx.Arc(compassX, compassY, compassRadius, 0, 2 * Math.PI);
                ctx.Fill();

                ctx.SetSourceRGBA(0.4, 0.4, 0.4, 1);
                ctx.Arc(compassX, compassY, compassRadius, 0, 2 * Math.PI);
                ctx.Stroke();

                double arrowAngle = -relativeAngle;
                ctx.Save();
                ctx.Translate(compassX, compassY);
                ctx.Rotate(arrowAngle);
                ctx.SetSourceRGBA(color.r, color.g, color.b, 1);
                double arrowTip = compassRadius - 2;
                double arrowBase = compassRadius * 0.45;
                ctx.MoveTo(0, -arrowTip);
                ctx.LineTo(-arrowBase * 0.5, arrowBase);
                ctx.LineTo(0, arrowBase * 0.5);
                ctx.LineTo(arrowBase * 0.5, arrowBase);
                ctx.ClosePath();
                ctx.Fill();
                ctx.Restore();

                // Name - shifts right when focused to make room for hamburger
                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
                double nameFontSize = 20 * s;
                ctx.SetFontSize(nameFontSize);
                ctx.MoveTo(nameStartX, topRowHeight * 0.7);
                ctx.ShowText(buddy.Name);

                // Distance + height arrow - positioned left of compass with more space
                ctx.SetSourceRGBA(0.7, 0.9, 0.9, 1);
                ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
                double infoFontSize = 18 * s;
                ctx.SetFontSize(infoFontSize);
                double infoX = compassX - compassRadius - 95 * s;  // More space from compass
                ctx.MoveTo(infoX, topRowHeight * 0.7);
                ctx.ShowText($"{distanceStr}  {heightArrow}");

                // Bars - fill width, collapse for buttons when focused
                double barW = w - barX - barRightMargin;

                // HP Bar (top bar in bar area)
                double hpBarY = barAreaTop;
                ctx.SetSourceRGBA(0.25, 0.08, 0.08, 0.9);
                ctx.Rectangle(barX, hpBarY, barW, barHeight);
                ctx.Fill();

                float hpPercent = buddy.MaxHealth > 0 ? Math.Min(1f, buddy.Health / buddy.MaxHealth) : 0;
                ctx.SetSourceRGBA(0.8, 0.2, 0.2, 1);
                ctx.Rectangle(barX, hpBarY, barW * hpPercent, barHeight);
                ctx.Fill();

                ctx.SetSourceRGBA(1, 1, 1, 1);
                double barFontSize = 11 * s;
                ctx.SetFontSize(barFontSize);
                ctx.MoveTo(barX + 4, hpBarY + barHeight * 0.7);
                ctx.ShowText($"HP: {buddy.Health:F0}/{buddy.MaxHealth:F0}");

                // Food Bar (below HP bar)
                double foodBarY = hpBarY + barHeight + barGap;
                ctx.SetSourceRGBA(0.18, 0.12, 0.05, 0.9);
                ctx.Rectangle(barX, foodBarY, barW, barHeight);
                ctx.Fill();

                float hungerPercent = buddy.MaxSaturation > 0 ? Math.Min(1f, buddy.Saturation / buddy.MaxSaturation) : 0;
                ctx.SetSourceRGBA(0.75, 0.55, 0.2, 1);
                ctx.Rectangle(barX, foodBarY, barW * hungerPercent, barHeight);
                ctx.Fill();

                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.SetFontSize(barFontSize);
                ctx.MoveTo(barX + 4, foodBarY + barHeight * 0.7);
                ctx.ShowText($"Food: {(hungerPercent * 100):F0}%");
            }
            catch (Exception ex)
            {
                capi?.Logger?.Error($"[VSBuddyBeacon] DrawPinnedBuddyFrame error: {ex.Message}");
            }
        }

        private void IncreaseScale()
        {
            uiScale = Math.Min(1.5f, uiScale + 0.1f);
            ComposeDialog();
            SaveSettings();
        }

        private void DecreaseScale()
        {
            uiScale = Math.Max(0.9f, uiScale - 0.1f);  // Only allow 1 step down from default
            ComposeDialog();
            SaveSettings();
        }

        private void ToggleAddMode()
        {
            isAddingBuddy = !isAddingBuddy;
            if (isAddingBuddy)
            {
                selectedBuddyName = null;
                var usedColors = new HashSet<int>(pinnedPlayers.Values);
                selectedColorIndex = 0;
                for (int i = 0; i < PinColors.Length; i++)
                {
                    if (!usedColors.Contains(i))
                    {
                        selectedColorIndex = i;
                        break;
                    }
                }
            }
            ComposeDialog();
        }

        private void OnBuddyDropdownChanged(string code, bool selected)
        {
            selectedBuddyName = code;
        }

        private void SelectColor(int colorIndex)
        {
            selectedColorIndex = colorIndex;
            for (int i = 0; i < PinColors.Length; i++)
            {
                SingleComposer?.GetCustomDraw($"addcolor_{i}")?.Redraw();
            }
        }

        private void ConfirmPin()
        {
            if (string.IsNullOrEmpty(selectedBuddyName)) return;
            if (pinnedPlayers.ContainsKey(selectedBuddyName)) return;

            pinnedPlayers[selectedBuddyName] = selectedColorIndex;
            pinnedOrder.Add(selectedBuddyName);  // Add to order list
            isAddingBuddy = false;
            selectedBuddyName = null;
            ComposeDialog();
            NotifyPinnedChanged();
            SaveSettings();
        }

        private void UnpinPlayer(string playerName)
        {
            pinnedPlayers.Remove(playerName);
            pinnedOrder.Remove(playerName);
            ComposeDialog();
            NotifyPinnedChanged();
            SaveSettings();
        }

        private void OpenSlotMenu(string playerName, int colorIndex)
        {
            capi.Logger.Debug($"[VSBuddyBeacon] OpenSlotMenu called for {playerName}");

            // Close any existing menu
            openMenu?.TryClose();

            // Get screen position for the menu (near the slot)
            double menuX = capi.Input.MouseX;
            double menuY = capi.Input.MouseY;

            capi.Logger.Debug($"[VSBuddyBeacon] Creating menu at {menuX}, {menuY}");

            openMenu = new GuiDialogSlotMenu(capi, playerName, colorIndex, menuX, menuY, HandleSlotAction);
            bool opened = openMenu.TryOpen();

            capi.Logger.Debug($"[VSBuddyBeacon] TryOpen returned: {opened}");
        }

        private void HandleSlotAction(string playerName, string action)
        {
            switch (action)
            {
                case "changecolor":
                    StartColorSelection(playerName);
                    break;
                case "moveup":
                    MovePlayerUp(playerName);
                    break;
                case "movedown":
                    MovePlayerDown(playerName);
                    break;
                case "kick":
                    UnpinPlayer(playerName);
                    break;
            }
            openMenu = null;
        }

        private void StartColorSelection(string playerName)
        {
            colorSelectingPlayer = playerName;
            ComposeDialog();
        }

        private void SelectColorForPlayer(string playerName, int colorIndex)
        {
            if (pinnedPlayers.ContainsKey(playerName))
            {
                pinnedPlayers[playerName] = colorIndex;
                colorSelectingPlayer = null;
                ComposeDialog();
                NotifyPinnedChanged();
                SaveSettings();
            }
        }

        private void CancelColorSelection()
        {
            colorSelectingPlayer = null;
            ComposeDialog();
        }

        private void MovePlayerUp(string playerName)
        {
            int index = pinnedOrder.IndexOf(playerName);
            if (index > 0)
            {
                pinnedOrder.RemoveAt(index);
                pinnedOrder.Insert(index - 1, playerName);
                ComposeDialog();
                SaveSettings();
            }
        }

        private void MovePlayerDown(string playerName)
        {
            int index = pinnedOrder.IndexOf(playerName);
            if (index >= 0 && index < pinnedOrder.Count - 1)
            {
                pinnedOrder.RemoveAt(index);
                pinnedOrder.Insert(index + 1, playerName);
                ComposeDialog();
                SaveSettings();
            }
        }

        private string GetHeightDiffString(double fromY, double toY)
        {
            double diff = toY - fromY;
            if (Math.Abs(diff) < 3) return "";
            return diff > 0 ? $"(+{(int)diff})" : $"({(int)diff})";
        }

        private string GetHeightArrowString(double fromY, double toY)
        {
            double diff = toY - fromY;
            if (Math.Abs(diff) < 1) return "";
            string arrow = diff > 0 ? "↑" : "↓";
            return $"{arrow}{Math.Abs((int)diff)}";
        }

        public Action OnPinnedPlayersChanged { get; set; }

        private void NotifyPinnedChanged()
        {
            OnPinnedPlayersChanged?.Invoke();
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

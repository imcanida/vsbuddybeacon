using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
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
        private Dictionary<string, int> pinnedPlayers = new();  // Color preferences (name -> colorIndex)
        private List<string> pinnedOrder = new();  // Track order for move up/down
        private long tickListenerId;
        private bool isReady = false;
        private bool isAddingBuddy = false;
        private string selectedBuddyName = null;
        private int selectedColorIndex = 0;
        private float uiScale = 1.0f;
        private const float MIN_EFFECTIVE_SCALE = 0.6f;  // Minimum readable scale

        /// <summary>
        /// Gets the effective UI scale, clamped to a minimum to ensure readability
        /// </summary>
        private float EffectiveScale => Math.Max(MIN_EFFECTIVE_SCALE, uiScale * (float)RuntimeEnv.GUIScale);

        private bool lastMouseUnlocked = false;
        private Action<float, Dictionary<string, int>> onSettingsChanged;
        private GuiDialogSlotMenu openMenu = null;
        private string colorSelectingPlayer = null;  // Player currently selecting color

        // Party state from server
        private PartyStatePacket partyState = null;

        // Dragging state for custom title bar
        private bool isDragging = false;
        private double dragOffsetX;
        private double dragOffsetY;
        private double dialogPosX = 10;
        private double dialogPosY = 100;
        private const double TITLE_BAR_HEIGHT = 25;


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

            // Only recompose if a party member appeared or disappeared
            if (isReady && IsOpened())
            {
                var partyMemberNames = partyState?.MemberNames ?? Array.Empty<string>();
                bool partyMemberChanged = partyMemberNames.Any(p => oldNames.Contains(p) != newNames.Contains(p));
                if (partyMemberChanged)
                {
                    ComposeDialog();
                }
                // No Redraw() - drawing function looks up current data by name
            }
        }

        public void UpdatePartyState(PartyStatePacket state)
        {
            partyState = state;
            // Ensure all party members are in pinnedOrder
            if (state != null)
            {
                foreach (var name in state.MemberNames)
                {
                    if (!pinnedOrder.Contains(name))
                    {
                        pinnedOrder.Add(name);
                    }
                }
            }
            if (isReady && IsOpened())
            {
                ComposeDialog();
            }
        }

        public void ClearPartyState()
        {
            partyState = null;
            if (isReady && IsOpened())
            {
                ComposeDialog();
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

            // Redraw party member frames
            var partyMemberNames = partyState?.MemberNames ?? Array.Empty<string>();
            foreach (var name in partyMemberNames)
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

                // Get party members (excluding self) - include offline members too
                var partyMemberNames = partyState?.MemberNames ?? Array.Empty<string>();
                var partyMemberOnline = partyState?.MemberOnline ?? Array.Empty<bool>();
                var partyMemberUids = partyState?.MemberUids ?? Array.Empty<string>();
                var myName = capi.World.Player.PlayerName;

                // Build list of all displayable party members (online or offline)
                var displayablePartyMembers = new List<string>();
                var memberOnlineStatus = new Dictionary<string, bool>();
                var memberUidLookup = new Dictionary<string, string>();

                for (int i = 0; i < partyMemberNames.Length; i++)
                {
                    string name = partyMemberNames[i];
                    if (name == myName) continue;

                    displayablePartyMembers.Add(name);
                    memberOnlineStatus[name] = i < partyMemberOnline.Length && partyMemberOnline[i];
                    if (i < partyMemberUids.Length)
                        memberUidLookup[name] = partyMemberUids[i];
                }

                int partyMemberCount = displayablePartyMembers.Count;

                // Use effective scale with minimum floor for readability
                float effectiveScale = EffectiveScale;
                int pinnedRowHeight = (int)(50 * effectiveScale);  // Tighter slot height
                int windowWidth = (int)(280 * effectiveScale);
                bool showButtons = lastMouseUnlocked;

                // Can invite if: not in party OR is party leader
                bool isLeader = partyState != null && partyState.LeaderUid == capi.World.Player.PlayerUID;
                bool canInvite = partyState == null || isLeader;

                int addSectionHeight = (isAddingBuddy && showButtons && canInvite) ? (int)(70 * effectiveScale) : 0;

                // Extra height for color selection row when active
                bool isColorSelecting = !string.IsNullOrEmpty(colorSelectingPlayer) && displayablePartyMembers.Contains(colorSelectingPlayer);
                int colorSelectHeight = (isColorSelecting && showButtons) ? (int)(22 * effectiveScale) : 0;

                // Calculate content height - need space for empty message when no members
                int baseHeight;
                if (partyMemberCount == 0 && !isAddingBuddy)
                {
                    baseHeight = (int)(30 * effectiveScale);  // Space for "Not in party" message
                }
                else if (partyMemberCount == 0)
                {
                    baseHeight = (int)(10 * effectiveScale);  // Minimal when showing add form
                }
                else
                {
                    baseHeight = (int)(10 * effectiveScale);  // Minimal when showing members
                }
                int contentHeight = baseHeight + (partyMemberCount * pinnedRowHeight) + addSectionHeight + colorSelectHeight;

                ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                    .WithAlignment(EnumDialogArea.None)
                    .WithFixedPosition(dialogPosX, dialogPosY);

                // Add title bar height to content
                double titleHeight = TITLE_BAR_HEIGHT * effectiveScale;
                double totalHeight = contentHeight + titleHeight;
                ElementBounds bgBounds = ElementBounds.Fixed(0, 0, windowWidth, totalHeight);
                ElementBounds titleBounds = ElementBounds.Fixed(0, 0, windowWidth, titleHeight);
                ElementBounds contentBounds = ElementBounds.Fixed(0, titleHeight, windowWidth, contentHeight);

                // Drag handle area on left side of title bar
                double dragHandleWidth = 20 * effectiveScale;

                // Pre-calculate button dimensions (all in GUI units that scale with effectiveScale)
                double btnPadY = 3 * effectiveScale;
                double btnH = titleHeight - btnPadY * 2;
                double btnGap = 3 * effectiveScale;
                double btnPadRight = 6 * effectiveScale;

                // Button widths - scale with effectiveScale
                double leaveW = 38 * effectiveScale;
                double addW = 20 * effectiveScale;
                double scaleW = 20 * effectiveScale;

                // Build button list with positions
                var buttonsToDraw = new List<(double x, double w, string text, Action action)>();
                double btnX = windowWidth - btnPadRight;

                if (showButtons)
                {
                    if (partyState != null)
                    {
                        btnX -= leaveW;
                        buttonsToDraw.Add((btnX, leaveW, "Leave", LeaveParty));
                        btnX -= btnGap;
                    }
                    if (canInvite)
                    {
                        string addBtnText = isAddingBuddy ? "-" : "+";
                        btnX -= addW;
                        buttonsToDraw.Add((btnX, addW, addBtnText, ToggleAddMode));
                        btnX -= btnGap;
                    }
                    btnX -= scaleW;
                    buttonsToDraw.Add((btnX, scaleW, "A", IncreaseScale));
                    btnX -= btnGap;
                    btnX -= scaleW;
                    buttonsToDraw.Add((btnX, scaleW, "a", DecreaseScale));
                }

                // Capture for closure - need to convert to fractions for pixel drawing
                double capturedWidth = windowWidth;
                double capturedTitleH = titleHeight;
                double capturedBtnPadY = btnPadY;
                double capturedBtnH = btnH;
                var btns = buttonsToDraw.Select(b => (
                    xFrac: b.x / capturedWidth,
                    wFrac: b.w / capturedWidth,
                    yFrac: capturedBtnPadY / capturedTitleH,
                    hFrac: capturedBtnH / capturedTitleH,
                    b.text
                )).ToList();

                var composer = capi.Gui
                    .CreateCompo("vsbuddybeacon-partyhud", dialogBounds)
                    // Custom full background - consistent styling
                    .AddStaticCustomDraw(bgBounds, (ctx, surface, bounds) => {
                        double w = bounds.OuterWidth;
                        double h = bounds.OuterHeight;
                        double th = titleHeight;
                        double handleW = dragHandleWidth;

                        // Main background (content area)
                        ctx.SetSourceRGBA(0.1, 0.1, 0.1, 0.92);
                        ctx.Rectangle(0, 0, w, h);
                        ctx.Fill();

                        // Title bar background (slightly lighter)
                        ctx.SetSourceRGBA(0.18, 0.18, 0.18, 0.98);
                        ctx.Rectangle(0, 0, w, th);
                        ctx.Fill();

                        // Outer border
                        ctx.SetSourceRGBA(0.35, 0.35, 0.35, 1);
                        ctx.LineWidth = 1;
                        ctx.Rectangle(0.5, 0.5, w - 1, h - 1);
                        ctx.Stroke();

                        // Drag handle (4-way move icon)
                        double centerX = handleW / 2;
                        double centerY = th / 2;
                        double arrowLen = Math.Min(handleW, th) * 0.28;
                        double arrowHead = arrowLen * 0.4;

                        ctx.SetSourceRGBA(0.6, 0.6, 0.6, 0.9);
                        ctx.LineWidth = 1.5;

                        // Up arrow
                        ctx.MoveTo(centerX, centerY - arrowLen);
                        ctx.LineTo(centerX, centerY - 2);
                        ctx.Stroke();
                        ctx.MoveTo(centerX, centerY - arrowLen);
                        ctx.LineTo(centerX - arrowHead, centerY - arrowLen + arrowHead);
                        ctx.Stroke();
                        ctx.MoveTo(centerX, centerY - arrowLen);
                        ctx.LineTo(centerX + arrowHead, centerY - arrowLen + arrowHead);
                        ctx.Stroke();

                        // Down arrow
                        ctx.MoveTo(centerX, centerY + 2);
                        ctx.LineTo(centerX, centerY + arrowLen);
                        ctx.Stroke();
                        ctx.MoveTo(centerX, centerY + arrowLen);
                        ctx.LineTo(centerX - arrowHead, centerY + arrowLen - arrowHead);
                        ctx.Stroke();
                        ctx.MoveTo(centerX, centerY + arrowLen);
                        ctx.LineTo(centerX + arrowHead, centerY + arrowLen - arrowHead);
                        ctx.Stroke();

                        // Left arrow
                        ctx.MoveTo(centerX - arrowLen, centerY);
                        ctx.LineTo(centerX - 2, centerY);
                        ctx.Stroke();
                        ctx.MoveTo(centerX - arrowLen, centerY);
                        ctx.LineTo(centerX - arrowLen + arrowHead, centerY - arrowHead);
                        ctx.Stroke();
                        ctx.MoveTo(centerX - arrowLen, centerY);
                        ctx.LineTo(centerX - arrowLen + arrowHead, centerY + arrowHead);
                        ctx.Stroke();

                        // Right arrow
                        ctx.MoveTo(centerX + 2, centerY);
                        ctx.LineTo(centerX + arrowLen, centerY);
                        ctx.Stroke();
                        ctx.MoveTo(centerX + arrowLen, centerY);
                        ctx.LineTo(centerX + arrowLen - arrowHead, centerY - arrowHead);
                        ctx.Stroke();
                        ctx.MoveTo(centerX + arrowLen, centerY);
                        ctx.LineTo(centerX + arrowLen - arrowHead, centerY + arrowHead);
                        ctx.Stroke();

                        // Separator after drag handle
                        ctx.SetSourceRGBA(0.3, 0.3, 0.3, 1);
                        ctx.LineWidth = 1;
                        ctx.MoveTo(handleW, 4);
                        ctx.LineTo(handleW, th - 4);
                        ctx.Stroke();

                        // Title text
                        ctx.SetSourceRGBA(0.9, 0.85, 0.7, 1);
                        ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
                        ctx.SetFontSize(14 * effectiveScale);
                        ctx.MoveTo(handleW + 8 * effectiveScale, th * 0.7);
                        ctx.ShowText("Party");

                        // Draw custom buttons in title bar
                        if (btns != null && btns.Count > 0)
                        {
                            // Use pixel-based font size
                            double fontSize = th * 0.45;
                            ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Normal);
                            ctx.SetFontSize(fontSize);

                            foreach (var btn in btns)
                            {
                                // Convert fractions to pixels
                                double bx = btn.xFrac * w;
                                double bw = btn.wFrac * w;
                                double by = btn.yFrac * th;
                                double bh = btn.hFrac * th;

                                // Button background
                                ctx.SetSourceRGBA(0.25, 0.25, 0.25, 0.9);
                                ctx.Rectangle(bx, by, bw, bh);
                                ctx.Fill();

                                // Button border
                                ctx.SetSourceRGBA(0.4, 0.4, 0.4, 1);
                                ctx.LineWidth = 1;
                                ctx.Rectangle(bx + 0.5, by + 0.5, bw - 1, bh - 1);
                                ctx.Stroke();

                                // Button text (centered)
                                ctx.SetSourceRGBA(0.85, 0.85, 0.85, 1);
                                var ext = ctx.TextExtents(btn.text);
                                double textX = bx + (bw - ext.Width) / 2;
                                double textY = by + bh * 0.72;
                                ctx.MoveTo(textX, textY);
                                ctx.ShowText(btn.text);
                            }
                        }

                        // Title bar bottom border
                        ctx.SetSourceRGBA(0.3, 0.3, 0.3, 1);
                        ctx.LineWidth = 1;
                        ctx.MoveTo(0, th - 0.5);
                        ctx.LineTo(w, th - 0.5);
                        ctx.Stroke();
                    });

                double s = effectiveScale;

                // Add invisible click areas for buttons (using same positions from buttonsToDraw)
                foreach (var btn in buttonsToDraw)
                {
                    var action = btn.action;  // Capture for closure
                    composer.AddSmallButton("", () => { action(); return true; },
                        ElementBounds.Fixed(btn.x, btnPadY, btn.w, btnH), EnumButtonStyle.None);
                }

                composer.BeginChildElements(contentBounds);

                double y = 3 * s;  // Flush at top, minimal padding

                // Party members with custom draw - sorted by pinnedOrder, then by server order
                var orderedPartyMembers = pinnedOrder
                    .Where(name => displayablePartyMembers.Contains(name))
                    .ToList();
                // Add any party members not in pinnedOrder
                foreach (var name in displayablePartyMembers)
                {
                    if (!orderedPartyMembers.Contains(name))
                    {
                        orderedPartyMembers.Add(name);
                    }
                }

                // Content margins
                double contentMargin = 5 * s;
                double slotWidth = windowWidth - contentMargin * 2;

                int totalPartyCount = orderedPartyMembers.Count;
                for (int buddyIdx = 0; buddyIdx < orderedPartyMembers.Count; buddyIdx++)
                {
                    string buddyName = orderedPartyMembers[buddyIdx];
                    if (string.IsNullOrEmpty(buddyName)) continue;

                    bool isOnline = memberOnlineStatus.TryGetValue(buddyName, out bool online) && online;
                    string buddyUid = memberUidLookup.TryGetValue(buddyName, out string uid) ? uid : null;
                    bool isSelectingColor = colorSelectingPlayer == buddyName;

                    // Capture for closure
                    string capturedName = buddyName;
                    bool capturedOnline = isOnline;

                    composer.AddDynamicCustomDraw(
                        ElementBounds.Fixed(contentMargin, y, slotWidth, pinnedRowHeight - 5 * s),
                        (ctx, surface, bounds) => DrawPinnedBuddyFrame(ctx, surface, bounds, capturedName, capturedOnline),
                        $"pinned_{buddyName}"
                    );

                    // Menu button (≡) - only show when mouse unlocked (UI mode), positioned inside frame
                    if (showButtons && !isSelectingColor)
                    {
                        double menuButtonSize = 20 * s;
                        double menuButtonY = ((pinnedRowHeight - 5 * s) - menuButtonSize) / 2;
                        int colorIdx = pinnedPlayers.TryGetValue(buddyName, out int idx) ? idx : 0;
                        int capturedIdx = buddyIdx;  // Capture for closure
                        composer.AddSmallButton("≡", () => { OpenSlotMenu(buddyName, buddyUid, colorIdx, isLeader, capturedIdx, totalPartyCount); return true; },
                            ElementBounds.Fixed(contentMargin + 6 * s, y + menuButtonY, menuButtonSize, menuButtonSize), EnumButtonStyle.Small);
                    }

                    y += pinnedRowHeight;

                    // Color selection row - show when this player is selecting color
                    if (isSelectingColor && showButtons)
                    {
                        double colorRowHeight = 22 * s;
                        double swatchSize = 14 * s;
                        double swatchSpacing = 3 * s;
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

                        // Cancel button - use "X" instead of unicode character
                        double cancelSize = 16 * s;
                        composer.AddSmallButton("X", () => { CancelColorSelection(); return true; },
                            ElementBounds.Fixed(windowWidth - cancelSize - 8 * s, y + (swatchSize - cancelSize) / 2, cancelSize, cancelSize), EnumButtonStyle.Small);

                        y += colorRowHeight;
                    }
                }

                if (orderedPartyMembers.Count == 0 && !isAddingBuddy && showButtons)
                {
                    string emptyMessage = partyState == null
                        ? "Not in a party. Click + to invite."
                        : (canInvite ? "No party members online. Click + to invite." : "No party members online.");
                    composer.AddStaticText(emptyMessage,
                        CairoFont.WhiteDetailText().WithColor(new double[] { 0.5, 0.5, 0.5, 1 }).WithFontSize((float)(14 * s)),
                        ElementBounds.Fixed(10 * s, y, windowWidth - 20 * s, 25 * s));
                }

                // Inline add section - only when mouse unlocked and user can invite
                if (isAddingBuddy && showButtons && canInvite)
                {
                    // Show buddies who are in beacon group but NOT already in party
                    var partyMemberSet = new HashSet<string>(partyMemberNames);
                    var availableBuddies = buddyPositions
                        .Where(b => b.Name != myName && !partyMemberSet.Contains(b.Name))
                        .ToList();
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
                        composer.AddStaticText("No buddies available to invite",
                            CairoFont.WhiteSmallText().WithColor(new double[] { 0.6, 0.6, 0.6, 1 }).WithFontSize((float)(12 * s)),
                            ElementBounds.Fixed(10 * s, y + 3 * s, windowWidth - 20 * s, 20 * s));
                    }
                    y += 30 * s;

                    // Color selection - only mark colors as "used" if they belong to current party members
                    var currentPartyColors = new HashSet<int>();
                    foreach (var memberName in partyMemberNames)
                    {
                        if (memberName != myName && pinnedPlayers.TryGetValue(memberName, out int colorIdx))
                        {
                            currentPartyColors.Add(colorIdx);
                        }
                    }
                    var usedColors = currentPartyColors;
                    double colorX = 10 * s;
                    double colorSize = 14 * s;
                    double colorSpacing = 18 * s;

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
                    y += 20 * s;

                    if (availableNames.Length > 0)
                    {
                        composer.AddSmallButton("Invite",
                            () => { ConfirmInvite(); return true; },
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

        private void DrawPinnedBuddyFrame(Context ctx, ImageSurface surface, ElementBounds bounds, string buddyName, bool isOnline = true)
        {
            if (capi?.World?.Player?.Entity?.Pos == null) return;

            // Look up current buddy data by name (may be null if offline)
            var buddy = buddyPositions.FirstOrDefault(b => b.Name == buddyName);

            try
            {
                double w = bounds.OuterWidth;
                double h = bounds.OuterHeight;

                var playerPos = capi.World.Player.Entity.Pos.XYZ;
                double playerYaw = capi.World.Player.Entity.Pos.Yaw;

                // Background - darker for offline
                double bgAlpha = isOnline && buddy != null ? 0.9 : 0.7;
                ctx.SetSourceRGBA(0.08, 0.08, 0.08, bgAlpha);
                ctx.Rectangle(0, 0, w, h);
                ctx.Fill();

                // Calculate direction (only if online with position data)
                string distanceStr = "Offline";
                string heightArrow = "";
                double relativeAngle = 0;
                bool hasPositionData = isOnline && buddy != null;

                if (hasPositionData)
                {
                    double dx = buddy.Position.X - playerPos.X;
                    double dz = buddy.Position.Z - playerPos.Z;
                    double distance = Math.Sqrt(dx * dx + dz * dz);
                    double angleToTarget = Math.Atan2(dx, dz);
                    relativeAngle = angleToTarget - playerYaw;
                    while (relativeAngle > Math.PI) relativeAngle -= 2 * Math.PI;
                    while (relativeAngle < -Math.PI) relativeAngle += 2 * Math.PI;

                    distanceStr = distance < 1000 ? $"{(int)distance}m" : $"{distance / 1000:F1}km";
                    heightArrow = GetHeightArrowString(playerPos.Y, buddy.Position.Y);
                }

                (double r, double g, double b) color = pinnedPlayers.TryGetValue(buddyName, out int colorIndex)
                    ? PinColors[colorIndex % PinColors.Length]
                    : (0.5, 0.5, 0.5);

                // Dim color if offline
                if (!hasPositionData)
                {
                    color = (color.r * 0.5, color.g * 0.5, color.b * 0.5);
                }

                // Scale factor based on bounds height - ensures consistent proportions at any GUI scale
                double sf = h / 45.0;  // 45 is the design height at uiScale 1.0

                // Layout zones (proportional to height)
                double topPadding = 3 * sf;         // Gap at top
                double topRowHeight = 18 * sf;      // Name row height
                double barAreaTop = 24 * sf;        // Where bars start
                double barHeight = 9 * sf;          // Each bar height
                double barGap = 2 * sf;             // Gap between bars

                // Colored accent line on left edge
                ctx.SetSourceRGBA(color.r, color.g, color.b, 1);
                ctx.Rectangle(0, 0, 4 * sf, h);
                ctx.Fill();

                // Left margin - shifts when menu button visible
                double contentLeft = lastMouseUnlocked ? 32 * sf : 8 * sf;
                double contentRight = 8 * sf;

                // === COMPASS (top-right area) ===
                double compassRadius = 9 * sf;
                double compassX = w - compassRadius - 10 * sf;
                double compassY = topPadding + topRowHeight / 2;

                // Compass background
                ctx.SetSourceRGBA(0.15, 0.15, 0.15, 0.9);
                ctx.Arc(compassX, compassY, compassRadius, 0, 2 * Math.PI);
                ctx.Fill();

                // Compass border
                ctx.SetSourceRGBA(0.4, 0.4, 0.4, 1);
                ctx.Arc(compassX, compassY, compassRadius, 0, 2 * Math.PI);
                ctx.Stroke();

                if (hasPositionData)
                {
                    // Compass arrow - only show when we have position data
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
                }
                else
                {
                    // Draw "?" or "X" in compass for offline
                    ctx.SetSourceRGBA(0.5, 0.5, 0.5, 0.8);
                    ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
                    ctx.SetFontSize(compassRadius * 1.2);
                    var qExt = ctx.TextExtents("?");
                    ctx.MoveTo(compassX - qExt.Width / 2, compassY + qExt.Height / 3);
                    ctx.ShowText("?");
                }

                // === NAME (top-left) ===
                double textY = topPadding + topRowHeight * 0.7;  // Vertically centered in top row
                ctx.SetSourceRGBA(hasPositionData ? 1 : 0.6, hasPositionData ? 1 : 0.6, hasPositionData ? 1 : 0.6, 1);
                ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
                ctx.SetFontSize(13 * sf);
                ctx.MoveTo(contentLeft, textY);
                ctx.ShowText(buddyName);  // Use buddyName parameter, not buddy.Name

                // === DISTANCE + HEIGHT (between name and compass) ===
                string infoText = string.IsNullOrEmpty(heightArrow) ? distanceStr : $"{distanceStr}  {heightArrow}";
                ctx.SetSourceRGBA(hasPositionData ? 0.7 : 0.5, hasPositionData ? 0.9 : 0.5, hasPositionData ? 0.9 : 0.5, 1);
                ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
                ctx.SetFontSize(11 * sf);

                // Measure and right-align before compass
                var extents = ctx.TextExtents(infoText);
                double infoX = compassX - compassRadius - 8 * sf - extents.Width;
                // Ensure it doesn't overlap with name (leave at least 60px for name)
                infoX = Math.Max(contentLeft + 60 * sf, infoX);
                ctx.MoveTo(infoX, textY);
                ctx.ShowText(infoText);

                // === HP BAR ===
                double barLeft = contentLeft;
                double barWidth = w - barLeft - contentRight;

                ctx.SetSourceRGBA(0.25, 0.08, 0.08, hasPositionData ? 0.9 : 0.5);
                ctx.Rectangle(barLeft, barAreaTop, barWidth, barHeight);
                ctx.Fill();

                if (hasPositionData)
                {
                    float hpPercent = buddy.MaxHealth > 0 ? Math.Min(1f, buddy.Health / buddy.MaxHealth) : 0;
                    ctx.SetSourceRGBA(0.8, 0.2, 0.2, 1);
                    ctx.Rectangle(barLeft, barAreaTop, barWidth * hpPercent, barHeight);
                    ctx.Fill();

                    ctx.SetSourceRGBA(1, 1, 1, 1);
                    ctx.SetFontSize(9 * sf);
                    ctx.MoveTo(barLeft + 4 * sf, barAreaTop + barHeight - 2 * sf);
                    ctx.ShowText($"HP: {buddy.Health:F0}/{buddy.MaxHealth:F0}");
                }
                else
                {
                    // Show "---" for offline HP
                    ctx.SetSourceRGBA(0.5, 0.5, 0.5, 1);
                    ctx.SetFontSize(9 * sf);
                    ctx.MoveTo(barLeft + 4 * sf, barAreaTop + barHeight - 2 * sf);
                    ctx.ShowText("HP: ---");
                }

                // === FOOD BAR ===
                double foodBarY = barAreaTop + barHeight + barGap;

                ctx.SetSourceRGBA(0.18, 0.12, 0.05, hasPositionData ? 0.9 : 0.5);
                ctx.Rectangle(barLeft, foodBarY, barWidth, barHeight);
                ctx.Fill();

                if (hasPositionData)
                {
                    float hungerPercent = buddy.MaxSaturation > 0 ? Math.Min(1f, buddy.Saturation / buddy.MaxSaturation) : 0;
                    ctx.SetSourceRGBA(0.75, 0.55, 0.2, 1);
                    ctx.Rectangle(barLeft, foodBarY, barWidth * hungerPercent, barHeight);
                    ctx.Fill();

                    ctx.SetSourceRGBA(1, 1, 1, 1);
                    ctx.SetFontSize(9 * sf);
                    ctx.MoveTo(barLeft + 4 * sf, foodBarY + barHeight - 2 * sf);
                    ctx.ShowText($"Food: {(hungerPercent * 100):F0}%");
                }
                else
                {
                    // Show "---" for offline Food
                    ctx.SetSourceRGBA(0.5, 0.5, 0.5, 1);
                    ctx.SetFontSize(9 * sf);
                    ctx.MoveTo(barLeft + 4 * sf, foodBarY + barHeight - 2 * sf);
                    ctx.ShowText("Food: ---");
                }
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

        private void LeaveParty()
        {
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.SendPartyLeave();
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

        private void ConfirmInvite()
        {
            if (string.IsNullOrEmpty(selectedBuddyName)) return;

            // Find the buddy's UID
            var buddy = buddyPositions.FirstOrDefault(b => b.Name == selectedBuddyName);
            if (buddy == null || string.IsNullOrEmpty(buddy.PlayerUid)) return;

            // Save color preference locally (for when they join)
            pinnedPlayers[selectedBuddyName] = selectedColorIndex;
            if (!pinnedOrder.Contains(selectedBuddyName))
            {
                pinnedOrder.Add(selectedBuddyName);
            }

            // Send invite to server
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.SendPartyInvite(buddy.PlayerUid);

            isAddingBuddy = false;
            selectedBuddyName = null;
            ComposeDialog();
            SaveSettings();
        }

        private void RemoveFromColorPreferences(string playerName)
        {
            pinnedPlayers.Remove(playerName);
            pinnedOrder.Remove(playerName);
            ComposeDialog();
            NotifyPinnedChanged();
            SaveSettings();
        }

        private void OpenSlotMenu(string playerName, string playerUid, int colorIndex, bool isLeader, int playerIndex, int totalCount)
        {
            // Close any existing menu
            openMenu?.TryClose();

            // Get screen position for the menu (near the slot)
            double menuX = capi.Input.MouseX;
            double menuY = capi.Input.MouseY;

            // Check if this is the current player (self)
            bool isSelf = playerName == capi.World.Player.PlayerName;

            openMenu = new GuiDialogSlotMenu(capi, playerName, playerUid, colorIndex, menuX, menuY, isLeader, isSelf, playerIndex, totalCount, HandleSlotAction);
            openMenu.TryOpen();
        }

        private void HandleSlotAction(string playerName, string playerUid, string action)
        {
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();

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
                    // Send kick packet to server
                    if (!string.IsNullOrEmpty(playerUid))
                    {
                        modSystem?.SendPartyKick(playerUid);
                    }
                    break;
                case "makelead":
                    // Send make lead packet to server
                    if (!string.IsNullOrEmpty(playerUid))
                    {
                        modSystem?.SendPartyMakeLead(playerUid);
                    }
                    break;
                case "leave":
                    // Send leave packet to server
                    modSystem?.SendPartyLeave();
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

        // Custom dragging for title bar - only via drag handle (hamburger icon)
        public override void OnMouseDown(MouseEvent args)
        {
            if (SingleComposer == null) return;

            var bounds = SingleComposer.Bounds;
            double titleHeight = TITLE_BAR_HEIGHT * uiScale * RuntimeEnv.GUIScale;
            double dragHandleWidth = 20 * uiScale * RuntimeEnv.GUIScale;

            // Check if click is on the drag handle (hamburger icon on left)
            if (args.X >= bounds.absX && args.X <= bounds.absX + dragHandleWidth &&
                args.Y >= bounds.absY && args.Y <= bounds.absY + titleHeight)
            {
                isDragging = true;
                dragOffsetX = args.X - bounds.absX;
                dragOffsetY = args.Y - bounds.absY;
                args.Handled = true;
                return;
            }

            // Let buttons and other elements handle clicks
            base.OnMouseDown(args);
        }

        public override void OnMouseUp(MouseEvent args)
        {
            if (isDragging)
            {
                isDragging = false;
                args.Handled = true;
                return;
            }
            base.OnMouseUp(args);
        }

        public override void OnMouseMove(MouseEvent args)
        {
            if (isDragging)
            {
                // Calculate new position in unscaled GUI coordinates
                double scale = RuntimeEnv.GUIScale;
                dialogPosX = (args.X - dragOffsetX) / scale;
                dialogPosY = (args.Y - dragOffsetY) / scale;

                // Keep on screen
                dialogPosX = Math.Max(0, dialogPosX);
                dialogPosY = Math.Max(0, dialogPosY);

                ComposeDialog();
                args.Handled = true;
                return;
            }
            base.OnMouseMove(args);
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

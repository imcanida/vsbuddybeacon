using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VSBuddyBeacon.GUI
{
    public class GuiDialogSlotMenu : GuiDialog
    {
        private string playerName;
        private string playerUid;
        private int currentColorIndex;
        private Action<string, string, string> onAction;  // (playerName, playerUid, action)
        private double posX;
        private double posY;
        private bool isLeader;
        private bool isSelf;
        private int playerIndex;
        private int totalCount;

        public override string ToggleKeyCombinationCode => null;
        public override bool PrefersUngrabbedMouse => true;

        public GuiDialogSlotMenu(ICoreClientAPI capi, string playerName, string playerUid, int colorIndex, double x, double y, bool isLeader, bool isSelf, int playerIndex, int totalCount, Action<string, string, string> onAction) : base(capi)
        {
            this.playerName = playerName;
            this.playerUid = playerUid;
            this.currentColorIndex = colorIndex;
            this.onAction = onAction;
            this.posX = x;
            this.posY = y;
            this.isLeader = isLeader;
            this.isSelf = isSelf;
            this.playerIndex = playerIndex;
            this.totalCount = totalCount;
            ComposeDialog();
        }

        private void ComposeDialog()
        {
            int menuWidth = 120;
            int buttonHeight = 24;
            int padding = 6;
            int gap = 4;

            // Determine which buttons to show based on permissions
            var buttons = new List<(string label, string action)>();

            // Always show color change
            buttons.Add(("Change Color", "changecolor"));

            // Move up - only if not at top and more than 1 member
            if (totalCount > 1 && playerIndex > 0)
            {
                buttons.Add(("Move Up", "moveup"));
            }

            // Move down - only if not at bottom and more than 1 member
            if (totalCount > 1 && playerIndex < totalCount - 1)
            {
                buttons.Add(("Move Down", "movedown"));
            }

            // Leader-only actions for other players
            if (isLeader && !isSelf)
            {
                buttons.Add(("Kick", "kick"));
                buttons.Add(("Make Lead", "makelead"));
            }

            // Leave option - everyone can leave (for self)
            if (isSelf)
            {
                buttons.Add(("Leave Party", "leave"));
            }

            int menuHeight = padding * 2 + buttonHeight * buttons.Count + gap * (buttons.Count - 1);

            // Convert screen coordinates to GUI scale
            double guiScale = RuntimeEnv.GUIScale;
            double scaledX = posX / guiScale;
            double scaledY = posY / guiScale;

            // Keep menu on screen
            double screenWidth = capi.Render.FrameWidth / guiScale;
            double screenHeight = capi.Render.FrameHeight / guiScale;
            if (scaledX + menuWidth > screenWidth) scaledX = screenWidth - menuWidth - 10;
            if (scaledY + menuHeight > screenHeight) scaledY = screenHeight - menuHeight - 10;
            if (scaledX < 0) scaledX = 10;
            if (scaledY < 0) scaledY = 10;

            ElementBounds dialogBounds = ElementBounds.Fixed(scaledX, scaledY, menuWidth, menuHeight);
            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, menuWidth, menuHeight);

            double y = padding;

            var composer = capi.Gui.CreateCompo("slotmenu-" + playerName, dialogBounds)
                .AddStaticCustomDraw(bgBounds, (ctx, surface, bounds) => {
                    // Draw solid dark background
                    ctx.SetSourceRGBA(0.1, 0.1, 0.1, 0.95);
                    ctx.Rectangle(0, 0, bounds.OuterWidth, bounds.OuterHeight);
                    ctx.Fill();
                    // Border
                    ctx.SetSourceRGBA(0.3, 0.3, 0.3, 1);
                    ctx.LineWidth = 1;
                    ctx.Rectangle(0.5, 0.5, bounds.OuterWidth - 1, bounds.OuterHeight - 1);
                    ctx.Stroke();
                })
                .BeginChildElements(bgBounds);

            foreach (var (label, action) in buttons)
            {
                string actionCopy = action;  // Capture for closure
                composer.AddSmallButton(label, () => { OnAction(actionCopy); return true; },
                    ElementBounds.Fixed(padding, y, menuWidth - padding * 2, buttonHeight), EnumButtonStyle.Small);
                y += buttonHeight + gap;
            }

            SingleComposer = composer.EndChildElements().Compose();
        }

        private void OnAction(string action)
        {
            onAction?.Invoke(playerName, playerUid, action);
            TryClose();
        }

        public override void OnMouseDown(MouseEvent args)
        {
            base.OnMouseDown(args);

            // Close if clicking outside the menu
            if (!IsInBounds(args.X, args.Y))
            {
                TryClose();
                args.Handled = true;
            }
        }

        private bool IsInBounds(int x, int y)
        {
            if (SingleComposer == null) return false;
            var bounds = SingleComposer.Bounds;
            return x >= bounds.absX && x <= bounds.absX + bounds.OuterWidth &&
                   y >= bounds.absY && y <= bounds.absY + bounds.OuterHeight;
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            Dispose();
        }
    }
}

using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VSBuddyBeacon.GUI
{
    public class GuiDialogSlotMenu : GuiDialog
    {
        private string playerName;
        private int currentColorIndex;
        private Action<string, string> onAction;  // (playerName, action)
        private double posX;
        private double posY;

        public override string ToggleKeyCombinationCode => null;
        public override bool PrefersUngrabbedMouse => true;

        public GuiDialogSlotMenu(ICoreClientAPI capi, string playerName, int colorIndex, double x, double y, Action<string, string> onAction) : base(capi)
        {
            capi.Logger.Debug($"[VSBuddyBeacon] GuiDialogSlotMenu constructor for {playerName}");
            this.playerName = playerName;
            this.currentColorIndex = colorIndex;
            this.onAction = onAction;
            this.posX = x;
            this.posY = y;
            ComposeDialog();
            capi.Logger.Debug($"[VSBuddyBeacon] GuiDialogSlotMenu composed, SingleComposer null? {SingleComposer == null}");
        }

        private void ComposeDialog()
        {
            int menuWidth = 120;
            int buttonHeight = 24;
            int padding = 6;
            int menuHeight = padding * 2 + buttonHeight * 4 + 3 * 4; // 4 buttons + 3 gaps

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

            SingleComposer = capi.Gui.CreateCompo("slotmenu-" + playerName, dialogBounds)
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
                .BeginChildElements(bgBounds)
                    .AddSmallButton("Change Color", () => { OnAction("changecolor"); return true; },
                        ElementBounds.Fixed(padding, y, menuWidth - padding * 2, buttonHeight), EnumButtonStyle.Small)
                    .AddSmallButton("Move Up", () => { OnAction("moveup"); return true; },
                        ElementBounds.Fixed(padding, y += buttonHeight + 4, menuWidth - padding * 2, buttonHeight), EnumButtonStyle.Small)
                    .AddSmallButton("Move Down", () => { OnAction("movedown"); return true; },
                        ElementBounds.Fixed(padding, y += buttonHeight + 4, menuWidth - padding * 2, buttonHeight), EnumButtonStyle.Small)
                    .AddSmallButton("Kick", () => { OnAction("kick"); return true; },
                        ElementBounds.Fixed(padding, y += buttonHeight + 4, menuWidth - padding * 2, buttonHeight), EnumButtonStyle.Small)
                .EndChildElements()
                .Compose();
        }

        private void OnAction(string action)
        {
            onAction?.Invoke(playerName, action);
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

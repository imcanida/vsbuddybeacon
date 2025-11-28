using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VSBuddyBeacon
{
    public class GuiDialogBeaconCode : GuiDialog
    {
        private ItemSlot itemSlot;
        private string currentCode;

        public override string ToggleKeyCombinationCode => null;

        public GuiDialogBeaconCode(ICoreClientAPI capi, ItemSlot slot, string existingCode) : base(capi)
        {
            this.itemSlot = slot;
            this.currentCode = existingCode ?? "";
            ComposeDialog();
        }

        private void ComposeDialog()
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, 400, 185);

            var composer = capi.Gui
                .CreateCompo("vsbuddybeacon-beaconcode", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Set Beacon Code", () => TryClose())
                .BeginChildElements(bgBounds);

            // Instructions
            composer.AddStaticText(
                "Enter a code to share with friends.\nPlayers with matching codes can see each other.",
                CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(15, 40, 370, 45));

            // Text input
            composer.AddStaticText("Beacon Code:", CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(15, 90, 100, 20));

            composer.AddTextInput(
                ElementBounds.Fixed(15, 110, 370, 28),
                OnTextChanged,
                CairoFont.WhiteSmallText(),
                "beaconCodeInput"
            );

            // Set button
            composer.AddSmallButton("Set Code", OnSetClicked,
                ElementBounds.Fixed(80, 148, 110, 28), EnumButtonStyle.Normal);

            // Clear button
            composer.AddSmallButton("Clear", OnClearClicked,
                ElementBounds.Fixed(210, 148, 110, 28), EnumButtonStyle.Normal);

            SingleComposer = composer.EndChildElements().Compose();

            // Set existing value
            if (!string.IsNullOrEmpty(currentCode))
            {
                SingleComposer.GetTextInput("beaconCodeInput").SetValue(currentCode);
            }
        }

        private void OnTextChanged(string text)
        {
            currentCode = text?.Trim() ?? "";
        }

        private bool OnSetClicked()
        {
            if (string.IsNullOrWhiteSpace(currentCode))
            {
                capi.ShowChatMessage("[BuddyBeacon] Please enter a beacon code.");
                return true;
            }

            // Update item attributes locally
            if (itemSlot?.Itemstack != null)
            {
                itemSlot.Itemstack.Attributes.SetString("beaconCode", currentCode);
                itemSlot.MarkDirty();
            }

            // Send to server
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.SendBeaconCodeUpdate(currentCode);

            capi.ShowChatMessage($"[BuddyBeacon] Beacon code set to \"{currentCode}\"");
            TryClose();
            return true;
        }

        private bool OnClearClicked()
        {
            currentCode = "";

            // Update item attributes
            if (itemSlot?.Itemstack != null)
            {
                itemSlot.Itemstack.Attributes.RemoveAttribute("beaconCode");
                itemSlot.MarkDirty();
            }

            // Send to server (empty = no beacon)
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.SendBeaconCodeUpdate("");

            capi.ShowChatMessage("[BuddyBeacon] Beacon code cleared.");
            TryClose();
            return true;
        }
    }
}

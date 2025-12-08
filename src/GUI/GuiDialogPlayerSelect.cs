using System;
using Vintagestory.API.Client;

namespace VSBuddyBeacon
{
    public class GuiDialogPlayerSelect : GuiDialog
    {
        private TeleportRequestType requestType;
        private string[] playerNames = Array.Empty<string>();
        private string[] playerUids = Array.Empty<string>();
        private string selectedPlayerUid = null;

        public override string ToggleKeyCombinationCode => null;

        // Override to ensure dialog renders above other dialogs (0 = first, 1 = last)
        public override double DrawOrder => 0.9;

        public GuiDialogPlayerSelect(ICoreClientAPI capi, TeleportRequestType requestType) : base(capi)
        {
            this.requestType = requestType;
            ComposeDialog();
        }

        public void UpdatePlayerList(string[] names, string[] uids)
        {
            playerNames = names ?? Array.Empty<string>();
            playerUids = uids ?? Array.Empty<string>();

            // Pre-select first player if available
            if (playerUids.Length > 0)
            {
                selectedPlayerUid = playerUids[0];
            }

            ComposeDialog();
        }

        private void ComposeDialog()
        {
            string title = requestType == TeleportRequestType.TeleportTo
                ? "Teleport To Player"
                : "Summon Player";

            string costText = "Item will be consumed on use";
            string actionVerb = requestType == TeleportRequestType.TeleportTo ? "Teleport" : "Summon";

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, 400, 170);

            var composer = capi.Gui
                .CreateCompo("vsbuddybeacon-playerselect", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(title, () => TryClose())
                .BeginChildElements(bgBounds);

            // Cost info
            composer.AddStaticText(costText, CairoFont.WhiteSmallText().WithColor(new double[] { 0.8, 0.8, 0.6, 1 }),
                ElementBounds.Fixed(15, 40, 370, 20));

            if (playerNames.Length == 0)
            {
                // No players - show message
                composer.AddStaticText("No other players online...",
                    CairoFont.WhiteSmallText().WithColor(new double[] { 0.6, 0.6, 0.6, 1 }),
                    ElementBounds.Fixed(15, 70, 370, 25));

                // Only cancel button
                composer.AddSmallButton("Cancel", () => { TryClose(); return true; },
                    ElementBounds.Fixed(100, 115, 120, 30), EnumButtonStyle.Normal);
            }
            else
            {
                // Player dropdown
                composer.AddStaticText("Select player:", CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(15, 65, 100, 20));

                composer.AddDropDown(
                    playerUids,           // codes (values returned on selection)
                    playerNames,          // display names
                    0,                    // default selected index
                    OnPlayerDropdownChanged,
                    ElementBounds.Fixed(15, 85, 370, 28),
                    CairoFont.WhiteSmallText(),
                    "playerDropdown"
                );

                // Action button
                composer.AddSmallButton(actionVerb, OnConfirmClicked,
                    ElementBounds.Fixed(80, 130, 110, 28), EnumButtonStyle.Normal);

                // Cancel button
                composer.AddSmallButton("Cancel", () => { TryClose(); return true; },
                    ElementBounds.Fixed(210, 130, 110, 28), EnumButtonStyle.Normal);
            }

            SingleComposer = composer.EndChildElements().Compose();
        }

        private void OnPlayerDropdownChanged(string code, bool selected)
        {
            selectedPlayerUid = code;
        }

        private bool OnConfirmClicked()
        {
            if (string.IsNullOrEmpty(selectedPlayerUid))
            {
                return true;
            }

            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.SendTeleportRequest(selectedPlayerUid, requestType);
            TryClose();
            return true;
        }
    }
}

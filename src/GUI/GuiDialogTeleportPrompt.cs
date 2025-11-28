using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VSBuddyBeacon
{
    public class GuiDialogTeleportPrompt : GuiDialog
    {
        private string message;
        private long requestId;

        public override string ToggleKeyCombinationCode => null;

        public GuiDialogTeleportPrompt(ICoreClientAPI capi, string message, long requestId) : base(capi)
        {
            this.message = message;
            this.requestId = requestId;
            ComposeDialog();
        }

        private void ComposeDialog()
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, 400, 140);

            var composer = capi.Gui
                .CreateCompo("vsbuddybeacon-teleportprompt", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Teleport Request", () => OnDecline())
                .BeginChildElements(bgBounds);

            // Message
            composer.AddStaticText(message, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 45, 360, 40));

            // Timer hint
            composer.AddStaticText("(30 seconds to respond)",
                CairoFont.WhiteSmallText().WithColor(new double[] { 0.6, 0.6, 0.6, 1 }),
                ElementBounds.Fixed(20, 75, 360, 20));

            // Accept button
            composer.AddSmallButton("Accept", () => { OnAccept(); return true; },
                ElementBounds.Fixed(80, 100, 110, 28), EnumButtonStyle.Normal);

            // Decline button
            composer.AddSmallButton("Decline", () => { OnDecline(); return true; },
                ElementBounds.Fixed(210, 100, 110, 28), EnumButtonStyle.Normal);

            SingleComposer = composer.EndChildElements().Compose();
        }

        private void OnAccept()
        {
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.SendTeleportResponse(requestId, true);
            TryClose();
        }

        private void OnDecline()
        {
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.SendTeleportResponse(requestId, false);
            TryClose();
        }
    }
}

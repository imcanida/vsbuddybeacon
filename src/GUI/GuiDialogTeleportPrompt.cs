using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VSBuddyBeacon
{
    public class GuiDialogTeleportPrompt : GuiDialog
    {
        private string message;
        private long requestId;
        private int requestCount;
        private string requesterUid;

        public override string ToggleKeyCombinationCode => null;

        public GuiDialogTeleportPrompt(ICoreClientAPI capi, string message, long requestId, int requestCount = 0, string requesterUid = null) : base(capi)
        {
            this.message = message;
            this.requestId = requestId;
            this.requestCount = requestCount;
            this.requesterUid = requesterUid;
            ComposeDialog();
        }

        private void ComposeDialog()
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            // Show silence button if this is a repeat request (2nd or more)
            bool showSilenceButton = requestCount > 1;
            double dialogHeight = showSilenceButton ? 170 : 140;

            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, 400, dialogHeight);

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

            if (showSilenceButton)
            {
                // Spam warning
                composer.AddStaticText($"(Repeat request #{requestCount})",
                    CairoFont.WhiteSmallText().WithColor(new double[] { 1.0, 0.6, 0.2, 1 }),
                    ElementBounds.Fixed(20, 95, 360, 20));

                // Accept button
                composer.AddSmallButton("Accept", () => { OnAccept(); return true; },
                    ElementBounds.Fixed(40, 130, 100, 28), EnumButtonStyle.Normal);

                // Decline button
                composer.AddSmallButton("Decline", () => { OnDecline(); return true; },
                    ElementBounds.Fixed(150, 130, 100, 28), EnumButtonStyle.Normal);

                // Silence button (red/warning style)
                composer.AddSmallButton("Silence 10min", () => { OnSilence(); return true; },
                    ElementBounds.Fixed(260, 130, 110, 28), EnumButtonStyle.Normal);
            }
            else
            {
                // Accept button
                composer.AddSmallButton("Accept", () => { OnAccept(); return true; },
                    ElementBounds.Fixed(80, 100, 110, 28), EnumButtonStyle.Normal);

                // Decline button
                composer.AddSmallButton("Decline", () => { OnDecline(); return true; },
                    ElementBounds.Fixed(210, 100, 110, 28), EnumButtonStyle.Normal);
            }

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

        private void OnSilence()
        {
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            if (modSystem != null && !string.IsNullOrEmpty(requesterUid))
            {
                modSystem.SendSilencePlayer(requesterUid);
            }
            // Also decline the current request
            modSystem?.SendTeleportResponse(requestId, false);
            TryClose();
        }
    }
}

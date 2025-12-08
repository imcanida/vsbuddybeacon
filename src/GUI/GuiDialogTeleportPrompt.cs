using System;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VSBuddyBeacon
{
    public class GuiDialogTeleportPrompt : GuiDialog
    {
        private const float TIMEOUT_SECONDS = 30f;

        private string message;
        private long requestId;
        private int requestCount;
        private string requesterUid;
        private long clientStartTime; // When this dialog was opened (client time)
        private long timerId;

        private GuiElementDynamicText countdownText;

        public override string ToggleKeyCombinationCode => null;

        // Override to ensure dialog renders above other dialogs (0 = first, 1 = last; escape menu uses 0.89)
        public override double DrawOrder => 0.9;

        public GuiDialogTeleportPrompt(ICoreClientAPI capi, string message, long requestId, int requestCount = 0, string requesterUid = null, long requestTimestamp = 0) : base(capi)
        {
            this.message = message;
            this.requestId = requestId;
            this.requestCount = requestCount;
            this.requesterUid = requesterUid;
            // Use client time when dialog opens, ignore server timestamp to avoid clock sync issues
            this.clientStartTime = capi.World.ElapsedMilliseconds;
            ComposeDialog();

            // Register timer to update countdown every 100ms
            timerId = capi.Event.RegisterGameTickListener(OnTimerTick, 100);
        }

        private void ComposeDialog()
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            // Show silence button if this is a repeat request (2nd request or more)
            // requestCount: 1 = first request, 2 = second request, etc.
            bool showSilenceButton = requestCount >= 2;
            double dialogHeight = showSilenceButton ? 170 : 140;

            // Debug logging
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            if (modSystem?.IsVerboseLoggingEnabled() == true)
            {
                capi.Logger.Notification($"[VSBuddyBeacon] Teleport prompt received. RequestCount: {requestCount}, ShowSilenceButton: {showSilenceButton}");
            }

            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, 400, dialogHeight);

            var composer = capi.Gui
                .CreateCompo("vsbuddybeacon-teleportprompt", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Teleport Request", () => OnDecline())
                .BeginChildElements(bgBounds);

            // Message
            composer.AddStaticText(message, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 45, 360, 40));

            // Timer countdown (dynamic)
            composer.AddDynamicText(GetCountdownText(),
                CairoFont.WhiteSmallText().WithColor(new double[] { 0.6, 0.6, 0.6, 1 }),
                ElementBounds.Fixed(20, 75, 360, 20),
                "countdown");

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

            // Get reference to countdown text element for updates
            countdownText = SingleComposer.GetDynamicText("countdown");
        }

        private string GetCountdownText()
        {
            float remaining = GetRemainingSeconds();
            if (remaining <= 0)
                return "(Expired)";

            return $"({(int)Math.Ceiling(remaining)} seconds to respond)";
        }

        private float GetRemainingSeconds()
        {
            // Calculate elapsed time since dialog opened (using client clock only)
            long now = capi.World.ElapsedMilliseconds;
            long elapsed = now - clientStartTime;
            float remaining = TIMEOUT_SECONDS - (elapsed / 1000f);
            return remaining;
        }

        private void OnTimerTick(float dt)
        {
            if (!IsOpened() || countdownText == null)
            {
                // Dialog closed, unregister timer
                capi.Event.UnregisterGameTickListener(timerId);
                return;
            }

            float remaining = GetRemainingSeconds();

            // Update countdown text
            countdownText.SetNewText(GetCountdownText());

            // Auto-close if expired
            if (remaining <= 0)
            {
                capi.Event.UnregisterGameTickListener(timerId);
                capi.ShowChatMessage("[BuddyBeacon] Teleport request expired.");
                TryClose();
            }
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            // Cleanup timer if dialog is closed manually
            capi.Event.UnregisterGameTickListener(timerId);
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

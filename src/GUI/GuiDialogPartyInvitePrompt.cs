using System;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VSBuddyBeacon
{
    public class GuiDialogPartyInvitePrompt : GuiDialog
    {
        private const float TIMEOUT_SECONDS = 30f;

        private string inviterName;
        private long inviteId;
        private string inviterUid;
        private int requestCount;
        private long clientStartTime;
        private long timerId;

        private GuiElementDynamicText countdownText;

        public override string ToggleKeyCombinationCode => null;

        // Override to ensure dialog renders above other dialogs
        public override double DrawOrder => 0.9;

        public GuiDialogPartyInvitePrompt(ICoreClientAPI capi, string inviterName, long inviteId, string inviterUid, long requestTimestamp, int requestCount) : base(capi)
        {
            this.inviterName = inviterName;
            this.inviteId = inviteId;
            this.inviterUid = inviterUid;
            this.requestCount = requestCount;
            this.clientStartTime = capi.World.ElapsedMilliseconds;
            ComposeDialog();

            timerId = capi.Event.RegisterGameTickListener(OnTimerTick, 100);
        }

        private void ComposeDialog()
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            bool showSilenceButton = requestCount >= 2;
            double dialogHeight = showSilenceButton ? 170 : 140;

            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, 400, dialogHeight);

            var composer = capi.Gui
                .CreateCompo("vsbuddybeacon-partyinviteprompt", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Party Invite", () => OnDecline())
                .BeginChildElements(bgBounds);

            // Message
            composer.AddStaticText($"{inviterName} invites you to a party, accept?",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 45, 360, 40));

            // Timer countdown
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

                // Yes button
                composer.AddSmallButton("Yes", () => { OnAccept(); return true; },
                    ElementBounds.Fixed(40, 130, 90, 28), EnumButtonStyle.Normal);

                // No button
                composer.AddSmallButton("No", () => { OnDecline(); return true; },
                    ElementBounds.Fixed(140, 130, 90, 28), EnumButtonStyle.Normal);

                // Silence button
                composer.AddSmallButton("Silence 10min", () => { OnSilence(); return true; },
                    ElementBounds.Fixed(240, 130, 120, 28), EnumButtonStyle.Normal);
            }
            else
            {
                // Yes button
                composer.AddSmallButton("Yes", () => { OnAccept(); return true; },
                    ElementBounds.Fixed(80, 100, 110, 28), EnumButtonStyle.Normal);

                // No button
                composer.AddSmallButton("No", () => { OnDecline(); return true; },
                    ElementBounds.Fixed(210, 100, 110, 28), EnumButtonStyle.Normal);
            }

            SingleComposer = composer.EndChildElements().Compose();
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
            long now = capi.World.ElapsedMilliseconds;
            long elapsed = now - clientStartTime;
            float remaining = TIMEOUT_SECONDS - (elapsed / 1000f);
            return remaining;
        }

        private void OnTimerTick(float dt)
        {
            if (!IsOpened() || countdownText == null)
            {
                capi.Event.UnregisterGameTickListener(timerId);
                return;
            }

            float remaining = GetRemainingSeconds();
            countdownText.SetNewText(GetCountdownText());

            if (remaining <= 0)
            {
                capi.Event.UnregisterGameTickListener(timerId);
                capi.ShowChatMessage("[BuddyBeacon] Party invite expired.");
                TryClose();
            }
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            capi.Event.UnregisterGameTickListener(timerId);
        }

        private void OnAccept()
        {
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.SendPartyInviteResponse(inviteId, true);
            TryClose();
        }

        private void OnDecline()
        {
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.SendPartyInviteResponse(inviteId, false);
            TryClose();
        }

        private void OnSilence()
        {
            var modSystem = capi.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            if (modSystem != null && !string.IsNullOrEmpty(inviterUid))
            {
                modSystem.SendSilencePlayer(inviterUid);
            }
            modSystem?.SendPartyInviteResponse(inviteId, false);
            TryClose();
        }
    }
}

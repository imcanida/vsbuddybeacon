using Vintagestory.API.Common;
using Vintagestory.API.Client;

namespace VSBuddyBeacon
{
    /// <summary>
    /// Wayfinder's Compass - Teleport yourself TO another player (with their consent)
    /// </summary>
    public class ItemWayfinderCompass : Item
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            if (api.Side != EnumAppSide.Client)
            {
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            // Open player selection dialog
            var modSystem = api.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.OpenPlayerSelectDialog(TeleportRequestType.TeleportTo);

            handling = EnumHandHandling.PreventDefault;
        }

        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            return new WorldInteraction[]
            {
                new WorldInteraction()
                {
                    ActionLangCode = "vsbuddybeacon:heldhelp-wayfindercompass",
                    MouseButton = EnumMouseButton.Right
                }
            };
        }
    }
}

using Vintagestory.API.Common;
using Vintagestory.API.Client;

namespace VSBuddyBeacon
{
    /// <summary>
    /// Hero's Call Stone - Summon another player TO you (with their consent)
    /// Like EverQuest's "Call of the Hero" spell
    /// </summary>
    public class ItemHerosCallStone : Item
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
            modSystem?.OpenPlayerSelectDialog(TeleportRequestType.Summon);

            handling = EnumHandHandling.PreventDefault;
        }

        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            return new WorldInteraction[]
            {
                new WorldInteraction()
                {
                    ActionLangCode = "vsbuddybeacon:heldhelp-heroscallstone",
                    MouseButton = EnumMouseButton.Right
                }
            };
        }
    }
}

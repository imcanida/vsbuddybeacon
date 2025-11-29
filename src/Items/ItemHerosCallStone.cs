using System.Text;
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
        private bool IsEnabled()
        {
            var modSystem = api.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            return modSystem?.IsItemEnabled("heroscallstone") ?? true;
        }

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            // If disabled, do nothing
            if (!IsEnabled())
            {
                handling = EnumHandHandling.PreventDefault;
                return;
            }

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

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            if (!IsEnabled())
            {
                dsc.Clear();
                dsc.AppendLine("An inert object with untapped potential...");
                return;
            }

            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        }

        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            if (!IsEnabled())
            {
                return new WorldInteraction[0];
            }

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

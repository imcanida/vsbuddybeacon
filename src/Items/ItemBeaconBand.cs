using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace VSBuddyBeacon
{
    /// <summary>
    /// Beacon Band - Set a beacon code to see other players with the same code on your compass
    /// Works while worn in equipment slot or kept in inventory
    /// </summary>
    public class ItemBeaconBand : ItemWearable
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            if (api.Side != EnumAppSide.Client)
            {
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            // Get current code from item
            string currentCode = slot.Itemstack.Attributes.GetString("beaconCode", "");

            // Open code entry dialog
            var modSystem = api.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.OpenBeaconCodeDialog(slot, currentCode);

            handling = EnumHandHandling.PreventDefault;
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

            string code = inSlot.Itemstack.Attributes.GetString("beaconCode", "");
            if (!string.IsNullOrEmpty(code))
            {
                dsc.AppendLine($"Beacon Code: \"{code}\"");
                dsc.AppendLine("Players with matching codes will appear on your compass.");
                dsc.AppendLine("Works while worn or kept in inventory.");
            }
            else
            {
                dsc.AppendLine("No beacon code set. Right-click to set one.");
                dsc.AppendLine("Works while worn or kept in inventory.");
            }
        }

        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            return new WorldInteraction[]
            {
                new WorldInteraction()
                {
                    ActionLangCode = "vsbuddybeacon:heldhelp-beaconband",
                    MouseButton = EnumMouseButton.Right
                }
            };
        }
    }
}

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
        private bool IsEnabled()
        {
            var modSystem = api.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            return modSystem?.IsItemEnabled("beaconband") ?? true;
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

            // Get current code from item
            string currentCode = slot.Itemstack.Attributes.GetString("beaconCode", "");

            // Open code entry dialog
            var modSystem = api.ModLoader.GetModSystem<VSBuddyBeaconModSystem>();
            modSystem?.OpenBeaconCodeDialog(slot, currentCode);

            handling = EnumHandHandling.PreventDefault;
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            // Show inert description if disabled (replace everything)
            if (!IsEnabled())
            {
                dsc.Clear();
                dsc.AppendLine("An inert object with untapped potential...");
                return;
            }

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
            // No interaction help if disabled
            if (!IsEnabled())
            {
                return new WorldInteraction[0];
            }

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

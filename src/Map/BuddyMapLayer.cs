using System;
using System.Collections.Generic;
using System.Text;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VSBuddyBeacon
{
    public class BuddyMapLayer : MapLayer
    {
        private Dictionary<string, BuddyMapComponent> buddyComponents = new Dictionary<string, BuddyMapComponent>();
        private ICoreClientAPI capi;
        private LoadedTexture buddyTexture;

        public override string Title => "Buddy Beacons";
        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
        public override string LayerGroupCode => "buddybeacons";

        public BuddyMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
        {
            capi = api as ICoreClientAPI;
        }

        private void EnsureTextureCreated()
        {
            if (capi == null || buddyTexture != null) return;

            int size = (int)GuiElement.scaled(24);
            ImageSurface surface = new ImageSurface(Format.Argb32, size, size);
            Context ctx = new Context(surface);

            // Clear
            ctx.SetSourceRGBA(0, 0, 0, 0);
            ctx.Paint();

            // Draw a green circle with white border
            double cx = size / 2.0;
            double cy = size / 2.0;
            double radius = size / 2.0 - 2;

            // White border
            ctx.SetSourceRGBA(1, 1, 1, 1);
            ctx.Arc(cx, cy, radius, 0, 2 * Math.PI);
            ctx.Fill();

            // Green fill
            ctx.SetSourceRGBA(0.2, 0.8, 0.2, 1);
            ctx.Arc(cx, cy, radius - 2, 0, 2 * Math.PI);
            ctx.Fill();

            buddyTexture = new LoadedTexture(capi, capi.Gui.LoadCairoTexture(surface, false), size / 2, size / 2);

            ctx.Dispose();
            surface.Dispose();
        }

        public override void OnMapOpenedClient()
        {
            EnsureTextureCreated();

            // Clear existing components
            foreach (var comp in buddyComponents.Values)
            {
                comp?.Dispose();
            }
            buddyComponents.Clear();
        }

        public void UpdateBuddyPositions(List<BuddyPosition> positions)
        {
            if (capi == null) return;
            EnsureTextureCreated();
            if (buddyTexture == null) return;

            // Update or create components for each buddy
            HashSet<string> currentBuddies = new HashSet<string>();

            foreach (var buddy in positions)
            {
                currentBuddies.Add(buddy.Name);

                if (buddyComponents.TryGetValue(buddy.Name, out var existing))
                {
                    // Update existing
                    existing.UpdatePosition(buddy.Position);
                }
                else
                {
                    // Create new
                    buddyComponents[buddy.Name] = new BuddyMapComponent(capi, buddyTexture, buddy.Name, buddy.Position);
                }
            }

            // Remove buddies that are no longer in the list
            List<string> toRemove = new List<string>();
            foreach (var name in buddyComponents.Keys)
            {
                if (!currentBuddies.Contains(name))
                {
                    toRemove.Add(name);
                }
            }
            foreach (var name in toRemove)
            {
                buddyComponents[name]?.Dispose();
                buddyComponents.Remove(name);
            }
        }

        public override void Render(GuiElementMap mapElem, float dt)
        {
            if (!Active) return;

            foreach (var comp in buddyComponents.Values)
            {
                comp.Render(mapElem, dt);
            }
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            if (!Active) return;

            foreach (var comp in buddyComponents.Values)
            {
                comp.OnMouseMove(args, mapElem, hoverText);
            }
        }

        public override void Dispose()
        {
            foreach (var comp in buddyComponents.Values)
            {
                comp?.Dispose();
            }
            buddyComponents.Clear();

            buddyTexture?.Dispose();
            buddyTexture = null;

            base.Dispose();
        }
    }
}

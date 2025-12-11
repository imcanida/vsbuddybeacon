using System.Collections.Generic;

namespace VSBuddyBeacon.Config
{
    public class ModConfig
    {
        public bool VerboseLogging { get; set; } = false;

        /// <summary>
        /// Enable the Party List HUD (press P to toggle). If disabled, party list UI won't be created.
        /// Note: Beacon positions are still sent for compass/map features - this only hides the party UI.
        /// </summary>
        public bool EnablePartyList { get; set; } = true;

        /// <summary>
        /// Enable map pinging (middle-click on map to ping location to party members).
        /// If disabled on server, all pings are rejected. If disabled on client, ping UI is hidden.
        /// </summary>
        public bool EnableMapPings { get; set; } = true;

        /// <summary>
        /// Party list UI scale (0.9 to 1.5)
        /// </summary>
        public float PartyListScale { get; set; } = 1.0f;

        /// <summary>
        /// Pinned players with their color indices
        /// </summary>
        public Dictionary<string, int> PinnedPlayers { get; set; } = new Dictionary<string, int>();

        public Dictionary<string, ItemConfig> Items { get; set; } = new Dictionary<string, ItemConfig>
        {
            ["wayfindercompass"] = new ItemConfig(),
            ["heroscallstone"] = new ItemConfig(),
            ["beaconband"] = new ItemConfig { GiveOnFirstJoin = true }
        };

        public ItemConfig GetItemConfig(string itemCode)
        {
            return Items.TryGetValue(itemCode, out var config) ? config : null;
        }

        public bool IsItemEnabled(string itemCode)
        {
            var config = GetItemConfig(itemCode);
            return config?.Enabled ?? false;
        }
    }

    public class ItemConfig
    {
        public bool Enabled { get; set; } = true;
        public bool AllowCrafting { get; set; } = true;
        public bool GiveOnFirstJoin { get; set; } = false;
        public CustomRecipe CustomRecipe { get; set; } = null;
    }

    public class CustomRecipe
    {
        public string IngredientPattern { get; set; }
        public Dictionary<string, RecipeIngredient> Ingredients { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public RecipeOutput Output { get; set; } = new RecipeOutput();
    }

    public class RecipeIngredient
    {
        public string Type { get; set; } // "item" or "block"
        public string Code { get; set; }
        public int Quantity { get; set; } = 1;
        public string[] AllowedVariants { get; set; } = null;
    }

    public class RecipeOutput
    {
        public int Quantity { get; set; } = 1;
    }
}

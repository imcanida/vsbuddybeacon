using System.Collections.Generic;

namespace VSBuddyBeacon.Config
{
    public class ModConfig
    {
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

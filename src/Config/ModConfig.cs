using System.Collections.Generic;

namespace VSBuddyBeacon.Config
{
    /// <summary>
    /// How health/saturation data is sent with position updates
    /// </summary>
    public enum HealthDataMode
    {
        /// <summary>Send every update (real-time HP bars, most bandwidth)</summary>
        Always,
        /// <summary>Only send when values change significantly (recommended balance)</summary>
        OnChange,
        /// <summary>Never send health data (minimal bandwidth, no HP/food bars)</summary>
        Never
    }

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

        #region Network Optimization Settings

        /// <summary>
        /// How often beacon positions are broadcast in seconds.
        /// Higher values reduce bandwidth but make tracking less responsive.
        /// Default: 1.0, Range: 0.5 - 10.0
        /// </summary>
        public float BeaconUpdateInterval { get; set; } = 1.0f;

        /// <summary>
        /// Maximum players allowed in a single beacon group.
        /// Prevents O(n²) bandwidth explosion with large groups.
        /// 0 = unlimited (default), Suggested: 8-12 for large servers
        /// </summary>
        public int MaxBeaconGroupSize { get; set; } = 0;

        /// <summary>
        /// Minimum distance (meters) a player must move before position update is sent.
        /// Reduces bandwidth for stationary players.
        /// 0 = always send (default), Suggested: 0.5-2.0 for bandwidth savings
        /// </summary>
        public float PositionChangeThreshold { get; set; } = 0f;

        /// <summary>
        /// How health/saturation data is sent with position updates.
        /// - Always: Every update (real-time, most bandwidth)
        /// - OnChange: Only when values change (recommended)
        /// - Never: Disable entirely (no HP/food bars in party HUD)
        /// </summary>
        public HealthDataMode HealthDataMode { get; set; } = HealthDataMode.OnChange;

        /// <summary>
        /// Threshold for health change detection when HealthDataMode is OnChange.
        /// Health must change by at least this amount to trigger an update.
        /// Default: 0.5 HP
        /// </summary>
        public float HealthChangeThreshold { get; set; } = 0.5f;

        /// <summary>
        /// Threshold for saturation change detection when HealthDataMode is OnChange.
        /// Saturation must change by at least this amount to trigger an update.
        /// Default: 25 (about 2% of max 1200)
        /// </summary>
        public float SaturationChangeThreshold { get; set; } = 25f;

        /// <summary>
        /// Enable distance-based Level of Detail for beacon updates.
        /// Distant players receive updates less frequently than nearby players.
        /// </summary>
        public bool EnableDistanceLod { get; set; } = true;

        /// <summary>
        /// Near distance threshold (meters). Players within this range get full update rate.
        /// Default: 100
        /// </summary>
        public float LodNearDistance { get; set; } = 100f;

        /// <summary>
        /// Mid distance threshold (meters). Players between near and mid get half update rate.
        /// Players beyond mid distance get quarter update rate.
        /// Default: 500
        /// </summary>
        public float LodMidDistance { get; set; } = 500f;

        #endregion

        /// <summary>
        /// Party list UI scale (0.9 to 1.5)
        /// </summary>
        public float PartyListScale { get; set; } = 1.0f;

        /// <summary>
        /// Personal color preferences for party members (playerName -> colorIndex).
        /// Each player chooses their own colors for viewing other party members.
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

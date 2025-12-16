using Vintagestory.API.MathTools;

namespace VSBuddyBeacon
{
    /// <summary>
    /// Extended buddy position with timestamp tracking for staleness detection
    /// </summary>
    public class BuddyPositionWithTimestamp
    {
        public string Name { get; set; }
        public string PlayerUid { get; set; }  // Player UID for party invites
        public Vec3d Position { get; set; }
        public long ServerTimestamp { get; set; }      // Server's ElapsedMilliseconds when position was captured
        public long ClientReceivedTime { get; set; }   // Client's ElapsedMilliseconds when packet was received

        // RPG stats
        public float Health { get; set; }
        public float MaxHealth { get; set; }
        public float Saturation { get; set; }
        public float MaxSaturation { get; set; }

        /// <summary>
        /// Calculate age in seconds based on current client time
        /// </summary>
        public float GetAgeSinceReceived(long currentClientTime)
        {
            return (currentClientTime - ClientReceivedTime) / 1000f;
        }

        /// <summary>
        /// Determine staleness level for color coding
        /// </summary>
        public StalenessLevel GetStalenessLevel(long currentClientTime)
        {
            float age = GetAgeSinceReceived(currentClientTime);

            if (age < 3f) return StalenessLevel.Fresh;
            if (age < 10f) return StalenessLevel.Aging;
            if (age < 30f) return StalenessLevel.Stale;
            if (age < 60f) return StalenessLevel.VeryStale;
            return StalenessLevel.Expired;
        }
    }

    public enum StalenessLevel
    {
        Fresh,      // 0-3s: Green
        Aging,      // 3-10s: Yellow
        Stale,      // 10-30s: Orange
        VeryStale,  // 30-60s: Red
        Expired     // 60s+: Should be removed
    }
}

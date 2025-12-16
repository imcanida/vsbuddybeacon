using ProtoBuf;

namespace VSBuddyBeacon
{
    [ProtoContract]
    public class BeaconCodeSetPacket
    {
        [ProtoMember(1)]
        public string BeaconCode { get; set; }
    }

    [ProtoContract]
    public class BeaconPositionPacket
    {
        [ProtoMember(1)]
        public string[] PlayerNames { get; set; }

        [ProtoMember(2)]
        public double[] PosX { get; set; }

        [ProtoMember(3)]
        public double[] PosY { get; set; }

        [ProtoMember(4)]
        public double[] PosZ { get; set; }

        [ProtoMember(5)]
        public long[] Timestamps { get; set; }  // Server's ElapsedMilliseconds when position was captured

        [ProtoMember(6)]
        public float[] Health { get; set; }  // Current health

        [ProtoMember(7)]
        public float[] MaxHealth { get; set; }  // Max health

        [ProtoMember(8)]
        public float[] Saturation { get; set; }  // Current saturation (hunger)

        [ProtoMember(9)]
        public float[] MaxSaturation { get; set; }  // Max saturation

        [ProtoMember(10)]
        public string[] PlayerUids { get; set; }  // Player UIDs for party invites
    }

    [ProtoContract]
    public class MapPingPacket
    {
        [ProtoMember(1)]
        public string SenderName { get; set; }

        [ProtoMember(2)]
        public double PosX { get; set; }

        [ProtoMember(3)]
        public double PosZ { get; set; }

        [ProtoMember(4)]
        public long Timestamp { get; set; }
    }

    [ProtoContract]
    public class FakeBuddySpawnPacket
    {
        [ProtoMember(1)]
        public double[] PosX { get; set; }

        [ProtoMember(2)]
        public double[] PosY { get; set; }

        [ProtoMember(3)]
        public double[] PosZ { get; set; }

        [ProtoMember(4)]
        public string[] Names { get; set; }
    }

    [ProtoContract]
    public class FakeBuddyClearPacket
    {
        // Empty packet - just signals to clear
    }

    [ProtoContract]
    public class BuddyChatPacket
    {
        [ProtoMember(1)]
        public string SenderName { get; set; }

        [ProtoMember(2)]
        public string Message { get; set; }

        [ProtoMember(3)]
        public string[] TargetNames { get; set; }  // null/empty = all group members, otherwise specific players

        [ProtoMember(4)]
        public bool IsPartyChat { get; set; }  // true = party (pinned), false = group (all beacon)
    }
}

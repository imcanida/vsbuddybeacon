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
    }
}

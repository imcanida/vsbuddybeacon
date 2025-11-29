using ProtoBuf;

namespace VSBuddyBeacon
{
    public enum TeleportRequestType
    {
        TeleportTo,  // Requester wants to go TO target
        Summon       // Requester wants to SUMMON target to them
    }

    [ProtoContract]
    public class TeleportRequestPacket
    {
        [ProtoMember(1)]
        public string RequesterPlayerUid { get; set; }

        [ProtoMember(2)]
        public string TargetPlayerUid { get; set; }

        [ProtoMember(3)]
        public TeleportRequestType RequestType { get; set; }

        [ProtoMember(4)]
        public long RequestId { get; set; }
    }

    [ProtoContract]
    public class TeleportResponsePacket
    {
        [ProtoMember(1)]
        public long RequestId { get; set; }

        [ProtoMember(2)]
        public bool Accepted { get; set; }

        [ProtoMember(3)]
        public string ResponderPlayerUid { get; set; }
    }

    [ProtoContract]
    public class TeleportPromptPacket
    {
        [ProtoMember(1)]
        public string RequesterName { get; set; }

        [ProtoMember(2)]
        public TeleportRequestType RequestType { get; set; }

        [ProtoMember(3)]
        public long RequestId { get; set; }

        [ProtoMember(4)]
        public int RequestCount { get; set; }  // How many times this player has requested

        [ProtoMember(5)]
        public string RequesterUid { get; set; }  // UID of requester for silencing
    }

    [ProtoContract]
    public class TeleportResultPacket
    {
        [ProtoMember(1)]
        public bool Success { get; set; }

        [ProtoMember(2)]
        public string Message { get; set; }
    }

    [ProtoContract]
    public class PlayerListRequestPacket
    {
        // Empty - just requests the list
    }

    [ProtoContract]
    public class PlayerListResponsePacket
    {
        [ProtoMember(1)]
        public string[] PlayerNames { get; set; }

        [ProtoMember(2)]
        public string[] PlayerUids { get; set; }
    }

    [ProtoContract]
    public class SilencePlayerPacket
    {
        [ProtoMember(1)]
        public string PlayerUidToSilence { get; set; }  // The player being silenced
    }
}

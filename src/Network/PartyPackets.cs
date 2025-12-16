using ProtoBuf;

namespace VSBuddyBeacon
{
    // Server-side party data structure
    public class Party
    {
        public long PartyId { get; set; }
        public string OriginalLeaderUid { get; set; }  // The "true" leader (for auto-restore on reconnect)
        public string LeaderUid { get; set; }          // Current acting leader (may differ if original offline)
        public System.Collections.Generic.List<string> MemberUids { get; set; } = new();
        public System.Collections.Generic.Dictionary<string, string> MemberNames { get; set; } = new();  // uid -> name
        public long CreatedTime { get; set; }
    }

    // Server-side pending invite tracking
    public class PendingPartyInvite
    {
        public long InviteId { get; set; }
        public string InviterUid { get; set; }
        public string TargetUid { get; set; }
        public long PartyId { get; set; }  // 0 if creating new party
        public long RequestTime { get; set; }
    }

    // Client -> Server: Request to invite a player to party
    [ProtoContract]
    public class PartyInvitePacket
    {
        [ProtoMember(1)]
        public string TargetPlayerUid { get; set; }
    }

    // Server -> Target: Prompt the target to accept/decline invite
    [ProtoContract]
    public class PartyInvitePromptPacket
    {
        [ProtoMember(1)]
        public string InviterName { get; set; }

        [ProtoMember(2)]
        public long InviteId { get; set; }

        [ProtoMember(3)]
        public string InviterUid { get; set; }  // For silence feature

        [ProtoMember(4)]
        public long RequestTimestamp { get; set; }

        [ProtoMember(5)]
        public int RequestCount { get; set; }  // For repeat request detection
    }

    // Target -> Server: Response to party invite
    [ProtoContract]
    public class PartyInviteResponsePacket
    {
        [ProtoMember(1)]
        public long InviteId { get; set; }

        [ProtoMember(2)]
        public bool Accepted { get; set; }
    }

    // Server -> All party members: Current party state
    [ProtoContract]
    public class PartyStatePacket
    {
        [ProtoMember(1)]
        public long PartyId { get; set; }

        [ProtoMember(2)]
        public string LeaderUid { get; set; }

        [ProtoMember(3)]
        public string LeaderName { get; set; }

        [ProtoMember(4)]
        public string[] MemberUids { get; set; }

        [ProtoMember(5)]
        public string[] MemberNames { get; set; }

        [ProtoMember(6)]
        public bool[] MemberOnline { get; set; }

        [ProtoMember(7)]
        public string OriginalLeaderUid { get; set; }

        [ProtoMember(8)]
        public string OriginalLeaderName { get; set; }
    }

    // Client -> Server: Leave current party
    [ProtoContract]
    public class PartyLeavePacket
    {
        // Empty - just signals intent to leave
    }

    // Leader -> Server: Kick a member from party
    [ProtoContract]
    public class PartyKickPacket
    {
        [ProtoMember(1)]
        public string TargetPlayerUid { get; set; }
    }

    // Leader -> Server: Transfer leadership to another member
    [ProtoContract]
    public class PartyMakeLeadPacket
    {
        [ProtoMember(1)]
        public string TargetPlayerUid { get; set; }
    }

    // Server -> Kicked/Left player: Notify of removal from party
    [ProtoContract]
    public class PartyDisbandedPacket
    {
        [ProtoMember(1)]
        public string Reason { get; set; }  // "kicked", "left", "leader_left", "disbanded"
    }

    // Server -> Inviter: Result of invite attempt
    [ProtoContract]
    public class PartyInviteResultPacket
    {
        [ProtoMember(1)]
        public bool Success { get; set; }

        [ProtoMember(2)]
        public string Message { get; set; }
    }
}

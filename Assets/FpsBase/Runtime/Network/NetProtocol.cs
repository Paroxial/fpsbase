namespace FpsBase
{
    public enum MessageType : byte
    {
        Hello = 1,
        Welcome,
        Input,
        Snapshot,
        Bye,
        Probe,
        ProbeReply,
        Ping,
        Pong,
        Scores,
    }

    public enum CommandType : byte
    {
        Fire = 1,
        Select,
        PickUp,
        Drop,
    }

    public enum NetEventType : byte
    {
        Shot,
        Feed,
    }

    // What a client's shot struck, so the host can find its own copy.
    public enum NetEntity : byte
    {
        None,
        Player,
        Target,
        Body,
        Item,
    }

    public static class NetProtocol
    {
        public const int DefaultPort = 27015;
        // Fits a full snapshot of a busy session; LAN links fragment and reassemble larger datagrams.
        public const int MaxPacket = 16384;
        // "FBS2": filters stray datagrams and peers running another protocol version.
        public const int Magic = 0x32534246;
        public const float TimeoutSeconds = 5f;
        // Host and client each measure the round trip this often, from a Ping the other echoes as a Pong.
        public const float PingInterval = 1f;
        // Each input packet repeats this many recent ticks, so a lost packet costs no input.
        public const int InputRedundancy = 8;
        // Remote entities render this far behind the newest snapshot. One tick leaves no margin: a
        // late snapshot holds the newest pose until the next arrives.
        public const float InterpolationDelayTicks = 1f;
        // Events ride along in every snapshot for this long, so one lost snapshot loses none.
        public const int EventLifetimeTicks = 20;
        public const int ItemsPerPlayer = 16;
    }
}

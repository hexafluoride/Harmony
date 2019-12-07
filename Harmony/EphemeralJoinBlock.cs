using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;

namespace Harmony
{
    [MessagePackObject(keyAsPropertyName: true)]
    public class EphemeralJoinBlock : IMessage
    {
        public string Name { get; set; }

        public string Software { get; set; }
        public string Version { get; set; }

        public List<string> Capabilities { get; set; }

        public bool HasStorageLimits { get; set; }
        public long MaxStorage { get; set; }

        public bool AcceptsLongTermStorage { get; set; }
        public long MaxPieceSize { get; set; }
    }
}

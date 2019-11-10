using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

using Chordette;

namespace Harmony
{
    public class HarmonyNetwork : Network
    {
        public new HarmonyNode Self { get; set; }

        public HarmonyNetwork(Node self) : base(self, HashSingleton.Hash.HashSize)
        {
        }

        public new INode Connect(byte[] id)
        {
            var join_block = JoinBlock.FromID(id);
            var endpoint = new IPEndPoint(join_block.Address, join_block.Port);
            var node = Self.Connect(endpoint);

            node.DisconnectEvent += HandleNodeDisconnect;

            Add(node);

            return node;
        }
    }
}

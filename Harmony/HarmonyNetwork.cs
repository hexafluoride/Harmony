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
            Self = self as HarmonyNode;
        }

        internal new void Add(INode node) => base.Add(node);

        public override INode Connect(byte[] id)
        {
            var join_block = JoinBlock.FromID(id);

            if (join_block == null)
                return null;

            var endpoint = new IPEndPoint(join_block.Address, join_block.Port);
            var node = Self.Connect(endpoint) as HarmonyRemoteNode;

            if (node == null)
            {
                MarkUnreachable(id);
                return null;
            }

            node.DisconnectEvent += HandleNodeDisconnect;

            Add(node);

            return node;
        }
    }
}

using MessagePack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Harmony
{
    // JoinBlock notes
    //
    // Since we calculate the ID of a particular node by hash(join_block), and 
    // since we want node IDs to be as resistant to optimization by an attacker 
    // as possible, join_block should be as rigid to change as possible. This
    // has multiple implications:
    //
    // * JoinBlock should only contain properties that are hard to modify
    //   + "hard to modify here" can mean multiple things, such as:
    //     * changing a particular property might be computationally expensive
    //     * changing a particular property might break crucial functionality
    //     * changing a particular property might lead to rejection from peers
    // * JoinBlock's on-wire representation, join_block, should be rigidly
    //   defined with no "slack" allowed by the de/serializer.
    // * JoinBlock should only contain information that is absolutely essential
    // * We might still wanna deliver other important information (but 
    //   information that might be subject to change) about the node to a peer.
    //   In this case, deliver them separately inside a construct that doesn't
    //   affect the node ID, such as EphemeralJoinBlock
    //
    // Considering all of this, it's logical at the moment to define join_block
    // as join_block = (ip || port), as those are the 2 properties that fit the
    // above criteria.
    //
    // Note that the serializer "slack" mentioned above (the ability to cheaply
    // change the on-wire representation of JoinBlock while keeping its 
    // contents identical, therefore cheaply changing the node ID) is not
    // addressed by Harmony at this point.

    [MessagePackObject(keyAsPropertyName: true)]
    public class JoinBlock : IMessage
    {
        public IPAddress Address { get; set; }
        public ushort Port { get; set; }

        public JoinBlock() { }

        public JoinBlock(IPEndPoint ep) :
            this(ep.Address, (ushort)ep.Port)
        {
        }

        public JoinBlock(IPAddress addr, ushort port)
        {
            Address = addr;
            Port = port;
        }

        public bool Verify(byte[] id)
        {
            return GenerateID().SequenceEqual(id);
        }

        public byte[] GenerateID()
        {
            var ID = HashSingleton.ComputeSerialized(this);

            // current Chordette peer ID coding:
            // 4 bytes IPv4 address
            // 2 bytes TCP listening port
            var offset = 0;

            Array.Copy(Address.GetAddressBytes(), 0, ID, offset, 4);
            Array.Copy(BitConverter.GetBytes(Port), 0, ID, offset + 4, 2);

            return ID;
        }

        public static JoinBlock FromID(byte[] id)
        {
            var offset = 0;

            var ip_bytes = id.Skip(offset).Take(4).ToArray();
            var port_bytes = id.Skip(offset + 4).Take(2).ToArray();

            var addr = new IPAddress(ip_bytes);
            var port = BitConverter.ToUInt16(port_bytes, 0);

            var block = new JoinBlock(addr, port);

            if (!block.Verify(id))
                return null;

            return block;
        }
    }
}

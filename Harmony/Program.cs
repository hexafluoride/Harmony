using System;
using System.Net;
using Chordette;

namespace Harmony
{
    class Program
    {
        static void Main(string[] args)
        {
            // initialize network parameters
            int m = 160;
            var node = new Node(IPAddress.Loopback, 30000 + Node.Random.Next(1000), m);
            node.Start();

            // join network
            // TODO: add logic for bootstrapping from a known bootstrap server or node

            // stop node
            foreach (var peer in node.Peers.Nodes)
                (peer.Value as RemoteNode).Disconnect(false);
        }
    }
}

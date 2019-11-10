using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Chordette;

namespace Harmony
{
    public class HarmonyNode : Node
    {
        public JoinBlock JoinBlock { get; set; }
        public EphemeralJoinBlock EphemeralJoinBlock { get; set; }

        public DataStore LocalDataStore { get; set; }

        public int StabilizeRate { get; set; }
        public bool Running { get; set; }
        private ManualResetEvent RunningSemaphore = new ManualResetEvent(false);

        public HarmonyNode(IPAddress listen_addr, int port, DataStore store = default) : base()
        {
            JoinBlock = new JoinBlock() { Address = listen_addr, Port = (ushort)port };
            ID = JoinBlock.GenerateID();

            LocalDataStore = LocalDataStore ?? new DataStore();

            Listener = new TcpListener(listen_addr, port);
            Peers = new HarmonyNetwork(this);

            Table = new FingerTable(HashSingleton.Hash.HashSize, this);
            Successor = Table[0].ID;

            StabilizeRate = StabilizeRate == default ? 3000 : StabilizeRate;
        }

        public new void Start()
        {
            base.Start();

            var stab_thread = new Thread((ThreadStart)StabilizerLoop);
            stab_thread.Start();

            RunningSemaphore.Set();
            Running = true;
        }

        public void Stop()
        {
            Listener.Stop();
            RunningSemaphore.Reset();
            Running = false;

            // stop node
            foreach (var peer in Peers.Nodes)
                (peer.Value as RemoteNode).Disconnect(false);
        }

        private void StabilizerLoop()
        {
            while (!RunningSemaphore.WaitOne()) ; // wait until we start running

            while (RunningSemaphore.WaitOne(0))
            {
                if (Peers.Nodes.Count > 1)
                {
                    Stabilize();
                    FixFingers();
                }

                Thread.Sleep(StabilizeRate);
            }
        }

        public new HarmonyRemoteNode Connect(IPEndPoint ep)
        {
            Log($"Connecting to {ep}...");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(ep);

            var remote_node = new HarmonyRemoteNode(this, socket);
            remote_node.Start();

            return remote_node;
        }

        internal (Piece, byte[]) RetrievePieceLocally(byte[] id)
        {
            int d = 5; // redundancy factor, ask d nodes before giving up

            // try to reach nodes that might have this piece
            // count from 0 to d (redundancy factor)
            for (int i = 0; i < d; i++)
            {
                var iterated_hash = HashSingleton.ComputeRounds(id, i);

                // we look locally for i = 0...d first as it's the cheapest
                if (LocalDataStore.ContainsKey(iterated_hash))
                    return (LocalDataStore[iterated_hash], LocalDataStore[iterated_hash].Data);
            }

            return (null, null);
        }

        public byte[] RetrievePiece(byte[] id)
        {
            int d = 5;

            // try to find the piece in our internal cache
            (_, byte[] locally_retrieved) = RetrievePieceLocally(id);

            if (locally_retrieved != null)
                return locally_retrieved;

            // by this point, we know that the data isn't cached locally
            for (int i = 0; i < d; i++)
            {
                var iterated_hash = HashSingleton.ComputeRounds(id, i);

                var successor = FindSuccessor(iterated_hash);
                if (successor == null || successor.Length == 0)
                    continue;

                var peer = Peers[successor] as HarmonyRemoteNode;
                var data = peer.Retrieve(iterated_hash);

                if (!HashSingleton.VerifyRounds(data, iterated_hash, i))
                    continue;

                var piece = new Piece(data, i);
                return (LocalDataStore[iterated_hash] = piece).Data;
            }

            // we failed
            return null;
        }

        public IEnumerable<byte[]> StorePiece(byte[] piece)
        {
            int d = 5; // redundancy factor, store d copies of this piece
            var base_id = HashSingleton.Compute(piece);

            for (uint i = 0; i < d; i++)
            {
                var iterated_hash = HashSingleton.ComputeRounds(base_id, i);

                // store locally first
                LocalDataStore.Store(piece, iterated_hash, i);

                var successor = FindSuccessor(iterated_hash);

                if (successor == null || successor.Length == 0 || successor.SequenceEqual(ID))
                    continue;
                
                var peer = Peers[successor] as HarmonyRemoteNode;
                var response = peer.Store(iterated_hash, piece);

                if (response != null && response.Key.SequenceEqual(iterated_hash))
                    yield return iterated_hash;
            }
        }
    }
}

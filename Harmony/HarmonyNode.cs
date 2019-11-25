using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using Chordette;

namespace Harmony
{
    public class HarmonyNode : Node
    {
        public new HarmonyNetwork Network { get => base.Network as HarmonyNetwork; set => base.Network = value; }
        
        public JoinBlock JoinBlock { get; set; }
        public EphemeralJoinBlock EphemeralJoinBlock { get; set; }

        public DataStore LocalDataStore { get; set; }

        public int StabilizeRate { get; set; }
        public bool Running { get; set; }
        private ManualResetEvent RunningSemaphore = new ManualResetEvent(false);

        public bool Stable => Network.IsReachable(Successor) && Network.IsReachable(Predecessor);

        public HarmonyNode(IPEndPoint listen_ep) :
            this(listen_ep.Address, listen_ep.Port)
        {
        }

        public HarmonyNode(IPAddress listen_addr, int port, DataStore store = default) : base()
        {
            JoinBlock = new JoinBlock(listen_addr, (ushort)port);
            ID = JoinBlock.GenerateID();

            LocalDataStore = LocalDataStore ?? new DataStore();

            Listener = new TcpListener(listen_addr, port);
            Network = new HarmonyNetwork(this);

            Table = new FingerTable(HashSingleton.Hash.HashSize, this);
            Successor = Table[0].ID;

            StabilizeRate = StabilizeRate == default ? 3000 : StabilizeRate;

            PredecessorChanged += OnPredecessorChanged;
        }

        public new void Start()
        {
            base.Start();

            var stab_thread = new Thread((ThreadStart)StabilizerLoop);
            stab_thread.Start();

            RunningSemaphore.Set();
            Running = true;
        }

        public void Shutdown()
        {
            var next_spot = (new BigInteger(ID, true) + 1).ToPaddedArray(ID.Length);
            var successor_id = FindSuccessor(next_spot) ?? Successor;

            if (successor_id == null || successor_id.Length == 0 || successor_id.SequenceEqual(ID))
            {
                // can't perform key handoff
                Log("early break 0");
            }
            else
            {
                // connect to successor
                var uncasted_successor = Network[successor_id];

                while (uncasted_successor == null || !(uncasted_successor is HarmonyRemoteNode))
                {
                    successor_id = FindSuccessor(successor_id); // keep going around the Chord circle

                    if (successor_id.SequenceEqual(ID)) // we've looped around and reached ourselves
                    {
                        Log("early break 1");
                        break;
                    }
                }

                if (uncasted_successor != null && uncasted_successor is HarmonyRemoteNode)
                {
                    var successor = uncasted_successor as HarmonyRemoteNode;
                    var handed_off = HandoffRange(successor).Count();

                    Log($"Handed off {handed_off} pieces out of {LocalDataStore.Pieces.Count} (missed {LocalDataStore.Pieces.Count - handed_off})");
                }
            }

            Stop();
        }

        private void OnPredecessorChanged(object sender, PredecessorChangedEventArgs e)
        {
            HandoffRange(e.NewPredecessor, e.PreviousPredecessor, e.NewPredecessor);
        }

        public void Stop()
        {
            Listener.Stop();
            RunningSemaphore.Reset();
            Running = false;

            // stop node
            foreach (var peer in Network.Nodes)
                if (peer.Value is RemoteNode)
                    (peer.Value as RemoteNode).Disconnect(false);
        }

        private IEnumerable<Piece> HandoffRange(byte[] target, byte[] start = default, byte[] end = default)
        {
            Log($"Asked to handoff key range {start.ToUsefulString(true)}:{end.ToUsefulString(true)} to node {target.ToUsefulString(true)}");
            var target_peer = Network[target];

            if (target_peer == null)
            {
                Log($"Can't handoff key range {start.ToUsefulString(true)}:{end.ToUsefulString(true)} to null node {target.ToUsefulString(true)}");
                return null;
            }

            if (!(target_peer is HarmonyRemoteNode))
            {
                Log($"Can't handoff key range {start.ToUsefulString(true)}:{end.ToUsefulString(true)} to {target.ToUsefulString(true)} of node type {target_peer.GetType()} (self={target.SequenceEqual(ID)})");
                return null;
            }

            return HandoffRange(target_peer as HarmonyRemoteNode, start, end).ToList(); // force execution, TODO: find a better way to do this
        }

        private IEnumerable<Piece> HandoffRange(HarmonyRemoteNode target, byte[] start = default, byte[] end = default)
        {
            start = start ?? new byte[KeySize];
            end = end ?? new byte[KeySize];

            if (target == null)
            {
                Log($"Can't handoff key range {start.ToUsefulString(true)}:{end.ToUsefulString(true)} to null node (probably ourselves)");
                yield break;
            }

            Log($"Handing key range {start.ToUsefulString(true)}:{end.ToUsefulString(true)} off to node {target.ID.ToUsefulString(true)}");

            foreach (var piece in LocalDataStore)
            {
                if (piece.ID.IsNotIn(start, end))
                    continue;
                
                var response = target.Store(piece, true);

                if (response != null && HashSingleton.VerifyRounds(piece.Data, response.Key, (int)piece.RedundancyIndex))
                {
                    Log($"Successfully handed off {piece.ID.ToUsefulString()}");
                    yield return piece;
                }
                else
                {
                    Log($"Failed to handoff {piece.ID.ToUsefulString()}");
                }
            }
        }

        protected override void ListenerLoop()
        {
            while (!RunningSemaphore.WaitOne()) ;

            while (Running)
            {
                Socket incoming_socket = default;

                try
                {
                    incoming_socket = Listener.AcceptSocket();

                    Log($"Incoming connection on {Listener.LocalEndpoint} from {incoming_socket.RemoteEndPoint}");
                    var remote_node = new HarmonyRemoteNode(this, incoming_socket);

                    remote_node.DisconnectEvent += HandleNodeDisconnection;
                    remote_node.Start();
                    Log($"Connected to {remote_node.ID.ToUsefulString()} on {incoming_socket.RemoteEndPoint}");

                    Network.Add(remote_node);
                }
                catch (Exception ex)
                {
                    Log($"{ex.GetType()} occurred while trying to accept connection: {ex.Message}");

                    try
                    {
                        incoming_socket.Close();
                        incoming_socket.Dispose();
                    }
                    catch { }
                }
            }
        }

        private void HandleNodeDisconnection(object sender, RemoteNodeDisconnectingEventArgs e)
        {
            var node = sender as HarmonyRemoteNode;

            if (e.LongTerm)
            {
                if (node.ID.SequenceEqual(Successor))
                {
                    Stabilize(); // immediately try to find a new successor
                    Notify(Successor); // notify our new successor of our existence
                }
                else if (node.ID.SequenceEqual(Predecessor))
                {
                    // TODO: find a new predecessor somehow
                }
            }
        }

        private void StabilizerLoop()
        {
            while (!RunningSemaphore.WaitOne()) ; // wait until we start running

            while (RunningSemaphore.WaitOne(0))
            {
                if (Network.PeerCount > 0)
                {
                    Stabilize();
                    FixFingers();
                }

                Thread.Sleep(StabilizeRate);
            }
        }

        internal void DropPiece(Piece piece)
        {
            LocalDataStore.Drop(piece.ID);
        }

        internal (Piece, byte[]) RetrievePieceLocally(byte[] id)
        {
            int d = 5; // redundancy factor, ask d nodes before giving up

            // try to reach nodes that might have this piece
            // count from 0 to d (redundancy factor)
            for (int i = 0; i < d; i++)
            {
                var iterated_hash = i == 0 ? id : HashSingleton.ComputeRounds(id, i);

                // we look locally for i = 0...d first as it's the cheapest
                if (LocalDataStore.ContainsKey(iterated_hash))
                    return (LocalDataStore[iterated_hash], LocalDataStore[iterated_hash].Data);
            }

            return (null, null);
        }

        public override RemoteNode Connect(IPEndPoint ep)
        {
            Log($"Connecting to {ep}...");

            try
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(ep);

                var remote_node = new HarmonyRemoteNode(this, socket);
                remote_node.Start();

                if (remote_node.Disconnected || remote_node.ID == null)
                    return null;

                return remote_node;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        public Piece RetrievePiece(byte[] id)
        {
            int d = 5;

            // try to find the piece in our internal cache
            (var piece, byte[] locally_retrieved) = RetrievePieceLocally(id);

            if (locally_retrieved != null)
                return piece;

            // by this point, we know that the data isn't cached locally
            for (int i = 0; i < d; i++)
            {
                var iterated_hash = i == 0 ? id : HashSingleton.ComputeRounds(id, i);

                try
                {
                    var successor = FindSuccessor(iterated_hash);
                    if (successor == null || successor.Length == 0 || successor.SequenceEqual(ID))
                        continue;

                    var peer = Network[successor] as HarmonyRemoteNode;
                    piece = peer.Retrieve(iterated_hash);

                    if (piece == null ||
                        !(HashSingleton.VerifyRounds(piece.Data, piece.ID, (int)piece.RedundancyIndex) && piece.OriginalID.SequenceEqual(id)))
                        continue;

                    return (LocalDataStore[iterated_hash] = piece);
                }
                catch (Exception ex) { Log($"Exception while retrieving piece: {ex}"); }
            }

            // we failed
            return null;
        }

        public IEnumerable<byte[]> StorePiece(byte[] piece)
        {
            LocalDataStore.Store(new Piece(piece, 1));
            int d = 5; // redundancy factor, store d copies of this piece

            for (uint i = 1; i < d; i++)
            {
                var iterated_hash = HashSingleton.ComputeRounds(piece, i);

                // store locally first

                var successor = FindSuccessor(iterated_hash);

                if (successor == null || successor.Length == 0 || successor.SequenceEqual(ID))
                    continue;
                
                var peer = Network[successor] as HarmonyRemoteNode;
                var response = peer?.Store(new Piece(piece, iterated_hash, i) { Source = ID });

                if (response != null && response.Key.SequenceEqual(iterated_hash))
                    yield return iterated_hash;
            }
        }
    }
}

﻿using System;
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

        public bool Running { get; set; }
        public bool Stable => Network.IsReachable(Successor) && Network.IsReachable(Predecessor);

        public bool PrintMetricOutput { get; set; }
        public int StabilizeRate { get; set; }

        public bool Locked => ActiveLocks.Any(l => !l.Value.Expired);
        public bool AcceptingPieces { get; set; }

        private bool ShuttingDown = false;
        private ManualResetEvent ShutdownSemaphore = new ManualResetEvent(true);
        private ManualResetEvent RunningSemaphore = new ManualResetEvent(false);
        private Dictionary<byte[], Lock> ActiveLocks = new Dictionary<byte[], Lock>(new StructuralEqualityComparer());
        private bool AcceptingLocks = true;

        public HarmonyNode(IPEndPoint listen_ep) :
            this(listen_ep.Address, listen_ep.Port)
        {
        }

        public HarmonyNode(IPAddress listen_addr, int port, DataStore store = default) : base()
        {
            JoinBlock = new JoinBlock(listen_addr, (ushort)port);
            EphemeralJoinBlock = new EphemeralJoinBlock()
            {
                Name = Program.Name,
                Software = "harmony"
            };

            ID = JoinBlock.GenerateID();

            LocalDataStore = LocalDataStore ?? new DataStore();
            AcceptingPieces = true;

            Listener = new TcpListener(listen_addr, port);
            Network = new HarmonyNetwork(this);
            KeySize = Network.KeySize;

            Table = new FingerTable(HashSingleton.Hash.HashSize, this);
            Successor = Table[0].ID;

            StabilizeRate = StabilizeRate == default ? 3000 : StabilizeRate;

            PredecessorChanged += OnPredecessorChanged;
        }

        public new void Start()
        {
            RunningSemaphore.Set();
            Running = true;

            base.Start();

            var stab_thread = new Thread((ThreadStart)StabilizerLoop);
            stab_thread.Start();
        }

        public void Shutdown()
        {
            lock (ShutdownSemaphore)
            {
                if (ShuttingDown)
                {
                    Log($"node-shutdown: Waiting for shutdown process already in motion");

                    ShutdownSemaphore.WaitOne();
                    return;
                }

                ShutdownSemaphore.Reset();
                ShuttingDown = true;
            }

            Log("node-shutdown: Node shutdown initiated");
            AcceptingLocks = false;

            if (Locked)
            {
                Log($"shutdown-lock-resolve: Waiting for {ActiveLocks.Count} locks...");
                WaitLocks();
                Log($"shutdown-lock-resolve: Unlocked.");
            }

            AcceptingPieces = false;

            try
            {
                Log($"handoff-start: {LocalDataStore.Pieces.Count} pieces to hand off");

                var next_spot = (new BigInteger(ID, true) + 1).ToPaddedArray(ID.Length);
                var successor_id = FindSuccessor(next_spot) ?? Successor;
            
                // connect to successor
                var uncasted_successor = Network[successor_id];
                Lock successor_lock = ((HarmonyRemoteNode)uncasted_successor)?.AcquireLock();

                if ((uncasted_successor == null || !(uncasted_successor is HarmonyRemoteNode)) || 
                    ((successor_lock = ((HarmonyRemoteNode)uncasted_successor)?.AcquireLock()) == null) || (successor_lock?.ID?.Length == 0))
                {
                    uncasted_successor = Network.Nodes.Values.ToList()
                        .OfType<HarmonyRemoteNode>()
                        .FirstOrDefault(n => n.Ping() && (successor_lock = n.AcquireLock())?.ID?.Length != 0);
                }

                if (uncasted_successor != null && uncasted_successor is HarmonyRemoteNode && successor_lock != null && successor_lock.ID.Length != 0)
                {
                    var successor = uncasted_successor as HarmonyRemoteNode;
                    var handed_off = HandoffRange(successor).Count();

                    Log($"handoff-result: Handed off {handed_off} pieces out of {LocalDataStore.Pieces.Count} (missed {LocalDataStore.Pieces.Count - handed_off})");

                    successor.ReleaseLock(successor_lock);
                }
                else
                {
                    Log($"handoff-result: Unable to find any peers. Cannot perform key handoff. {LocalDataStore.Pieces.Count} pieces lost.");
                }
            }
            catch (Exception ex)
            {
                Log($"handoff-result: {ex.GetType()} thrown: {ex.Message}");
            }

            Stop();
            ShutdownSemaphore.Set();

            Log($"node-shutdown: Shutdown complete.");
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

        private void WaitLocks()
        {
            // first get rid of any already-expired locks
            ActiveLocks.Values.Where(l => l.Expired).ToList().ForEach(ReleaseLock);

            // find last lock to expire
            var last_lock_expiry = ActiveLocks.Values.OrderByDescending(l => l.Expires).FirstOrDefault()?.Expires ?? DateTime.Now;

            // wait
            int wait_ms = (int)Math.Max(0, (DateTime.Now - last_lock_expiry).TotalMilliseconds);
            Thread.Sleep(wait_ms);
        }

        private bool CanAcquireLock(byte[] source)
        {
            // TODO: prevent nodes from locking us forever
            // right now this allows anyone to permanently lock us
            return AcceptingLocks;
        }

        internal Lock AcquireLock(byte[] source)
        {
            if (!CanAcquireLock(source))
            {
                Log($"acquire-lock: Refusing lock request from {source.ToUsefulString(true)}");
                return new Lock() { ID = new byte[0] };
            }

            Log($"acquire-lock: Accepted lock request from {source.ToUsefulString(true)}");
            var @lock = new Lock(source);
            ActiveLocks[@lock.ID] = @lock;
            return @lock;
        }

        internal void ReleaseLock(Lock @lock) => ReleaseLock(@lock.ID);
        internal void ReleaseLock(byte[] lock_id)
        {
            if (ActiveLocks.ContainsKey(lock_id))
                ActiveLocks.Remove(lock_id);
        }

        private void OnPredecessorChanged(object sender, PredecessorChangedEventArgs e)
        {
            HandoffRange(e.NewPredecessor, e.PreviousPredecessor, e.NewPredecessor);
        }

        private IEnumerable<Piece> HandoffRange(byte[] target, byte[] start = default, byte[] end = default)
        {
            Log($"handoff-range: Asked to handoff key range {start.ToUsefulString(true)}:{end.ToUsefulString(true)} to node {target.ToUsefulString(true)}");
            var target_peer = Network[target];

            if (target_peer == null)
            {
                Log($"handoff-range: Can't handoff key range {start.ToUsefulString(true)}:{end.ToUsefulString(true)} to null node {target.ToUsefulString(true)}");
                return null;
            }

            if (!(target_peer is HarmonyRemoteNode))
            {
                Log($"handoff-range: Can't handoff key range {start.ToUsefulString(true)}:{end.ToUsefulString(true)} to {target.ToUsefulString(true)} of node type {target_peer.GetType()} (self={target.SequenceEqual(ID)})");
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
                Log($"handoff-range: Can't handoff key range {start.ToUsefulString(true)}:{end.ToUsefulString(true)} to null node (probably ourselves)");
                yield break;
            }

            Log($"handoff-range: Handing key range {start.ToUsefulString(true)}:{end.ToUsefulString(true)} off to node {target.ID.ToUsefulString(true)}");

            foreach (var piece in LocalDataStore.ToList())
            {
                if (piece.ID.IsNotIn(start, end))
                    continue;

                var response = target.Store(piece, true);

                if (response != null && piece.ID.SequenceEqual(response?.Key))
                {
                    Log($"handoff-range: Successfully handed off {piece.ID.ToUsefulString()}");
                    yield return piece;
                }
                else
                {
                    Log($"handoff-range: Failed to handoff {piece.ID.ToUsefulString()}");
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

                    Log($"listener-loop: Incoming connection on {Listener.LocalEndpoint} from {incoming_socket.RemoteEndPoint}");
                    var remote_node = new HarmonyRemoteNode(this, incoming_socket);

                    remote_node.DisconnectEvent += HandleNodeDisconnection;
                    remote_node.Start();
                    Log($"listener-loop: Connected to {remote_node.ID.ToUsefulString()} on {incoming_socket.RemoteEndPoint}");

                    Network.Add(remote_node);
                }
                catch (Exception ex)
                {
                    Log($"listener-loop: {ex.GetType()} occurred while trying to accept connection: {ex.Message}");

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

            if (node == null || node.ID == null)
                return;

            if (e.LongTerm)
            {
                try
                {
                    if (node.ID.SequenceEqual(Successor))
                    {
                        Stabilize(); // immediately try to find a new successor
                        Network[Successor]?.NotifyForwards(ID); // notify our new successor of our existence
                        Network[Predecessor]?.NotifyBackwards(ID); // notify our new predecessor of our existence
                    }
                    else if (node.ID.SequenceEqual(Predecessor))
                    {
                        // TODO: find a new predecessor somehow
                    }
                }
                catch {  }
            }
        }

        private void RedistributePieces()
        {
            if (Stable && LocalDataStore.Any(p => p != null && p.MarkedForRedistribution))
            {
                try
                {
                    var pieces_to_redistrib = LocalDataStore.Where(p => p != null && p.MarkedForRedistribution).ToList();

                    Log($"stabilizer-redistributor: {pieces_to_redistrib.Count} pieces out of {LocalDataStore.Pieces.Count} marked for redistribution in local data store.");

                    var piece = pieces_to_redistrib.ShuffleIterator(Random).First();
                    var successor = FindSuccessor(piece.ID);

                    Log($"stabilizer-redistributor: picked piece {piece.ID.ToUsefulString(true)}");

                    if (successor != null && successor.Length != 0 && !successor.SequenceEqual(ID) &&
                        Network[successor] != null && Network[successor].Ping())
                    {
                        Log($"stabilizer-redistributor: found real successor {successor.ToUsefulString(true)} for piece {piece.ID.ToUsefulString(true)}");

                        var peer = Network[successor] as HarmonyRemoteNode;
                        var resp = peer.Store(piece);

                        if (resp != null && resp.Key?.SequenceEqual(piece.ID) == true)
                        {
                            Log($"stabilizer-redistributor: successfully redistributed piece {piece.ID.ToUsefulString(true)}");
                            piece.MarkedForRedistribution = false;
                            piece.OurResponsibility = false;
                        }
                        else
                        {
                            Log($"stabilizer-redistributor: failed to redistribute piece {piece.ID.ToUsefulString(true)}");
                        }
                    }
                    else if (successor?.SequenceEqual(ID) == true)
                    {
                        Log($"stabilizer-redistributor: piece {piece.ID.ToUsefulString(true)} already belongs to us, unmarking for redistribution");
                        piece.MarkedForRedistribution = false;
                    }
                }
                catch (Exception ex)
                {
                    Log($"stabilizer-redistributor: {ex.GetType()} thrown: {ex.Message}");
                }
            }
        }

        private void PurgePieces()
        {
            if (Stable && LocalDataStore.Any(p => p != null && !p.MarkedForRedistribution && !p.OurResponsibility))
            {
                try
                {
                    var pieces_to_purge = LocalDataStore.Where(p => p != null && !p.MarkedForRedistribution && !p.OurResponsibility).ToList();

                    Log($"stabilizer-purge: {pieces_to_purge.Count} pieces out of {LocalDataStore.Pieces.Count} marked for purge in local data store.");

                    foreach (var piece in pieces_to_purge)
                        LocalDataStore.Drop(piece.ID);

                    Log($"stabilizer-purge: {pieces_to_purge.Count} pieces purged, {LocalDataStore.Pieces.Count} pieces remain");
                }
                catch (Exception ex)
                {
                    Log($"stabilizer-purge: {ex.GetType()} thrown: {ex.Message}");
                }
            }
        }

        private void PrintMetrics()
        {
            if (PrintMetricOutput)
            {
                Log($"metrics: " +
                    $"stable: {Stable}, " +
                    $"peers: {Network.PeerCount}, " +
                    $"pieces: {LocalDataStore.Pieces.Count}, " +
                    $"pieces-to-redistribute: {LocalDataStore.Count(p => p != null && p.MarkedForRedistribution)}, " +
                    $"uptime: {(long)(DateTime.Now - HarmonyModule.Start).TotalMilliseconds}, " +
                    $"messages: {RemoteNode.SentMessages + RemoteNode.ReceivedMessages}");
            }
        }

        private void StabilizerLoop()
        {
            while (!RunningSemaphore.WaitOne()) ; // wait until we start running

            while (RunningSemaphore.WaitOne(0))
            {
                if (Network.PeerCount > 0)
                {
                    try { Stabilize(); } catch (Exception ex) { Log($"stabilize: {ex.GetType()} thrown: {ex.Message}"); }
                    try { FixFingers(); } catch (Exception ex) { Log($"fix-fingers: {ex.GetType()} thrown: {ex.Message}"); }

                    RedistributePieces();
                    PurgePieces();
                    PrintMetrics();
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

        public override RemoteNode CreateRemoteNode(IPEndPoint ep)
        {
            try
            {
                var id = new JoinBlock(ep).GenerateID();

                if (ep == ListenEndPoint || id.SequenceEqual(ID))
                    return null;

                if (Network.Nodes.OfType<RemoteNode>().Any(n => n.ID.SequenceEqual(ID)))
                    return Network.Nodes[id] as RemoteNode;

                Log($"create-remote-node: Connecting to {ep}...");

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

        public Piece RetrievePiece(byte[] id, bool skip_cache = false)
        {
            int d = 5;

            // try to find the piece in our internal cache
            (var piece, byte[] locally_retrieved) = RetrievePieceLocally(id);

            if (locally_retrieved != null && !skip_cache)
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
                catch (Exception ex) { Log($"retrieve-piece: Exception while retrieving piece: {ex}"); }
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

                if (response?.Key != null && response.Key.SequenceEqual(iterated_hash))
                    yield return iterated_hash;
            }
        }
    }
}

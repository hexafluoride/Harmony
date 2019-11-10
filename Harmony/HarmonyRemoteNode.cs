using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

using Chordette;
using MessagePack;

namespace Harmony
{
    public class HarmonyRemoteNode : RemoteNode
    {
        public JoinBlock PeerJoinBlock { get; set; }
        public EphemeralJoinBlock EphemeralPeerJoinBlock { get; set; }

        public new HarmonyNode SelfNode { get => (HarmonyNode)base.SelfNode; set => base.SelfNode = value; }

        public HarmonyRemoteNode(INode self_node, Socket socket) : base(self_node, socket)
        {
            AddMessageHandler("get_join_block", (s, e) => { Reply(e.RequestID, SelfNode.JoinBlock); });
            AddMessageHandler("get_ephemeral_join_block", (s, e) => { Reply(e.RequestID, SelfNode.EphemeralJoinBlock); });
            AddMessageHandler("get_piece", (s, e) =>
            {
                (_, var local_piece) = SelfNode.RetrievePieceLocally(e.Parameter);
                var piece_response = new PieceResponse(local_piece);

                Reply(e.RequestID, piece_response);
            });

            AddMessageHandler("store_piece", (s, e) =>
            {
                // TODO: filter store requests for local preference
                // i.e. don't store a piece if it would violate our storage
                // constraints

                var request = LZ4MessagePackSerializer.Deserialize<PieceStorageRequest>(e.Parameter);
                var iterated_hash = HashSingleton.ComputeRounds(request.Data, request.RedundancyIndex);

                if (!HashSingleton.Verify(request.Data, request.OriginalID))
                {
                    Reply(e.RequestID, new PieceStorageResponse(false));
                    return;
                }

                SelfNode.LocalDataStore.Store(new Piece(request.Data, request.RedundancyIndex));
                Reply(e.RequestID, new PieceStorageResponse(true, iterated_hash));
            });
        }

        public T Request<T>(string method, byte[] parameter = default) => LZ4MessagePackSerializer.Deserialize<T>(Request(method, parameter));
        public T Request<T, TParam>(string method, TParam parameter) => LZ4MessagePackSerializer.Deserialize<T>(Request(method, LZ4MessagePackSerializer.Serialize(parameter)));
        public T Request<T>(string method, object parameter) => LZ4MessagePackSerializer.Deserialize<T>(Request(method, LZ4MessagePackSerializer.Serialize(parameter)));

        public (byte[], T) RequestWithBinary<T>(string method, byte[] parameter = default)
        {
            var reply = Request(method, parameter);
            return (reply, LZ4MessagePackSerializer.Deserialize<T>(reply));
        }

        public void Invoke<T>(string method, T parameter) => Invoke(method, LZ4MessagePackSerializer.Serialize(parameter));
        public void Invoke(string method, object parameter) => Invoke(method, LZ4MessagePackSerializer.Serialize(parameter));

        public void Reply<T>(byte[] request_id, T parameter) => Reply(request_id, LZ4MessagePackSerializer.Serialize(parameter));
        public void Reply(byte[] request_id, object parameter) => Reply(request_id, LZ4MessagePackSerializer.Serialize(parameter));

        public new void Start()
        {
            base.Start();
            PeerJoinBlock = Request<JoinBlock>("get_join_block");
            EphemeralPeerJoinBlock = Request<EphemeralJoinBlock>("get_ephemeral_join_block");
        }

        public byte[] Retrieve(byte[] key)
        {
            var reply = Request<PieceResponse>("get_piece", key);

            if (!reply.Successful)
                return null;

            return reply.Data;
        }

        public PieceStorageResponse Store(byte[] key, byte[] data) => Request<PieceStorageResponse>("store_piece", data);
    }
}

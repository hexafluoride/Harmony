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
                (var local_piece, _) = SelfNode.RetrievePieceLocally(e.Parameter);
                var piece_response = PieceResponse.FromPiece(local_piece);

                Reply(e.RequestID, piece_response);
            });

            AddMessageHandler("store_piece", (s, e) =>
            {
                // TODO: filter store requests for local preference
                // i.e. don't store a piece if it would violate our storage
                // constraints

                var request = LZ4MessagePackSerializer.Deserialize<PieceStorageRequest>(e.Parameter);
                Log($"received piece storage request from {ID.ToUsefulString(true)} (piece_id={request.ID.ToUsefulString(true)}, d={request.RedundancyIndex})");

                var iterated_hash = HashSingleton.ComputeRounds(request.Data, request.RedundancyIndex);

                if (!HashSingleton.VerifyRounds(request.Data, request.ID, (int)request.RedundancyIndex))
                {
                    Log($"piece storage request from {ID.ToUsefulString(true)} (piece_id={request.ID.ToUsefulString(true)}, d={request.RedundancyIndex}) FAILED verification");
                    Reply(e.RequestID, new PieceStorageResponse(false));
                    return;
                }

                Log($"piece storage request from {ID.ToUsefulString(true)} (piece_id={request.ID.ToUsefulString(true)}, d={request.RedundancyIndex}) PASSED verification");

                SelfNode.LocalDataStore.Store(new Piece(request.Data, request.RedundancyIndex) { Source = ID });
                Reply(e.RequestID, new PieceStorageResponse(true, iterated_hash));
            });
        }

        public T Request<T>(string method, byte[] parameter = default) => DeserializeSoft<T>(base.Request(method, parameter));
        public T Request<T, TParam>(string method, TParam parameter) => DeserializeSoft<T>(base.Request(method, LZ4MessagePackSerializer.Serialize(parameter)));
        public T Request<T>(string method, object parameter) => DeserializeSoft<T>(base.Request(method, LZ4MessagePackSerializer.Serialize(parameter)));

        public (byte[], T) RequestWithBinary<T>(string method, byte[] parameter = default)
        {
            var reply = Request(method, parameter);
            return (reply, DeserializeSoft<T>(reply));
        }

        public void Invoke<T>(string method, T parameter) => base.Invoke(method, LZ4MessagePackSerializer.Serialize(parameter));
        public void Invoke(string method, object parameter) => base.Invoke(method, LZ4MessagePackSerializer.Serialize(parameter));

        public void Reply<T>(byte[] request_id, T parameter) => base.Reply(request_id, LZ4MessagePackSerializer.Serialize(parameter));
        public void Reply(byte[] request_id, object parameter) => base.Reply(request_id, LZ4MessagePackSerializer.Serialize(parameter));

        private T DeserializeSoft<T>(byte[] arr) => arr == null || arr.Length == 0 ? default : LZ4MessagePackSerializer.Deserialize<T>(arr);

        public new void Start()
        {
            base.Start();
            PeerJoinBlock = Request<JoinBlock>("get_join_block");
            EphemeralPeerJoinBlock = Request<EphemeralJoinBlock>("get_ephemeral_join_block");
        }

        public Piece Retrieve(byte[] key)
        {
            var reply = Request<PieceResponse>("get_piece", key);

            if (!reply.Successful)
                return null;

            return new Piece(reply.Data, reply.RedundancyIndex) { Source = ID };
        }

        public PieceStorageResponse Store(Piece piece, bool handoff = false) => Request<PieceStorageResponse>("store_piece", PieceStorageRequest.FromPiece(piece, handoff));
    }
}

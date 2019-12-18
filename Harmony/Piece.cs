using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;

namespace Harmony
{
    [MessagePackObject]
    public class Piece
    {
        [Key(0)]
        public byte[] ID { get; set; }

        [Key(1)]
        public uint RedundancyIndex { get; set; }

        [Key(2)]
        public byte[] OriginalID { get; set; }

        [Key(3)]
        public DateTime Created { get; set; }

        [Key(4)]
        public byte[] Metadata { get; set; }

        [Key(5)]
        public byte[] Data { get; set; }
        
        [IgnoreMember]
        public byte[] Source { get; set; }

        [IgnoreMember]
        public bool MarkedForRedistribution { get; set; }

        [IgnoreMember]
        public bool OurResponsibility { get; set; }

        public Piece() { }

        internal Piece(byte[] data, byte[] id, uint rounds)
        {
            Data = data;
            ID = id;
            RedundancyIndex = rounds;
            OriginalID = HashSingleton.Compute(Data);
            OurResponsibility = true;
        }

        public Piece(byte[] data, int rounds) :
            this(data, (uint)rounds)
        { }

        public Piece(byte[] data, uint rounds = 1) :
            this(data, HashSingleton.ComputeRounds(data, rounds), rounds)
        {
        }

        public static Piece FromStorageRequest(PieceStorageRequest req) =>
            new Piece(req.Data, req.RedundancyIndex);
    }

    [MessagePackObject]
    public class PieceResponse
    {
        [Key(0)]
        public bool Successful { get; set; }

        [Key(1)]
        public byte[] Data { get; set; }

        [Key(2)]
        public uint RedundancyIndex { get; set; }

        public PieceResponse() { }

        public PieceResponse(byte[] data)
        {
            Successful = data != null;
            Data = data;
        }

        public static PieceResponse FromPiece(Piece piece) =>
            new PieceResponse(piece?.Data) { RedundancyIndex = piece?.RedundancyIndex ?? 0 };
    }

    [MessagePackObject]
    public class PieceStorageRequest
    {
        [Key(0)]
        public byte[] ID { get; set; }

        [Key(1)]
        public uint RedundancyIndex { get; set; }

        [Key(2)]
        public byte[] Data { get; set; }

        [Key(3)]
        public bool HandoffRequest { get; set; }

        public PieceStorageRequest() { }

        public PieceStorageRequest(byte[] id, int rounds, byte[] data, bool handoff_request = false)
        {
            ID = id;
            RedundancyIndex = (uint)rounds;
            Data = data;

            HandoffRequest = handoff_request;
        }

        public static PieceStorageRequest FromPiece(Piece piece, bool handoff = false) =>
            new PieceStorageRequest(piece.ID, (int)piece.RedundancyIndex, piece.Data, handoff);
    }

    [MessagePackObject]
    public class PieceStorageResponse
    {
        [Key(0)]
        public byte[] Key { get; set; }

        public PieceStorageResponse() { }

        public PieceStorageResponse(bool successful, byte[] key = default)
        {
            Key = successful ? key : null;
        }
    }
}

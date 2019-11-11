using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;

namespace Harmony
{
    public class Piece
    {
        public byte[] OriginalID { get; set; }
        public uint RedundancyIndex { get; set; }
        public byte[] ID { get; set; }
        public byte[] Data { get; set; }

        internal Piece(byte[] data, byte[] id, uint rounds)
        {
            Data = data;
            ID = id;
            RedundancyIndex = rounds;
            OriginalID = HashSingleton.Compute(Data);
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
            new PieceResponse(piece.Data) { RedundancyIndex = piece.RedundancyIndex };
    }

    [MessagePackObject]
    public class PieceStorageRequest
    {
        [Key(0)]
        public byte[] OriginalID { get; set; }

        [Key(1)]
        public uint RedundancyIndex { get; set; }

        [Key(2)]
        public byte[] Data { get; set; }

        public PieceStorageRequest() { }

        public PieceStorageRequest(byte[] original_id, int rounds, byte[] data)
        {
            OriginalID = original_id;
            RedundancyIndex = (uint)rounds;
            Data = data;
        }

        public static PieceStorageRequest FromPiece(Piece piece) =>
            new PieceStorageRequest(piece.OriginalID, (int)piece.RedundancyIndex, piece.Data);
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

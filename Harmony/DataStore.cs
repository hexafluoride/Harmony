using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Chordette;

namespace Harmony
{
    public class DataStore : IDataStore
    {
        public Dictionary<byte[], Piece> Pieces = new Dictionary<byte[], Piece>(new StructuralEqualityComparer());
        public Piece this[byte[] key] { get => Pieces[key]; set => Pieces[key] = value; }
        public bool ContainsKey(byte[] key) => Pieces.ContainsKey(key);
        public void Store(Piece piece) => this[piece.ID] = piece;
        public void Store(byte[] data, byte[] id, uint rounds) => Store(new Piece(data, id, rounds));

        public void Drop(byte[] key) => Pieces.Remove(key);

        public DataStore() { }
    }
}

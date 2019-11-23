using System;
using System.Collections.Generic;
using System.Text;

namespace Harmony
{
    interface IDataStore : IEnumerable<Piece>
    {
        Piece this[byte[] key] { get; set; }
        void Store(Piece piece);
        bool ContainsKey(byte[] key);
        void Drop(byte[] key);
    }
}

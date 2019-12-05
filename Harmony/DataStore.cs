using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Chordette;
using MessagePack;
using NLog;

namespace Harmony
{
    public class DataStore : IDataStore
    {
        public Logger Log = LogManager.GetCurrentClassLogger();

        public string CachePath = Path.Combine(Path.GetTempPath(), ".cache/harmony");

        public Dictionary<byte[], Piece> Pieces = new Dictionary<byte[], Piece>(new StructuralEqualityComparer());
        public Piece this[byte[] key] { get => Retrieve(key); set => Store(value); }
        
        public DataStore() { }

        public bool ContainsKey(byte[] key) => Pieces.ContainsKey(key) || File.Exists(GetPath(key, CachePath));
        
        public void Store(Piece piece)
        {
            lock (Pieces)
            {
                _StoreInternal(piece);
            }
        }

        public void StoreMany(IEnumerable<Piece> pieces)
        {
            lock (Pieces)
            {
                foreach (var piece in pieces)
                {
                    _StoreInternal(piece);
                }
            }
        }

        public void Drop(byte[] key) => Pieces.Remove(key);

        public Piece Retrieve(byte[] key)
        {
            lock (Pieces)
            {
                return _RetrieveInternal(key);
            }
        }

        private Piece _RetrieveInternal(byte[] key)
        {
            if (!Pieces.ContainsKey(key) || Pieces[key] == null)
            {
                var piece_file = GetPath(key, CachePath);

                if (!File.Exists(piece_file))
                    return null;

                Pieces[key] = FromFile(piece_file);
            }

            return Pieces[key];
        }
        
        private void _StoreInternal(Piece piece)
        {
            if (!HashSingleton.VerifyRounds(piece.Data, piece.ID, (int)piece.RedundancyIndex)) // just in case
            {
                Log.Warn($"Corrupt piece {piece.ID.ToUsefulString()} with d={piece.RedundancyIndex}");
                return;
            }

            if (ToFile(piece, GetPath(piece.ID, CachePath)))
                Pieces[piece.ID] = piece;
            else
                Log.Warn($"Couldn't commit piece {piece.ID.ToUsefulString()} to fs");
        }

        private bool ToFile(Piece piece, string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);

                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var fs = new FileStream(path, FileMode.OpenOrCreate))
                {
                    LZ4MessagePackSerializer.Serialize<Piece>(fs, piece);
                    return true;
                }
            }
            catch { return false; }
        }

        private Piece FromFile(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    var piece = LZ4MessagePackSerializer.Deserialize<Piece>(fs);

                    if (!HashSingleton.VerifyRounds(piece.Data, piece.ID, (int)piece.RedundancyIndex))
                        throw new Exception("Corrupt piece");

                    return piece;
                }
            }
            catch (Exception ex) { return null; }
        }

        public static string GetPath(byte[] key, string base_path = default)
        {
            int directories = 2;
            int dir_len = 2;

            base_path = base_path.TrimEnd('/', '\\');

            var whole = key.ToUsefulString();
            var path = new StringBuilder(base_path);

            int index = 0;
            int position = 0;

            for (; index <= directories && position < whole.Length; index++)
            {
                var forwards = Math.Min(whole.Length - position, dir_len);
                var fragment = whole.Substring(position, forwards);
                path.Append($"/{fragment}");

                position += forwards;
            }
            
            path.Append($"/{whole}.har");

            return path.ToString();
        }

        public IEnumerator<Piece> GetEnumerator()
        {
            return Pieces.Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}

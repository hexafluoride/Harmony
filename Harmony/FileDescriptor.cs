using Chordette;
using MessagePack;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Harmony
{
    [MessagePackObject]
    public class FileDescriptor
    {
        [Key(0)]
        public string Name { get; set; }

        [Key(1)]
        public long Length { get; set; }

        [Key(2)]
        public DateTime Created { get; set; }

        [Key(3)]
        public byte[] Checksum { get; set; }

        [Key(4)]
        public List<byte[]> PieceIdentifiers { get; set; }

        [Key(5)]
        public string MimeType { get; set; }

        public FileDescriptor()
        {
        }

        public byte[] Serialize() => LZ4MessagePackSerializer.Serialize(this);
        public static FileDescriptor Deserialize(byte[] blob) => LZ4MessagePackSerializer.Deserialize<FileDescriptor>(blob);

        public static (FileDescriptor, List<byte[]>) FromFile(string path, int chunk_size = 65000)
        {
            using (var fs = File.OpenRead(path)) { return FromFile(fs, Path.GetFileName(path), chunk_size); }
        }

        public static (FileDescriptor, List<byte[]>) FromFile(Stream file, string name, int chunk_size = 65000)
        {
            var pieces = new List<byte[]>();
            var hasher = HashSingleton.Duplicate();
            var descriptor = new FileDescriptor()
            {
                Name = name,
                Length = file.Length,
                Created = DateTime.UtcNow,
                Checksum = new byte[hasher.HashSize / 8],
                PieceIdentifiers = new List<byte[]>(),
                MimeType = MimeMapper.MapToMime(name)
        };

            byte[] buffer = new byte[chunk_size];

            while (file.Position < file.Length)
            {
                var read = file.Read(buffer, 0, chunk_size);
                var chunk = new byte[read];

                Array.Copy(buffer, 0, chunk, 0, read);

                if (file.Position == file.Length)
                    hasher.TransformFinalBlock(buffer, 0, read);
                else
                    hasher.TransformBlock(buffer, 0, read, buffer, 0);

                pieces.Add(chunk);
                descriptor.PieceIdentifiers.Add(HashSingleton.Compute(chunk));
            }
            
            descriptor.Checksum = hasher.Hash;

            return (descriptor, pieces);
        }
    }
}

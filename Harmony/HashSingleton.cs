using MessagePack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Harmony
{
    public static class HashSingleton
    {
        public static HashAlgorithm Hash = SHA256Managed.Create();

        public static byte[] ComputeSerialized(object obj) => Compute(LZ4MessagePackSerializer.Serialize(obj));
        public static byte[] Compute(byte[] data) => Hash.ComputeHash(data);

        public static byte[] ComputeRounds(byte[] data, uint rounds = 1) => ComputeRounds(data, (int)rounds);
        public static byte[] ComputeRounds(byte[] data, int rounds = 1)
        {
            if (rounds < 1)
                throw new InvalidOperationException($"Cannot hash a blob 0 or less times (requested rounds: {rounds})");

            for (int i = 0; i < rounds; i++)
                data = Compute(data);

            return data;
        }

        public static bool Verify(byte[] data, byte[] hash) => Compute(data).SequenceEqual(hash);
        public static bool VerifyRounds(byte[] data, byte[] hash, int rounds) => ComputeRounds(data, rounds).SequenceEqual(hash);

        public static HashAlgorithm Duplicate() => Activator.CreateInstance(Hash.GetType()) as HashAlgorithm;
    }
}

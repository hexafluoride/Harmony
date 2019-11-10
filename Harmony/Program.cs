using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using Chordette;
using MessagePack.Resolvers;

namespace Harmony
{
    class Program
    {
        static void Main(string[] args)
        {
            CompositeResolver.RegisterAndSetAsDefault(IPAddressResolver.Instance, StandardResolver.Instance);

            // initialize network parameters
            HashSingleton.Hash = SHA256.Create();
            var node = new HarmonyNode(IPAddress.Loopback, 30000 + Node.Random.Next(1000));
            node.Start();

            // join network
            // TODO: add logic for bootstrapping from a known bootstrap server or node

            // store some test pieces
            for (int i = 0; i < 10; i++)
            {
                var random_piece_data = new byte[512];
                Node.Random.NextBytes(random_piece_data);

                var stored_piece_ids = node.StorePiece(random_piece_data);

                foreach (var id in stored_piece_ids)
                {
                    (var stored_piece, _) = node.RetrievePieceLocally(id);
                    Console.WriteLine($"Stored {random_piece_data.Length}-byte " +
                        $"random block with original ID {stored_piece.OriginalID.ToUsefulString()} in " +
                        $"{stored_piece.ID.ToUsefulString()} with d={stored_piece.RedundancyIndex}");
                }

                Thread.Sleep(500);
            }

            Thread.Sleep(-1);
        }
    }
}

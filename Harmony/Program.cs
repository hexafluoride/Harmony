using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using Chordette;
using MessagePack.Resolvers;
using Mono.Options;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace Harmony
{
    class Program
    {
        static Logger Log = LogManager.GetCurrentClassLogger();

        static void Main(string[] args)
        {
            // initialize NLog
            var console_target = new ColoredConsoleTarget("console")
            {
                Layout = @"${date:format=HH\:mm\:ss.fff} [${level}] ${message} ${exception}"
            };

            var nlog_config = new LoggingConfiguration();
            nlog_config.AddTarget(console_target);
            nlog_config.AddRuleForAllLevels(console_target);

            LogManager.Configuration = nlog_config;
            Log.Info("Harmony starting.");

            // register IPAddressResolver for the serializer to work
            CompositeResolver.RegisterAndSetAsDefault(IPAddressResolver.Instance, StandardResolver.Instance);

            // create option set for command line argument parsing
            var bootstrap_list = new List<string>();
            var listen_arg = "";

            OptionSet set = null;
            set = new OptionSet()
            {
                {"b|bootstrap=", "A comma-separated list of Harmony IDs or IP endpoints. (can be mixed)", b => bootstrap_list.AddRange(b.Split(',')) },
                {"l|listen=", "Starts listening on the given IP endpoint. If only an integer is specified, treat the argument as 127.0.0.1:<port>.", l => listen_arg = l }
            };

            var cli_leftovers = set.Parse(args);

            // interpret command line arguments
            IPEndPoint listen_ep = new IPEndPoint(IPAddress.None, 0);
            
            if (ushort.TryParse(listen_arg, out ushort listen_port))
            {
                listen_ep = new IPEndPoint(IPAddress.Loopback, listen_port);
            }
            else if (Utilities.TryParseIPEndPoint(listen_arg, out IPEndPoint temp_ep))
            {
                listen_ep = temp_ep;
            }

            if (listen_ep.Address == IPAddress.None && listen_ep.Port == 0)
            {
                if (listen_arg.Any())
                    Log.Warn($"Invalid argument passed to --listen: \"{listen_arg}\" is not a parsable IP endpoint. Try something in the form of 127.0.0.1:30001.");

                listen_ep = new IPEndPoint(IPAddress.Loopback, 30000 + Node.Random.Next(1000));
            }

            Log.Info($"Listening on {listen_ep}");

            // initialize network parameters
            HashSingleton.Hash = SHA256.Create();
            var node = new HarmonyNode(listen_ep);
            node.Start();

            Log.Info($"Started node, our ID is {node.ID.ToUsefulString()}");

            // join network
            if (bootstrap_list.Any())
            {
                Log.Info($"Bootstrapping from {bootstrap_list.Count} sources...");

                foreach (var bootstrap_node in bootstrap_list)
                {
                    bool bootstrap_result = false;

                    if (bootstrap_node.Length == (HashSingleton.Hash.HashSize / 4)) // if bootstrap_node is an ID
                    {
                        var bootstrap_id = Utilities.ParseBytesFromString(bootstrap_node);
                        bootstrap_result = node.Join(bootstrap_id);
                    }
                    else if (Utilities.TryParseIPEndPoint(bootstrap_node, out IPEndPoint bootstrap_ep))
                    {
                        var join_block = new JoinBlock(bootstrap_ep);
                        bootstrap_result = node.Join(join_block.GenerateID());
                    }
                    else
                    {
                        Log.Warn($"Couldn't parse bootstrap node descriptor \"{bootstrap_node}\". Bootstrap descriptors can be in 2 forms: " +
                            $"IP end point (ex.: 127.0.0.1:30303) or node ID (abbv. ex.: 7f00...34fc)");
                    }

                    if (bootstrap_result)
                    {
                        Log.Info($"Successfully connected to {bootstrap_node}");
                    }
                    else
                        Log.Warn($"Failed to connect to {bootstrap_node}");
                }
            }
            else
            {
                Log.Warn($"No bootstrap nodes specified, we're alone. You can form an actual network by passing either " +
                    $"{node.ID.ToUsefulString()} or {listen_ep} to another Harmony instance.");
            }

            Console.ReadLine();

            List<byte[]> test_piece_keys = new List<byte[]>();

            // store some test pieces
            for (int i = 0; i < 3; i++)
            {
                var random_piece_data = new byte[512];
                Node.Random.NextBytes(random_piece_data);

                var key = HashSingleton.Compute(random_piece_data);

                test_piece_keys.Add(key);
                var stored_piece_ids = node.StorePiece(random_piece_data);

                foreach (var id in stored_piece_ids)
                {
                    //(var stored_piece, _) = node.RetrievePieceLocally(id);
                    Log.Debug($"Stored {random_piece_data.Length}-byte " +
                        $"random block with original ID {key.ToUsefulString()} in " +
                        $"{id.ToUsefulString()}");
                }

                node.LocalDataStore.Drop(key);

                Thread.Sleep(500);
            }

            Console.ReadLine();

            // retrieve test pieces
            foreach (var key in test_piece_keys)
            {
                var piece = node.RetrievePiece(key);

                if (piece == null || piece == default)
                {
                    Log.Warn($"Couldn't retrieve piece {key.ToUsefulString()}");
                }
                else
                    Log.Info($"Successfully retrieve piece {key.ToUsefulString()}");
            }

            Thread.Sleep(-1);
        }
    }
}

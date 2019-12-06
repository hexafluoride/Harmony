using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Chordette;

using MessagePack.Resolvers;

using Mono.Options;

using Nancy.Hosting.Self;

using NLog;
using NLog.Config;
using NLog.Targets;

namespace Harmony
{
    class Program
    {
        static Logger Log = LogManager.GetCurrentClassLogger();
        static HarmonyNode Node { get; set; }
        static IPEndPoint ListenEP { get; set; }
        static Random Random = new Random();

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
            Log.Debug($"args: [{string.Join(", ", args.Select(arg => $"\"{arg}\""))}]");

            // register IPAddressResolver for the serializer to work
            CompositeResolver.RegisterAndSetAsDefault(IPAddressResolver.Instance, StandardResolver.Instance);

            // create data store
            var data_store = new DataStore();

            // create option set for command line argument parsing
            var bootstrap_list = new List<string>();
            var listen_arg = "";
            var api_listen_arg = "";
            bool test_mode = false;
            bool daemon_mode = false;

            OptionSet set = null;
            set = new OptionSet()
            {
                {"b|bootstrap=", "A comma-separated list of Harmony IDs or IP endpoints. (can be mixed)", b => bootstrap_list.AddRange(b.Split(',')) },
                {"l|listen=", "Starts listening for Harmony connections on the given IP endpoint. " +
                    "If only an integer is specified, treats the argument as 127.0.0.1:<port>." +
                    "If only an address is specified, treats the argument as <addr>:<random_port>", l => listen_arg = l },
                {"api=", "Starts listening for HTTP requests on the given IP endpoint. Implements the Harmony REST API." +
                    "If only an integer is specified, treats the argument as 127.0.0.1:<port>." +
                    "If only an address is specified, treats the argument as <addr>:<random_port>", l => api_listen_arg = l },
                {"test", "Starts an interactive test session after boot.", t => test_mode = true },
                {"c|cache=", "Instructs Harmony to read cached pieces from the given cache directory.", c => data_store.CachePath = c },
                {"daemon", "Replaces stdin reads with indefinite waits, useful for when running as a daemon", d => daemon_mode = true }
            };

            var cli_leftovers = set.Parse(args);

            // interpret command line arguments
            IPEndPoint listen_ep = new IPEndPoint(IPAddress.None, 0);
            IPEndPoint api_listen_ep = new IPEndPoint(IPAddress.None, 0);
            
            if (ushort.TryParse(listen_arg, out ushort listen_port))
            {
                listen_ep = new IPEndPoint(IPAddress.Loopback, listen_port);
            }
            else if (IPAddress.TryParse(listen_arg, out IPAddress listen_addr))
            {
                listen_ep = new IPEndPoint(listen_addr, 30000 + Random.Next(1000));
            }
            else if (Utilities.TryParseIPEndPoint(listen_arg, out IPEndPoint temp_ep))
            {
                listen_ep = temp_ep;
            }

            if (ushort.TryParse(api_listen_arg, out ushort api_listen_port))
            {
                api_listen_ep = new IPEndPoint(IPAddress.Loopback, api_listen_port);
            }
            else if (IPAddress.TryParse(api_listen_arg, out IPAddress api_listen_addr))
            {
                api_listen_ep = new IPEndPoint(api_listen_addr, 8000 + Random.Next(1000));
            }
            else if (Utilities.TryParseIPEndPoint(api_listen_arg, out IPEndPoint temp_ep))
            {
                api_listen_ep = temp_ep;
            }

            if (listen_ep.Address == IPAddress.None && listen_ep.Port == 0)
            {
                if (listen_arg.Any())
                    Log.Warn($"Invalid argument passed to --listen: \"{listen_arg}\" is not a parsable IP endpoint. Try something in the form of 127.0.0.1:30001.");

                listen_ep = new IPEndPoint(IPAddress.Loopback, 30000 + Random.Next(1000));
            }

            ListenEP = listen_ep;
            Log.Info($"Listening for Harmony connections on {listen_ep}");

            // start HTTP server if needed
            if (api_listen_ep.Port != 0)
            {
                Log.Info($"Listening for HTTP requests on {api_listen_ep}");
                
                var host = new NancyHost(new Uri($"http://{api_listen_ep.Address}:{api_listen_ep.Port}/"));
                host.Start();

                Log.Info("Started API server");
            }

            // initialize network parameters
            HashSingleton.Hash = SHA256.Create();
            Node = new HarmonyNode(listen_ep);
            Node.LocalDataStore = data_store;

            HarmonyModule.Node = Node; // Nancy module for the HTTP API

            // configure title display
            Node.PredecessorChanged += (e, s) => { UpdateDisplay(); };
            Node.SuccessorChanged += (e, s) => { UpdateDisplay(); };
            Task.Run(() => { while (true) { UpdateDisplay(); Thread.Sleep(1000); } }).ConfigureAwait(false);

            // start node
            Node.Start();
            Log.Info($"Started node, our ID is {Node.ID.ToUsefulString()}");
            Log.Info($"Piece cache is located at {Node.LocalDataStore.CachePath}");

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
                        bootstrap_result = Node.Join(bootstrap_id);
                    }
                    else if (Utilities.TryParseIPEndPoint(bootstrap_node, out IPEndPoint bootstrap_ep))
                    {
                        var join_block = new JoinBlock(bootstrap_ep);
                        bootstrap_result = Node.Join(join_block.GenerateID());
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
                    $"{Node.ID.ToUsefulString()} or {listen_ep} to another Harmony instance.");

                Node.Join(default);
            }

            if (test_mode)
            {
                Console.ReadLine();

                List<byte[]> test_piece_keys = new List<byte[]>();

                // store some test pieces
                for (int i = 0; i < 3; i++)
                {
                    var random_piece_data = new byte[512];
                    Random.NextBytes(random_piece_data);

                    var key = HashSingleton.Compute(random_piece_data);

                    test_piece_keys.Add(key);
                    var stored_piece_ids = Node.StorePiece(random_piece_data);

                    foreach (var id in stored_piece_ids)
                    {
                        Log.Debug($"Stored {random_piece_data.Length}-byte " +
                            $"random block with original ID {key.ToUsefulString()} in " +
                            $"{id.ToUsefulString()}");
                    }

                    Node.LocalDataStore.Drop(key);

                    Thread.Sleep(500);
                }

                Console.ReadLine();

                // retrieve test pieces
                foreach (var key in test_piece_keys)
                {
                    var piece = Node.RetrievePiece(key);

                    if (piece == null || piece == default)
                    {
                        Log.Warn($"Couldn't retrieve piece {key.ToUsefulString()}");
                    }
                    else
                        Log.Info($"Successfully retrieved piece {key.ToUsefulString()}");
                }
            }

            if (daemon_mode)
                Thread.Sleep(-1);
            else
                Console.ReadLine();

            Node.Shutdown();

            Console.ReadLine();
        }

        static readonly Stopwatch stat_sw = Stopwatch.StartNew();

        static long last_message_count = 0;
        static long last_bytes = 0;
        static long last_stat_calc_time = 0; // milliseconds since message rate last calc.
        static long message_rate = 0;
        static long data_rate = 0;

        private static void UpdateDisplay()
        {
            lock (stat_sw)
            {
                try
                {
                    var actual_time = stat_sw.ElapsedMilliseconds - last_stat_calc_time; // time since last stat calculation, we shouldn't assume 1000 for accuracy
                    last_stat_calc_time = stat_sw.ElapsedMilliseconds;

                    var message_count = RemoteNode.SentMessages + RemoteNode.ReceivedMessages;
                    var delta_msg = message_count - last_message_count;
                    last_message_count = message_count;
                    message_rate = (long)(delta_msg / (actual_time / 1000d)); // correcting for if a heartbeat takes longer than the stat calc. cycle period

                    var delta_bytes = RemoteNode.SentBytes - last_bytes;
                    last_bytes = RemoteNode.SentBytes;
                    data_rate = (long)(delta_bytes / (actual_time / 1000d));

                    Console.Title = $"{ListenEP}, " +
                        $"{(Node.Stable ? "stable" : "not stable")}, " +
                        $"id: {Node.ID.ToUsefulString(true)}, " +
                        $"{Node.Network.PeerCount} connections, " +
                       $"{message_rate:N0} msg/s, " +
                       $"{data_rate:N0} byte/s, " +
                       $"predecessor: {Node.Predecessor.ToUsefulString(true)}, " +
                       $"successor: {Node.Successor.ToUsefulString(true)}, " +
                       $"keys in memory: {Node.LocalDataStore.Pieces.Count}";
                }
                catch
                { }
            }
        }
    }
}

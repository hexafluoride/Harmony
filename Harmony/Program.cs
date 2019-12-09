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
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Mono.Options;

using NLog;
using NLog.Config;
using NLog.Targets;

namespace Harmony
{
    class Program
    {
        static Logger Log = LogManager.GetCurrentClassLogger();
        public static HarmonyNode Node { get; set; }
        public static string Name { get; set; }

        static IPEndPoint ListenEP { get; set; }
        static Uri Tracker { get; set; }
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

            // register IPAddressResolver for the serializer to work
            CompositeResolver.RegisterAndSetAsDefault(IPAddressResolver.Instance, StandardResolver.Instance);

            // create data store
            var data_store = new DataStore();

            // create option set for command line argument parsing
            var bootstrap_list = new List<string>();
            var listen_arg = "";
            var api_listen_arg = "";
            var tracker_arg = "";
            bool test_mode = false;
            bool daemon_mode = false;
            var tracker_interval = 60;
            var max_peers = 8;

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
                {"daemon", "Replaces stdin reads with indefinite waits, useful for when running as a daemon", d => daemon_mode = true },
                {"t|tracker=", "Announces and asks for peers from a tracker Zgibe server.", t => tracker_arg = t },
                {"tracker-interval=", "Sets the announcement and node stability check interval in seconds.", i => tracker_interval = int.Parse(i) },
                {"n|name=", "Sets the node name, announced to other peers and added to the response headers of the HTTP API.", n => Name = n },
                {"m|max-peers=", "The maximum number of peers to connect to. Recommended to be set above 4.", m => max_peers = int.Parse(m) },
                {"?|h|help", "Shows this text.", h => 
                {
                    Console.WriteLine("usage: harmony [--listen [<IP>:]<port>] [--api [<IP>:]port] [--bootstrap ...]\n" +
                                      "               [--tracker <URI>] [--name <name>] [--tracker-interval <seconds>]\n" +
                                      "               [--cache <path>] [--max-peers <N>] [--test] [--help] [--daemon]\n");
                    Console.WriteLine();
                    
                    set.WriteOptionDescriptions(Console.Out);
                    Environment.Exit(0);
                } }
            };

            var cli_leftovers = set.Parse(args);

            Log.Info("Harmony starting.");
            Log.Debug($"args: [{string.Join(", ", args.Select(arg => $"\"{arg}\""))}]");

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

            if (!string.IsNullOrWhiteSpace(tracker_arg))
            {
                if (Uri.TryCreate(tracker_arg, UriKind.Absolute, out Uri tracker))
                {
                    Tracker = tracker;
                }
                else if (Uri.TryCreate($"http://{tracker_arg}/", UriKind.Absolute, out tracker))
                {
                    Tracker = tracker;
                }
                else
                {
                    Log.Warn($"Invalid argument passed to --tracker: \"{tracker_arg}\" is not a parsable HTTP URI. Try something in the form of http://tracker.example.com/.");
                }
            }

            ListenEP = listen_ep;
            Log.Info($"Listening for Harmony connections on {listen_ep}");

            // start HTTP server if needed
            if (api_listen_ep.Port != 0)
            {
                Log.Info($"Listening for HTTP requests on {api_listen_ep}");

                var host = new WebHostBuilder()
                    .UseUrls($"http://{api_listen_ep}/")
                    .UseKestrel()
                    .ConfigureLogging((logging) => { logging.ClearProviders(); })
                    .SuppressStatusMessages(true)
                    .UseStartup<Startup>()
                    .Build();

                host.RunAsync();

                Log.Info("Started API server");
            }

            // initialize network parameters
            HashSingleton.Hash = SHA256.Create();
            Node = new HarmonyNode(listen_ep);
            Node.Network.MaximumPeers = max_peers;
            Node.LocalDataStore = data_store;

            HarmonyModule.Node = Node; // Nancy module for the HTTP API

            // announce self to tracker if configured
            if (Tracker != default)
                Announcer.AnnounceTo(Tracker, Node);

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

            // configure SIGTERM handler to shutdown node/wait for in-progress shutdown
            System.Runtime.Loader.AssemblyLoadContext.Default.Unloading += ctx =>
            {
                Log.Info("Shutting down...");
                Node.Shutdown();
            };

            // keep checking stability and asking for nodes from tracker if configured
            if (Tracker != default && tracker_interval > 0)
            {
                Task.Run(() => 
                {
                    Log.Info("Starting tracker thread...");

                    while (true)
                    {
                        try
                        {
                            Announcer.AnnounceTo(Tracker, Node);

                            if (!Node.Stable || Node.Network.PeerCount < (Node.Network.MaximumPeers - 1))
                            {
                                var peers = Announcer.GetPeers(Tracker).Where(i => i != null && i.SequenceEqual(Node.ID) != true).ToArray();

                                if (!peers.Any())
                                {
                                    Log.Warn($"{Node.Network.PeerCount} connections, our tracker isn't giving us any peers.");
                                }
                                else
                                {
                                    Log.Info($"Received {peers.Length} peers from tracker");
                                    bool any_successful = false;

                                    if (!Node.Joined)
                                    {
                                        foreach (var peer in peers)
                                        {
                                            var join_succ = Node.Join(peer);

                                            if (join_succ)
                                            {
                                                any_successful = true;
                                                break;
                                            }
                                        }
                                    }

                                    if (Node.Joined)
                                    {
                                        foreach (var peer in peers)
                                        {
                                            any_successful = Node.Network.Connect(peer) != null || any_successful;
                                            Thread.Sleep(250);
                                        }
                                    }

                                    if (!any_successful)
                                        Log.Warn("Couldn't connect to any tracker-provided peers.");
                                    else
                                    {
                                        Node.BootstrapSelf();
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Exception in tracker thread: {ex}");
                        }

                        Thread.Sleep(tracker_interval * 1000);
                    }
                });
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

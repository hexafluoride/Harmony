using Harmony;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Mono.Options;
using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Zgibe
{
    class Program
    {
        static Logger Log = LogManager.GetCurrentClassLogger();
        public static Random Random = new Random();

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

            // register IPAddressResolver for the serializer to work
            CompositeResolver.RegisterAndSetAsDefault(IPAddressResolver.Instance, StandardResolver.Instance);

            LogManager.Configuration = nlog_config;
            Log.Info("Zgibe starting.");
            Log.Debug($"args: [{string.Join(", ", args.Select(arg => $"\"{arg}\""))}]");

            var listen_arg = "";
            var housekeeping_interval = 60;

            OptionSet set = null;
            set = new OptionSet()
            {
                {"l|listen=", "Starts listening for Harmony connections on the given IP endpoint.", l => listen_arg = l },
                {"h|housekeep=", "Tests a random peer to see if they're alive every n seconds.", h => housekeeping_interval = int.Parse(h) },
                {"no-housekeeping", "Doesn't purge dead peers.", h => housekeeping_interval = 0 }
            };

            var cli_leftovers = set.Parse(args);

            IPEndPoint listen_ep = new IPEndPoint(IPAddress.None, 0);

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

            if (listen_ep.Address == IPAddress.None && listen_ep.Port == 0)
            {
                if (listen_arg.Any())
                    Log.Warn($"Invalid argument passed to --listen: \"{listen_arg}\" is not a parsable IP endpoint. Try something in the form of 127.0.0.1:30001.");

                listen_ep = new IPEndPoint(IPAddress.Loopback, 30000 + Random.Next(1000));
            }

            if (housekeeping_interval > 0)
            {
                Log.Info($"Starting housekeeping thread to cycle every {housekeeping_interval}s");
                Task.Run(() =>
                {
                    while (true)
                    {
                        Thread.Sleep(housekeeping_interval * 1000);
                    }
                });
            }
            
            Log.Info($"Listening for Harmony clients on {listen_ep}");

            var host = new WebHostBuilder()
                .UseUrls($"http://{listen_ep}/")
                .UseKestrel()
                .ConfigureLogging((logging) => { logging.ClearProviders(); })
                .SuppressStatusMessages(true)
                .UseStartup<Startup>()
                .Build();

            host.Run();
        }

        public static void Housekeep()
        {
            var peers_snapshot = MainModule.Peers.ToArray();

            if (!peers_snapshot.Any())
                return;

            var random_peer = peers_snapshot[Random.Next(peers_snapshot.Length)];

            // TODO: actually check whether the peer is alive or not

            if (false)
            {
                MainModule.Peers.Remove(random_peer);
            }
        }
    }
}

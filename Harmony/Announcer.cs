using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Harmony
{
    public static class Announcer
    {
        public static Logger Log = LogManager.GetCurrentClassLogger();

        public static void AnnounceTo(Uri tracker, HarmonyNode node)
        {
            try
            {
                Log.Info($"Attempting to announce our presence to {tracker}...");
                var client = new WebClient();
                client.UploadString(new Uri(tracker, "/announce"), JsonConvert.SerializeObject(Announcement.Create(node)));
                Log.Info("Successfully announced ourselves to tracker");
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't announce ourselves to tracker {tracker}: {ex}");
            }
        }

        public static IEnumerable<byte[]> GetPeers(Uri tracker)
        {
            try
            {
                var client = new WebClient();
                var response = client.DownloadString(new Uri(tracker, "/bootstrap"));
                var response_parsed = JObject.Parse(response);

                if (!response_parsed.Value<bool>("success"))
                    throw new Exception("Bootstrap request unsuccessful");

                return (response_parsed["peers"] as JArray).Select(t => Utilities.ParseBytesFromString(t.Value<string>())).ToArray();
            }
            catch (Exception ex)
            {
                Log.Warn($"Bootstrap request to tracker failed: {ex}");
                return new byte[0][];
            }
        }
    }

    public class Announcement
    {
        [JsonConverter(typeof(IPAddressConverter))]
        public IPAddress Address { get; set; }
        public ushort Port { get; set; }
        public byte[] ID { get; set; }

        public Announcement()
        {

        }

        public static Announcement Create(HarmonyNode self) =>
            new Announcement() { Address = self.ListenEndPoint.Address, Port = (ushort)self.ListenEndPoint.Port, ID = self.ID };
    }
}

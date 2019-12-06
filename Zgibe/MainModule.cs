using Chordette;
using Harmony;
using Nancy;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Zgibe
{
    public class MainModule : NancyModule
    {
        public static DateTime Start = DateTime.Now;
        public static HashSet<byte[]> Peers = new HashSet<byte[]>(new StructuralEqualityComparer());
        public static Dictionary<byte[], DateTime> LastAnnounce = new Dictionary<byte[], DateTime>(new StructuralEqualityComparer());

        public Response CreateError(string message, HttpStatusCode code = HttpStatusCode.BadRequest) =>
            Response.AsJson(new { Success = false, Message = message }).WithStatusCode(code);

        public MainModule()
        {
            Get("/health", _ => 
            {
                return Response.AsJson(new
                {
                    Uptime = (long)(DateTime.Now - Start).TotalMilliseconds,
                    Peers = Peers.Count
                });
            });

            Post("/announce", _ => 
            {
                var request = Request.Body;

                if (request == null)
                    return CreateError("No request received");

                if (request.Length > 4096)
                    return CreateError("Announcement too large");

                try
                {
                    using (var ms = new MemoryStream())
                    {
                        request.CopyTo(ms);
                        var request_val = Encoding.UTF8.GetString(ms.ToArray());
                        var announcement = JsonConvert.DeserializeObject<Announcement>(request_val);
                        var join_block = new JoinBlock(announcement.Address, announcement.Port);

                        if (!join_block.Verify(announcement.ID))
                            throw new Exception();

                        var id = join_block.GenerateID();

                        bool known = Peers.Add(id);
                        LastAnnounce[id] = DateTime.UtcNow;

                        return Response.AsJson(new
                        {
                            Success = true,
                            Known = known
                        });
                    }
                }
                catch
                {
                    return CreateError("Error while announcing");
                }
            });

            Get("/bootstrap", _ => 
            {
                if (!int.TryParse(Request.Query["count"] ?? "4", out int count))
                    count = 4;

                count = Math.Min(Math.Max(1, count), 16);

                return Response.AsJson(new
                {
                    Success = true,
                    Peers = Peers.ShuffleIterator(Program.Random).Take(count).Select(id => id.ToUsefulString())
                });
            });
        }
    }
}

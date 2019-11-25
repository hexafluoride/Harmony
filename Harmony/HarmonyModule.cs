using Chordette;

using Nancy;
using Nancy.Conventions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using HttpStatusCode = Nancy.HttpStatusCode;

namespace Harmony
{
    public class HarmonyModule : NancyModule
    {
        public static DateTime Start = DateTime.Now;
        public static HarmonyNode Node { get; set; }
        public Response CreateError(string message, HttpStatusCode code = HttpStatusCode.BadRequest) =>
            Response.AsJson(new { Success = false, Message = message }).WithStatusCode(code);

        public HarmonyModule()
        {
            Get("/health", _ =>
            {
                if (Node == null)
                    return Response.AsJson(new { Running = false });

                return Response.AsJson(new
                {
                    Running = Node.Running,
                    Uptime = (long)(DateTime.Now - Start).TotalMilliseconds,
                    Stable = Node.Stable,
                    ListenEndPoint = Node.ListenEndPoint.ToString(),

                    ID = Node.ID.ToUsefulString(),
                    Successor = Node.Successor.ToUsefulString(),
                    Predecessor = Node.Predecessor.ToUsefulString(),

                    Connections = Node.Network.PeerCount,
                    CandidatePeers = Node.Network.GetCandidatePeers().Count(),
                    KeysInMemory = Node.LocalDataStore.Pieces.Count
                });
            });

            Get("/successor", _ =>
            {
                if (Node == null || !Node.Running)
                    return CreateError("Node is down", HttpStatusCode.ServiceUnavailable);

                var id_str = "";
                id_str = Request.Query["id"] ?? "";
                
                if (id_str.Length != Node.KeySize * 2 ||
                    !Utilities.TryParseBytesFromString(id_str, out byte[] id))
                    return CreateError("Invalid Chord key");

                var successor = Node.FindSuccessor(id);

                return Response.AsJson(new
                {
                    Success = successor != null && successor.Length == id.Length,
                    Result = successor.ToUsefulString()
                });
            });

            Get("/piece", _ =>
            {
                if (Node == null || !Node.Running)
                    return CreateError("Node is down", HttpStatusCode.ServiceUnavailable);

                var id_str = "";
                id_str = Request.Query["id"] ?? "";

                if (id_str.Length != Node.KeySize * 2 ||
                    !Utilities.TryParseBytesFromString(id_str, out byte[] piece_id))
                    return CreateError("Invalid piece ID");

                var piece = Node.RetrievePiece(piece_id);

                if (piece == null)
                    return CreateError("Couldn't retrieve piece");

                return Response.AsJson(new
                {
                    Success = piece != null,

                    Piece = new
                    {
                        ID = piece.OriginalID.ToUsefulString(),
                        d = piece.RedundancyIndex,
                        IteratedID = piece.ID.ToUsefulString(),
                        Contents = $"{piece.Data.ToUsefulString(true)} ({piece.Data.Length} bytes)"
                    }
                });
            });

            Get("/successor/{id}", _ =>
            {
                var id_str = ((string)(_.id));
                return Response.AsRedirect($"/successor?id={id_str}");
            });

            Get("/piece/{id}", _ =>
            {
                var id_str = ((string)(_.id));
                return Response.AsRedirect($"/piece?id={id_str}");
            });

            Get("/piece/create/", _ =>
            {
                if (Node == null || !Node.Running)
                    return CreateError("Node is down", HttpStatusCode.ServiceUnavailable);

                var possible_params = new[] { "data", "contents", "msg", "message", "text" };
                var contents = "";

                foreach (var param in possible_params)
                {
                    contents = Request.Query[param] ?? "";

                    if (contents != "")
                        break;
                }

                if (contents == "")
                    return CreateError("No piece contents specified");

                var pieces = Node.StorePiece(Encoding.UTF8.GetBytes(contents));

                return Response.AsJson(new
                {
                    Success = pieces.Any(),
                    Pieces = pieces.Select(id => id.ToUsefulString())
                    //Pieces = pieces.Select(piece_id => Node.RetrievePiece(piece_id))
                    //    .Where(piece => piece != null)
                    //    .Select(piece => new
                    //    {
                    //        ID = piece.OriginalID.ToUsefulString(),
                    //        d = piece.RedundancyIndex,
                    //        Contents = $"{piece.Data.ToUsefulString(true)} ({piece.Data.Length} bytes)"
                    //    })
                });
            });

            Get("/bootstrap", _ =>
            {
                if (Node == null || !Node.Running)
                    return CreateError("Node is down", HttpStatusCode.ServiceUnavailable);

                var added_peers = Node.Network.Take(5)
                    .Where(peer => peer is RemoteNode)
                    .Sum(peer => Node.AskForPeers(peer as RemoteNode));

                var connected_peers = Node.BootstrapSelf().ToList();

                return Response.AsJson(new
                {
                    Acquired = added_peers,
                    Connected = connected_peers.Count,
                    NewPeerCount = Node.Network.PeerCount
                });
            });

            Get("/connect", _ => 
            {
                var block = default(JoinBlock);

                var id = new byte[Node.KeySize];
                var ep = new IPEndPoint(IPAddress.None, 0);

                var id_str = "";
                id_str = Request.Query["id"] ?? "";

                if (block == default)
                {
                    if (!string.IsNullOrWhiteSpace(id_str))
                    {
                        if (id_str.Length != Node.KeySize * 2 || !Utilities.TryParseBytesFromString(id_str, out id))
                            return CreateError("Invalid node ID");
                        else
                            block = JoinBlock.FromID(id);
                    }
                }

                if (block == default)
                {
                    var ep_str = "";
                    ep_str = Request.Query["ep"] ?? "";

                    if (!string.IsNullOrWhiteSpace(ep_str))
                    {
                        if (!Utilities.TryParseIPEndPoint(ep_str, out ep))
                            return CreateError("Invalid endpoint");
                        else
                            block = new JoinBlock(ep);
                    }
                }

                if (block == default)
                    return CreateError("Either specify a Harmony ID via id= or an IP endpoint via ep=");

                var success = false;

                if (!Node.Stable)
                    success = Node.Join(block.GenerateID());
                else
                    success = Node.Network.Connect(block.GenerateID()) != null;

                return Response.AsJson(new { Success = success });
            });

            Get("/shutdown", _ =>
            {
                Task.Run(() =>
                {
                    try { Node?.Shutdown(); } catch { }
                    Task.Delay(1000);
                    Environment.Exit(0);
                });

                return Response.AsJson(new { Success = true, Message = "Initiated shutdown" });
            });
        }
    }

    public class Bootstrapper : DefaultNancyBootstrapper
    {
        protected override void ConfigureConventions(NancyConventions nancyConventions)
        {
            base.ConfigureConventions(nancyConventions);

            this.Conventions.AcceptHeaderCoercionConventions.Add((acceptHeaders, ctx) =>
            {
                return acceptHeaders.Concat(new List<Tuple<string, decimal>> { ("application/json", (decimal)30000).ToTuple() });
            });
        }
    }
}

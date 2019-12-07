using Chordette;

using Nancy;
using Nancy.Configuration;
using Nancy.Conventions;
using Nancy.Json;
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

                try
                {
                    return Response.AsJson(new
                    {
                        Running = Node.Running,
                        Uptime = (long)(DateTime.Now - Start).TotalMilliseconds,
                        Stable = Node.Stable,
                        ListenEndPoint = Node.ListenEndPoint.ToString(),

                        id = Node.ID.ToUsefulString(),
                        Successor = Node.Successor.ToUsefulString(),
                        Predecessor = Node.Predecessor.ToUsefulString(),

                        PeerCount = Node.Network.PeerCount,
                        Connections = Node.Network.Nodes.Values.OfType<RemoteNode>()
                            .Select(n => new { id = n.ID.ToUsefulString(), ep = ((IPEndPoint)n.Connection.RemoteEndPoint).ToString(), alive = n.Ping() }),
                        CandidatePeers = Node.Network.GetCandidatePeers().Count(),
                        KeysInMemory = Node.LocalDataStore.Pieces.Values
                            .Where(piece => piece != null)
                            .Select(piece => new
                            {
                                id = piece.OriginalID.ToUsefulString(),
                                d = piece.RedundancyIndex,
                                IteratedID = piece.ID.ToUsefulString(),
                                Contents = $"{piece.Data.ToUsefulString(true)} ({piece.Data.Length} bytes)",
                                ContentsText = piece.Data.Length > 50 ? "(too long)" : Encoding.UTF8.GetString(piece.Data),
                                Source = piece.Source.ToUsefulString()
                            })
                    });
                }
                catch (Exception ex)
                {
                    return CreateError($"{ex.GetType()} occurred while generating health page: {ex.Message}", HttpStatusCode.InternalServerError);
                }
            });

            Get("/file", _ =>
            {
                if (Node == null || !Node.Running)
                    return CreateError("Node is down", HttpStatusCode.ServiceUnavailable);

                var id_str = "";
                id_str = Request.Query["id"] ?? "";

                if (id_str.Length != Node.KeySize * 2 ||
                    !Utilities.TryParseBytesFromString(id_str, out byte[] descriptor_piece_id))
                    return CreateError("Invalid piece ID");

                var descriptor_piece = Node.RetrievePiece(descriptor_piece_id, true);
                var descriptor = FileDescriptor.Deserialize(descriptor_piece.Data);

                var pieces = descriptor.PieceIdentifiers.Select(piece_id => Node.RetrievePiece(piece_id));

                if (pieces.Any(p => p == null))
                    return CreateError($"Failed to retrieve {pieces.Count(p => p == null)} pieces out of {descriptor.PieceIdentifiers.Count}");

                var descriptor_mime_type = descriptor.MimeType;
                var whitelisted_mime_types = new[] {
                    "image/*",
                    "audio/*",
                    "video/*",
                    "font/*",
                    "model/*",
                    "text/plain",
                    "text/csv",
                    "application/pdf",
                    "application/octet-stream"
                };

                if (!whitelisted_mime_types.Any(allowed_type => MimeMapper.WildcardMatch(allowed_type, descriptor_mime_type)))
                    descriptor_mime_type = "application/octet-stream";

                return new Response()
                {
                    Contents = (str) => 
                    {
                        foreach (var piece in pieces)
                            str.Write(piece.Data, 0, piece.Data.Length);

                        str.Flush();
                    },
                    ContentType = descriptor_mime_type,
                    StatusCode = HttpStatusCode.OK
                };
            });

            Get("/upload", _ => Response.AsText("<form action=\"file\" method=\"post\" enctype=\"multipart/form-data\"><input type=\"file\" id=\"file\" name=\"file\"><input type=\"submit\"></form>", "text/html"));
            Post("/file", _ =>
            {
                var file = Request.Files.FirstOrDefault();

                if (file == null)
                    return CreateError("No file received");

                if (Node == null || !Node.Running)
                    return CreateError("Node is down", HttpStatusCode.ServiceUnavailable);

                (var descriptor, var pieces) = FileDescriptor.FromFile(file.Value, file.Name);
                var descriptor_blob = descriptor.Serialize();

                var pieces_to_be_stored = new[] { descriptor_blob }.Concat(pieces);
                var resulting_pieces = pieces_to_be_stored.ToDictionary(
                    piece => HashSingleton.Compute(piece).ToUsefulString(), 
                    piece => Node.StorePiece(piece).Select(id => id.ToUsefulString()));

                return Response.AsJson(new
                {
                    Success = resulting_pieces.All(piece_pair => piece_pair.Value.Any()),
                    FileID = HashSingleton.Compute(descriptor_blob).ToUsefulString(),
                    StoredPieces = resulting_pieces.Keys,
                    StoredPiecesWithDuplicates = resulting_pieces
                }).WithHeader("Location", $"/file?id={HashSingleton.Compute(descriptor_blob).ToUsefulString()}")
                .WithStatusCode(HttpStatusCode.Created);
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

                var piece = Node.RetrievePiece(piece_id, true);

                if (piece == null)
                    return CreateError("Couldn't retrieve piece");

                return Response.AsJson(new
                {
                    Success = piece != null,

                    Piece = new
                    {
                        id = piece.OriginalID.ToUsefulString(),
                        d = piece.RedundancyIndex,
                        IteratedID = piece.ID.ToUsefulString(),
                        Contents = $"{piece.Data.ToUsefulString(true)} ({piece.Data.Length} bytes)",
                        ContentsText = Encoding.UTF8.GetString(piece.Data),
                        Source = piece.Source.ToUsefulString()
                    }
                });
            });

            Get("/successor/{id}", _ =>
            {
                var id_str = ((string)(_.id));
                return Response.AsRedirect($"/successor?id={id_str}");
            });

            Get("/file/{id}", _ =>
            {
                var id_str = ((string)(_.id));
                return Response.AsRedirect($"/file?id={id_str}");
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
                    PieceID = pieces.FirstOrDefault().ToUsefulString(),
                    Copies = pieces.Select(id => id.ToUsefulString())
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

                var peer_id = block.GenerateID();

                if (peer_id.SequenceEqual(Node.ID))
                    return CreateError("Can't connect to ourselves");

                var success = false;

                if (!Node.Stable)
                    success = Node.Join(peer_id);
                else
                    success = Node.Network.Connect(peer_id) != null;

                return Response.AsJson(new
                {
                    Success = success,
                    PeerID = peer_id.ToUsefulString(),
                    SelfID = Node.ID.ToUsefulString()
                });
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

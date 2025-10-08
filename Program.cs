using System;
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethereum.Web3;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using System.Collections.Generic;
using System.Numerics; 

// DTO for the PanelEventAdded event (update parameter names/types if needed)
[Event("PanelEventAdded")]
public class PanelEventAddedDTO : IEventDTO
{
    [Parameter("string", "panelId", 1, false)]
    public string PanelId { get; set; }

    [Parameter("string", "eventType", 2, false)]
    public string EventType { get; set; }

    [Parameter("bytes32", "eventHash", 3, false)]
    public byte[] EventHash { get; set; }

    [Parameter("address", "validatedBy", 4, false)]
    public string ValidatedBy { get; set; }

    [Parameter("uint256", "timestamp", 5, false)]
    public BigInteger Timestamp { get; set; }
}

class Program
{
    static ConcurrentBag<WebSocket> clients = new ConcurrentBag<WebSocket>();

    // Mapping from panelId to latest eventType ("installed", "fault", etc.)
    static ConcurrentDictionary<string, string> panelStates = new ConcurrentDictionary<string, string>();

    static async Task StartWebSocketServer()
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Start();

        Console.WriteLine($"✅ WebSocket server listening on http://0.0.0.0:{port}/");

        while (true)
        {
            var context = await listener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                Console.WriteLine("🌐 Unity client connected.");
                clients.Add(wsContext.WebSocket);
            }
            else if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath == "/api/test")
            {
                var response = JsonSerializer.Serialize(new { status = "success", timestamp = DateTime.UtcNow });
                var message = Encoding.UTF8.GetBytes(response);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = message.Length;
                await context.Response.OutputStream.WriteAsync(message, 0, message.Length);
                context.Response.OutputStream.Close();
            }
            else if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath == "/api/visibility")
            {
                var visibilityJson = JsonSerializer.Serialize(panelStates);
                var message = Encoding.UTF8.GetBytes(visibilityJson);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = message.Length;
                await context.Response.OutputStream.WriteAsync(message, 0, message.Length);
                context.Response.OutputStream.Close();
            }
            else
            {
                var message = Encoding.UTF8.GetBytes("👋 DPPRegistryBackend is running!");
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = message.Length;
                await context.Response.OutputStream.WriteAsync(message, 0, message.Length);
                context.Response.OutputStream.Close();
            }
        }
    }

    static async Task StartBlockchainListener()
    {
        var infuraUrl = "https://sepolia.infura.io/v3/51bc36040f314e85bf103ff18c570993";
        var contractAddress = "0x59B649856d8c5Fb6991d30a345f0b923eA91a3f7";

        var web3 = new Web3(infuraUrl);

        // Replace this ABI with your full contract ABI if you call other functions
        // For just event listening, DTO is enough

        var eventHandler = web3.Eth.GetEvent<PanelEventAddedDTO>(contractAddress);

        Console.WriteLine("👂 Listening for PanelEventAdded events...");

        var lastBlock = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();

        while (true)
        {
            var currentBlock = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();

            if (currentBlock.Value > lastBlock.Value)
            {
                var filter = eventHandler.CreateFilterInput(
                    new BlockParameter(new HexBigInteger(lastBlock.Value + 1)),
                    new BlockParameter(new HexBigInteger(currentBlock.Value))
                );

                var logs = await eventHandler.GetAllChangesAsync(filter);

                foreach (var ev in logs)
                {
                    string panelId = ev.Event.PanelId;
                    string eventType = ev.Event.EventType;

                    Console.WriteLine($"[Blockchain] NEW PanelEventAdded: {panelId} - {eventType}");

                    // Update panel state
                    panelStates[panelId] = eventType;

                    // Notify any connected WebSocket clients (optional)
                    var json = JsonSerializer.Serialize(new { panelId, eventType });
                    var message = Encoding.UTF8.GetBytes(json);

                    foreach (var socket in clients)
                    {
                        if (socket.State == WebSocketState.Open)
                        {
                            await socket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                }

                lastBlock = currentBlock;
            }

            await Task.Delay(10000); // Poll every 10 seconds
        }
    }

    static async Task Main(string[] args)
    {
        await Task.WhenAll(StartWebSocketServer(), StartBlockchainListener());
    }
}



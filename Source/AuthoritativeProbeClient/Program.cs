using Multiplayer.Authoritative;
using Multiplayer.Authoritative.Transport;

if (args.Length < 1 || args.Length > 5 ||
    !int.TryParse(args[0], out var port) || port < 1 || port > 65535 ||
    (args.Length != 1 && args.Length != 5))
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project Source/AuthoritativeProbeClient -- PORT [move PAWN_ID X Z]\n" +
        "Without 'move', the client prints a read-only snapshot.\n" +
        "Credentials are entered interactively and never passed as CLI arguments.");
    return 2;
}

var move = args.Length == 5 && args[1] == "move";
if (args.Length == 5 && !move)
{
    Console.Error.WriteLine("Expected move PAWN_ID X Z");
    return 2;
}

long pawnId = 0;
int x = 0, z = 0;
if (move && (!long.TryParse(args[2], out pawnId) || pawnId <= 0 ||
             !int.TryParse(args[3], out x) || !int.TryParse(args[4], out z)))
{
    Console.Error.WriteLine("Invalid pawn ID or destination coordinates");
    return 2;
}

Console.Write("Paste one-time local credential, then press Enter (hidden): ");
var credential = ReadSecret();

try
{
    using var client = await LoopbackTransportClient.ConnectAsync(port, credential);
    Console.WriteLine("Connected. All state is read-only DTO data.");
    Display(client.Replica);
    if (!move) return 0;

    if (!client.Replica.Things.TryGetValue(pawnId, out var pawn))
    {
        Console.Error.WriteLine("Pawn not found in the server snapshot");
        return 3;
    }

    var command = new PlayerCommand(
        AuthoritativeProtocol.Version, Guid.NewGuid(), "local-probe",
        pawn.FactionId, pawn.DomainId, client.Replica.GameStateVersion,
        AuthoritativeCommandKind.MovePawn, pawn.Id, x, z, 0);
    var receipt = await client.SendCommandAsync(command);
    Console.WriteLine("Move receipt: " +
        (receipt.Accepted ? "Accepted" : "Rejected") + " / " + receipt.Reason);
    if (!receipt.Accepted) return 4;

    for (var i = 0; i < 40; i++)
    {
        await Task.Delay(250);
        await client.PollAsync();
        if (client.Replica.Things.TryGetValue(pawnId, out var latest) &&
            latest.X == x && latest.Z == z)
        {
            Console.WriteLine("Target position confirmed by server snapshot/deltas.");
            Display(client.Replica);
            return 0;
        }
    }
    Console.Error.WriteLine("Target cell not reached in the observation window.");
    Display(client.Replica);
    return 5;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Local probe failed: " + exception.GetType().Name +
                            " - " + exception.Message);
    return 1;
}

static string ReadSecret()
{
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (chars.Count != 0) chars.RemoveAt(chars.Count - 1);
        }
        else if (!char.IsControl(key.KeyChar))
            chars.Add(key.KeyChar);
    }
    Console.WriteLine();
    var result = new string(chars.ToArray());
    chars.Clear();
    return result;
}

static void Display(ClientStateReplica replica)
{
    Console.WriteLine("Network version: " + replica.Version +
                      ", game revision: " + replica.GameStateVersion);
    foreach (var thing in replica.Things.Values.OrderBy(t => t.Id).Take(50))
        Console.WriteLine("Pawn " + thing.Id + " (faction " + thing.FactionId +
            ", domain " + thing.DomainId + ") at (" + thing.X + ", " + thing.Z + ")");
    if (replica.Things.Count > 50)
        Console.WriteLine("... remaining " + (replica.Things.Count - 50) + " entities hidden");
}

using System;
using System.IO;
using System.IO.Pipes;
using Winland.Common;

// winlandctl: the control client. Sends one command line to the running winland-env over its named
// pipe and prints/returns the result. Usage: winlandctl <verb> [args...]   e.g. winlandctl workspace 1
//
// Exit codes: 0 = OK, 1 = env replied ERR (or no reply), 2 = bad usage, 3 = could not reach env.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: winlandctl <command> [args...]   e.g. winlandctl workspace 1");
    return 2;
}

string command = string.Join(' ', args);

try
{
    using var client = new NamedPipeClientStream(".", Ipc.PipeName, PipeDirection.InOut);
    client.Connect(2000);

    // Deliberately NOT wrapping the writer/reader in `using`: the server closes its end immediately
    // after replying, so flushing the writer on dispose would race that close and throw. AutoFlush has
    // already pushed the command; disposing the client (below) closes the handle.
    var writer = new StreamWriter(client) { AutoFlush = true };
    var reader = new StreamReader(client);

    writer.WriteLine(command);
    string? response = reader.ReadLine();

    if (response is null)
    {
        Console.Error.WriteLine("winlandctl: no response from winland-env");
        return 1;
    }

    if (response == Ipc.Ok)
    {
        return 0;
    }

    Console.Error.WriteLine($"winlandctl: {response}");
    return 1;
}
catch (TimeoutException)
{
    Console.Error.WriteLine("winlandctl: winland-env is not running (connect timed out)");
    return 3;
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine("winlandctl: access to the winland-env pipe was denied (different user?)");
    return 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"winlandctl: {ex.Message}");
    return 3;
}

namespace Winland.Common;

/// <summary>
/// Shared contract for the winland-env control channel. winlandctl (client) and winland-env (server)
/// both reference this. The protocol is line-based over a local named pipe: the client writes one
/// command line ("workspace 1"); the server replies with a single line — <see cref="Ok"/> on success,
/// or "<see cref="ErrPrefix"/> &lt;message&gt;" on failure.
/// </summary>
public static class Ipc
{
    /// <summary>Pipe the environment service listens on (local machine: \\.\pipe\winland-env).</summary>
    public const string PipeName = "winland-env";

    public const string Ok = "OK";
    public const string ErrPrefix = "ERR";
}

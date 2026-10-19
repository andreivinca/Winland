using System;
using System.Threading;
using Winland.Common;

namespace Winland.Indicator;

/// <summary>
/// Sends requests to Winland one at a time, in the order they were made, on a worker thread — so the
/// circles never wait on Winland, which takes a moment to put windows away and lay them out. A request
/// still waiting when a newer one arrives is dropped in its favour: for a workspace dragged back and forth
/// only where it ends up matters, and an older move arriving late would undo a newer one.
/// </summary>
internal sealed class LatestRequestSender
{
    private readonly Func<string, bool> _send;
    private readonly object _gate = new();
    private string? _waiting;
    private bool _sending;

    /// <param name="send">Sends one request; true when Winland carried it out. Must not throw.</param>
    public LatestRequestSender(Func<string, bool> send) => _send = send;

    /// <summary>Queue <paramref name="request"/>, replacing any request that is still waiting. Returns at once.</summary>
    public void Post(string request)
    {
        lock (_gate)
        {
            _waiting = request;
            if (_sending)
            {
                return; // the running worker picks it up next
            }

            _sending = true;
        }

        ThreadPool.QueueUserWorkItem(_ => SendWaiting());
    }

    private void SendWaiting()
    {
        while (true)
        {
            string request;
            lock (_gate)
            {
                if (_waiting == null)
                {
                    _sending = false;
                    return;
                }

                request = _waiting;
                _waiting = null;
            }

            if (!_send(request))
            {
                Log.Line($"indicator: '{request}' was not carried out (is winland running?)");
            }
        }
    }
}

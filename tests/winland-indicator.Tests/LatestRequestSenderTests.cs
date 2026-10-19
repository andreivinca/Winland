using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winland.Indicator.Tests;

[TestClass]
public sealed class LatestRequestSenderTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void Post_SendsTheRequest()
    {
        var sent = new BlockingCollection<string>();
        var sender = new LatestRequestSender(request => { sent.Add(request); return true; });

        sender.Post("a");

        Assert.IsTrue(sent.TryTake(out string? first, Patience));
        Assert.AreEqual("a", first);
    }

    [TestMethod]
    public void Post_WhileOneIsBeingSent_SendsOnlyTheNewestOfTheWaitingOnes_AfterIt()
    {
        using var inFlight = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var sent = new BlockingCollection<string>();
        var sender = new LatestRequestSender(request =>
        {
            sent.Add(request);
            if (request == "a")
            {
                inFlight.Set();
                finish.Wait(Patience); // "a" is still on its way while b and c arrive
            }

            return true;
        });

        sender.Post("a");
        Assert.IsTrue(inFlight.Wait(Patience));
        sender.Post("b");
        sender.Post("c");
        finish.Set();

        Assert.IsTrue(sent.TryTake(out string? first, Patience));
        Assert.IsTrue(sent.TryTake(out string? second, Patience));
        Assert.AreEqual("a", first);
        Assert.AreEqual("c", second);
        Assert.IsFalse(sent.TryTake(out _, TimeSpan.FromMilliseconds(200)), "b was replaced by c and must never be sent");
    }

    [TestMethod]
    public void Post_AfterAFailedRequest_KeepsSending()
    {
        var sent = new BlockingCollection<string>();
        var sender = new LatestRequestSender(request => { sent.Add(request); return false; });

        sender.Post("a");
        Assert.IsTrue(sent.TryTake(out _, Patience));
        sender.Post("b");

        Assert.IsTrue(sent.TryTake(out string? next, Patience));
        Assert.AreEqual("b", next);
    }
}

using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Observation;

[TestFixture]
public sealed class CaptureSessionStoreTests
{
    [Test]
    public void Start_ReturnsFreshSessionId()
    {
        var s = new CaptureSessionStore();
        var a = s.Start(1, "example.com");
        var b = s.Start(2, "example.com");
        Assert.That(a.SessionId, Is.Not.EqualTo(b.SessionId));
        Assert.That(a.SessionId, Does.Match("^[0-9a-fA-F-]{36}$"));
    }

    [Test]
    public void Start_LimitEnforced_1F_CaptureLimitExceeded()
    {
        var s = new CaptureSessionStore();
        for (int i = 0; i < CaptureSessionStore.MaxConcurrent; i++) s.Start(i, "");
        Assert.Throws<CaptureLimitException>(() => s.Start(999, ""));
    }

    [Test]
    public void Stop_MarksStoppedAt()
    {
        var clock = new FakeClock(1000);
        var s = new CaptureSessionStore(clock);
        var sess = s.Start(1, "example.com");
        clock.Advance(TimeSpan.FromSeconds(5));
        var stopped = s.Stop(sess.SessionId);
        Assert.That(stopped.StoppedAt, Is.EqualTo(1000 + 5000));
    }

    [Test]
    public void Get_UnknownSession_Throws_SESSION_NOT_FOUND()
    {
        var s = new CaptureSessionStore();
        Assert.Throws<SessionNotFoundException>(() => s.Get("no-such-session"));
    }

    [Test]
    public void IdleExpiry_TriggersSessionExpired()
    {
        var clock = new FakeClock(0);
        var s = new CaptureSessionStore(clock);
        var sess = s.Start(1, "");
        clock.Advance(TimeSpan.FromMilliseconds(CaptureSessionStore.IdleTtlMs + 1));
        Assert.Throws<SessionExpiredException>(() => s.Get(sess.SessionId));
    }

    [Test]
    public void MaxDuration_TriggersSessionExpired()
    {
        var clock = new FakeClock(0);
        var s = new CaptureSessionStore(clock);
        var sess = s.Start(1, "");
        clock.Advance(TimeSpan.FromMilliseconds(CaptureSessionStore.MaxCaptureDurationMs + 1));
        Assert.Throws<SessionExpiredException>(() => s.Get(sess.SessionId));
    }

    [Test]
    public void AppendRequest_RespectsPerSessionLimit()
    {
        var s = new CaptureSessionStore();
        var sess = s.Start(1, "");
        var added = 0;
        for (int i = 0; i < CaptureSessionStore.MaxRequestsPerSession + 5; i++)
        {
            if (s.AppendRequest(sess.SessionId, new CaptureSession.NetworkRequest { RequestId = i.ToString(), Url = "u", Method = "GET" }))
                added++;
        }
        Assert.That(added, Is.EqualTo(CaptureSessionStore.MaxRequestsPerSession));
    }
}

namespace Trading.Data;

// Pure no-message detector. A quiet market still produces transport heartbeats, so silence of any
// message (not of price change) inside the session means the socket is dead or half-open.
public static class FeedWatchdog
{
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(5);

    // Silence is measured from the later of the last message and the session open, so a feed connected
    // before the open is never judged on pre-open quiet. Outside [open, close) it never trips.
    public static bool IsStalled(DateTimeOffset now, DateTimeOffset lastMessageAtUtc, DateTimeOffset openUtc, DateTimeOffset closeUtc,
        TimeSpan threshold)
    {
        if (now < openUtc || now >= closeUtc) return false;
        var since = lastMessageAtUtc > openUtc ? lastMessageAtUtc : openUtc;
        return now - since > threshold;
    }
}

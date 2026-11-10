namespace Sims4ModSieve.Desktop;

internal enum CreditsSurprise { None, Clawd, Star, ChatGpt, DeepSeek, Message }

internal sealed class CreditsInteractionState
{
    private int confettiClicks;
    private int heartBurst;
    private DateTimeOffset? lastHeartClick;

    public CreditsSurprise ClickConfetti() => ++confettiClicks switch
    {
        5 => CreditsSurprise.Clawd,
        6 => CreditsSurprise.Star,
        7 => CreditsSurprise.ChatGpt,
        8 => CreditsSurprise.DeepSeek,
        20 => CreditsSurprise.Message,
        _ => CreditsSurprise.None
    };

    public int ClickHeart(DateTimeOffset now)
    {
        heartBurst = lastHeartClick is { } last && now - last <= TimeSpan.FromMilliseconds(450)
            ? Math.Min(3, heartBurst + 1) : 1;
        lastHeartClick = now;
        return heartBurst;
    }

    public void Reset()
    {
        confettiClicks = heartBurst = 0;
        lastHeartClick = null;
    }
}

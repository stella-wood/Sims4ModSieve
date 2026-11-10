namespace Sims4ModSieve.Desktop;

internal enum CreditsSurprise { None, Clawd, Star, ChatGpt, DeepSeek, Message }

internal sealed class CreditsInteractionState
{
    private int confettiClicks;
    private int consecutiveClicks;
    private double? lastClick;
    private double resumeAt;

    public bool CanClick(double now) => now >= resumeAt;

    public bool TryClickConfetti(double now, out CreditsSurprise surprise)
    {
        surprise = CreditsSurprise.None;
        if (!CanClick(now)) return false;
        if (lastClick is { } last && now - last >= 2) consecutiveClicks = 0;
        lastClick = now;
        if (++consecutiveClicks == 25) resumeAt = now + 2;
        surprise = ++confettiClicks switch
        {
            5 => CreditsSurprise.Clawd,
            6 => CreditsSurprise.Star,
            7 => CreditsSurprise.ChatGpt,
            8 => CreditsSurprise.DeepSeek,
            20 => CreditsSurprise.Message,
            _ => CreditsSurprise.None
        };
        return true;
    }

    public void Reset()
    {
        confettiClicks = consecutiveClicks = 0;
        lastClick = null;
        resumeAt = 0;
    }
}

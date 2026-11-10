namespace Sims4ModSieve.Desktop;

internal enum CreditsSurprise { None, Clawd, Star, ChatGpt, DeepSeek, Message }

internal sealed class CreditsInteractionState
{
    private int confettiClicks;

    public CreditsSurprise ClickConfetti() => ++confettiClicks switch
    {
        5 => CreditsSurprise.Clawd,
        6 => CreditsSurprise.Star,
        7 => CreditsSurprise.ChatGpt,
        8 => CreditsSurprise.DeepSeek,
        20 => CreditsSurprise.Message,
        _ => CreditsSurprise.None
    };

    public void Reset() => confettiClicks = 0;
}

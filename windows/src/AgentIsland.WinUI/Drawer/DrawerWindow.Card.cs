using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Shell;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace AgentIsland.WinUI.Drawer;

// The hover card: opening, refreshing and closing it, and what it says.
internal sealed partial class DrawerWindow
{
    /// Hovering a character opens a card pointing at it; null stacks every
    /// character's status in one card.
    private void OpenCard(Bot? only)
    {
        _cardClose.Stop();
        _cardClose.Interval = TimeSpan.FromMilliseconds(250);
        _cardBot = only;
        // What the queue held is on the card's list.
        foreach (var bot in _bots.Where(b => only is null || b == only)) bot.Speech.Clear();
        if (only is null) Hush();
        else only.Bubble?.Hide(animated: false);
        ShowCard();
    }

    private void RefreshCard()
    {
        if (_closed || !_card.IsOpen || _cardRefreshPending) return;
        _cardRefreshPending = true;
        if (!_queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _cardRefreshPending = false;
            if (!_closed && _card.IsOpen) ShowCard();
        })) _cardRefreshPending = false;
    }

    /// The card of the character the provider works under (Grok's for Cursor).
    private void RefreshCardFor(DisplayProvider provider)
    {
        if (_cardBot is null || _cardBot.Provider == provider.Character()) RefreshCard();
    }

    /// The sessions of a character's agents that wait on the user, as its style lists them.
    private static StatusWaitingList WaitingFor(Bot bot, CharacterTheme theme)
    {
        var waiting = ActivityMonitor.Shared.Waiting.Where(w => w.Tool.ToDisplayProvider().Character() == bot.Provider);
        return theme == CharacterTheme.Pixel ? StatusWaitingList.Of(waiting) : StatusWaitingList.ForClassic(waiting, DateTimeOffset.UtcNow);
    }

    private static StatusCard CardFor(Bot bot, CharacterTheme theme)
    {
        var card = LongText(StatusCards.For(bot.Provider, bot.Phase, bot.Detail, bot.Running) with
        {
            Phase = DrawerLooks.CardPhase(bot.Phase, false, theme),
            Waiting = WaitingFor(bot, theme),
        });
        return bot.Rest is { } rest ? card with { Phase = DrawerLooks.CardPhase(bot.Phase, true, theme), Detail = RestDetail(rest, bot.Provider) } : card;
    }

    /// AGENTISLAND_DEBUG_CARD_TEXT=long: the longest a card says, to check
    /// how its phrases flow: a long phase and detail, a few notes and a tip
    /// that can't be split.
    private static StatusCard LongText(StatusCard card) =>
        Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_CARD_TEXT") != "long" ? card : card with
        {
            Phase = DrawerLooks.PhaseLabel(AgentPhase.Approving),
            Detail = "Cursor · " + L10n.TrFormat("Reconnecting · attempt {0} of {1}", 3, 10),
            Note = new[]
            {
                new StatusPhrase(L10n.TrFormat("Quota updated {0}", Formatting.RelativeAgo(TimeSpan.FromMinutes(12), L10n.IsChinese)), Array.Empty<string>()),
                new StatusPhrase(L10n.TrFormat("{0} reset cards", "2"), new[] { "2" }),
                new StatusPhrase(L10n.TrFormat("Auto-switched to {0}", "dev.account+work@example.com"), Array.Empty<string>()),
                new StatusPhrase("Open Kimi Code once to sync its plan usage; the CLI's sign-in only lasts fifteen minutes", Array.Empty<string>()),
            },
        };

    private void ShowCard()
    {
        if (!_shown) return;
        var dark = Brand.IsDark();
        var pixel = _theme == CharacterTheme.Pixel;
        List<(StatusCard, uint)> sections;
        using (pixel ? L10n.English() : null)
        {
            sections = Ordered(_bots.Where(b => b.Visible && (_cardBot is null || b == _cardBot)))
                .Select(b => (CardFor(b, _theme), Brand.Readable(SwatchOf(b), dark)))
                .ToList();
        }
        var height = WindowHeightDip(_bots.Count(b => b.Visible));
        var anchor = _cardBot?.Bounds ?? new Rect(InnerInset, height / 2 - BotSize / 2, BotSize, BotSize);
        _card.Show(sections, pixel, DrawerStore.Shared.Side == DrawerSide.Right, anchor,
            CardArea(), _reducedMotion);
    }

    /// Where the card may go: the screen's work area, or with
    /// AGENTISLAND_DEBUG_WORK_HEIGHT=<DIP> only that much of it, to check the
    /// all-agents card's columns as on a shorter screen.
    private Rect CardArea()
    {
        var area = WorkAreaDip();
        if (double.TryParse(Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_WORK_HEIGHT"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var height) && height > 0 && height < area.Height)
        {
            area.Height = height;
        }
        return area;
    }

    /// Closes the card once the pointer is off every character; the region
    /// clips the window, so leaving one circle for the next exits it first.
    private void CloseCardUnlessHovered()
    {
        _cardClose.Stop();
        if (BotUnderCursor() is { } bot && _cardBot is not null)
        {
            if (bot != _cardBot) OpenCard(bot);
            return;
        }
        _card.Hide(animated: true);
        _cardBot = null;
    }

    private void OpenCardIfStillHovered()
    {
        _cardIntent.Stop();
        if (_pressed is null && !_cardHeldBack && BotUnderCursor() is { } bot) OpenCard(bot);
    }
}

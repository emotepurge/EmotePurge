using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// What step 3 of the bot pass books for one activated channel (#487 plan 2.5, 2.8, 2.5b): the suspension reason it
/// should carry, null for "proven editor, not suspended" (a resume when it was suspended). <see cref="EditorProven"/> says
/// whether the verdict rests on a positive editor read, the only one that may book <c>BotEditorCheckedAtUtc</c>.
/// </summary>
public readonly record struct WishBotChannelVerdict(string? SuspendedReason)
{
    /// <summary>The editor read proved the bot an editor with "manage emote sets": resume, and book the proof.</summary>
    public bool EditorProven => SuspendedReason is null;
}

/// <summary>
/// The pure rule of step 3 for one activated channel (#487 plan 2.5, 2.8; Revision 6a S6-2), in the order of the
/// preflight: not covered → <c>not_listed</c>; consent to another bot account → <c>bot_account_changed</c> (whatever the
/// read says for the new account, so it goes before <c>not_editor</c>); then the editor read: <c>editor</c> → resume,
/// <c>not_editor</c> → <c>not_editor</c>. An invitation (<c>invited</c>) counts as <c>not_editor</c> here; accepting it and
/// the reason <c>invitation_pending</c> belong to the invitation step (T8c). Without a usable read nothing is known about
/// the editor state and the channel stays as it is (null): no suspension because of a 7TV outage.
/// </summary>
public static class WishBotChannelReview
{
    /// <param name="covered">On the pilot list, or the activator is a global admin by id.</param>
    /// <param name="settings">The channel's settings row as read.</param>
    /// <param name="botSevenTvUserId">The bot account's current 7TV id.</param>
    /// <param name="editor">The editor read's verdict for the channel; null when the read is not available.</param>
    public static WishBotChannelVerdict? Decide(
        bool covered, ChannelWishSettings settings, string? botSevenTvUserId, BotAccountEditorEntry? editor)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!covered)
        {
            return new WishBotChannelVerdict(WishBotChannelReasons.NotListed);
        }

        if (!WishBotPreflight.ConsentApplies(settings, botSevenTvUserId))
        {
            return new WishBotChannelVerdict(WishBotChannelReasons.BotAccountChanged);
        }

        if (editor is not { } entry)
        {
            return null;
        }

        return entry.Status == BotAccountEditorStatus.Editor
            ? new WishBotChannelVerdict(null)
            : new WishBotChannelVerdict(WishBotChannelReasons.NotEditor);
    }
}

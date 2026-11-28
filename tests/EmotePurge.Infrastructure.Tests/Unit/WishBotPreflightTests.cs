using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The two preflights of the wish bot (#487 plan 2.5, revision 5a; Revision 6b K6b-3, 6c K6c-5; T8b), pure: every stage
// fails on its own, in the fixed order, and the first "no" wins.
public class WishBotPreflightTests
{
    private const string Bot = "01BOTACCOUNT";
    private const int Identity = 4;
    private const int Credential = 7;

    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------------------------ BotAccountPreflight

    [Fact]
    public void Account_EveryStageHolds_Passes() =>
        Assert.Equal(BotAccountPreflightOutcome.Pass, BotAccountPreflight.Check(Account(), Credential, tokenUsable: true, Now));

    [Theory]
    [InlineData("kill switch", BotAccountPreflightOutcome.KillSwitch)]
    [InlineData("paused", BotAccountPreflightOutcome.Paused)]
    [InlineData("invalid", BotAccountPreflightOutcome.TokenNotValid)]
    [InlineData("expired", BotAccountPreflightOutcome.TokenNotValid)]
    [InlineData("exp reached", BotAccountPreflightOutcome.TokenNotValid)]
    [InlineData("credential version", BotAccountPreflightOutcome.CredentialChanged)]
    [InlineData("token unusable", BotAccountPreflightOutcome.TokenUnusable)]
    public void Account_EachStageOnItsOwn_IsItsOutcome(string stage, BotAccountPreflightOutcome expected)
    {
        var account = stage switch
        {
            "kill switch" => Account() with { KillSwitch = true },
            "paused" => Account() with { PausedUntilUtc = Now.AddSeconds(1) },
            "invalid" => Account() with { Status = BotAccountStatus.Invalid },
            "expired" => Account() with { Status = BotAccountStatus.Expired },
            "exp reached" => Account() with { ExpiresAtUtc = Now },
            _ => Account(),
        };
        var opened = stage == "credential version" ? Credential - 1 : Credential;

        Assert.Equal(expected, BotAccountPreflight.Check(account, opened, tokenUsable: stage != "token unusable", Now));
    }

    // A pause that has run out is no pause.
    [Fact]
    public void Account_APauseThatEnded_Passes() =>
        Assert.Equal(
            BotAccountPreflightOutcome.Pass,
            BotAccountPreflight.Check(Account() with { PausedUntilUtc = Now }, Credential, tokenUsable: true, Now));

    // The order: with every stage failing, each removal of the first failure reveals the next one.
    [Fact]
    public void Account_TheFirstNoWins_InTheFixedOrder()
    {
        var account = Account() with
        {
            KillSwitch = true,
            PausedUntilUtc = Now.AddMinutes(1),
            Status = BotAccountStatus.Invalid,
            CredentialVersion = Credential + 1,
        };

        Assert.Equal(BotAccountPreflightOutcome.KillSwitch, BotAccountPreflight.Check(account, Credential, false, Now));
        account = account with { KillSwitch = false };
        Assert.Equal(BotAccountPreflightOutcome.Paused, BotAccountPreflight.Check(account, Credential, false, Now));
        account = account with { PausedUntilUtc = null };
        Assert.Equal(BotAccountPreflightOutcome.TokenNotValid, BotAccountPreflight.Check(account, Credential, false, Now));
        account = account with { Status = BotAccountStatus.Valid };
        Assert.Equal(BotAccountPreflightOutcome.CredentialChanged, BotAccountPreflight.Check(account, Credential, false, Now));
        account = account with { CredentialVersion = Credential };
        Assert.Equal(BotAccountPreflightOutcome.TokenUnusable, BotAccountPreflight.Check(account, Credential, false, Now));
    }

    // ------------------------------------------------------------------------------------ WishBotPreflight

    [Fact]
    public void Wish_EveryStageHolds_Passes() =>
        Assert.Equal(WishBotPreflightOutcome.Pass, Check());

    [Theory]
    [InlineData("switches", WishBotPreflightOutcome.SwitchedOff)]
    [InlineData("coverage", WishBotPreflightOutcome.NotCovered)]
    [InlineData("kill switch", WishBotPreflightOutcome.KillSwitch)]
    [InlineData("paused", WishBotPreflightOutcome.Paused)]
    [InlineData("token", WishBotPreflightOutcome.TokenNotValid)]
    [InlineData("credential version", WishBotPreflightOutcome.CredentialChanged)]
    [InlineData("token unusable", WishBotPreflightOutcome.TokenUnusable)]
    [InlineData("consent to another account", WishBotPreflightOutcome.BotAccountChanged)]
    [InlineData("no consent", WishBotPreflightOutcome.BotAccountChanged)]
    [InlineData("proof older than a minute", WishBotPreflightOutcome.EditorUnproven)]
    [InlineData("proof of another identity", WishBotPreflightOutcome.EditorUnproven)]
    [InlineData("never proven", WishBotPreflightOutcome.EditorUnproven)]
    [InlineData("suspended", WishBotPreflightOutcome.EditorUnproven)]
    public void Wish_EachStageOnItsOwn_IsItsOutcome(string stage, WishBotPreflightOutcome expected)
    {
        var settings = Settings();
        var account = Account();
        switch (stage)
        {
            case "kill switch": account = account with { KillSwitch = true }; break;
            case "paused": account = account with { PausedUntilUtc = Now.AddMinutes(1) }; break;
            case "token": account = account with { Status = BotAccountStatus.Expired }; break;
            case "credential version": account = account with { CredentialVersion = Credential + 1 }; break;
            case "consent to another account": settings.BotConsentSevenTvUserId = "01OTHERBOT"; break;
            case "no consent": settings.BotConsentSevenTvUserId = null; break;
            case "proof older than a minute": settings.BotEditorCheckedAtUtc = Now - WishBotPreflight.EditorProofFreshness - TimeSpan.FromSeconds(1); break;
            case "proof of another identity": settings.BotEditorCheckedIdentityVersion = Identity - 1; break;
            case "never proven": settings.BotEditorCheckedAtUtc = null; break;
            case "suspended": settings.BotSuspendedReason = WishBotChannelReasons.NotEditor; break;
        }

        Assert.Equal(
            expected,
            WishBotPreflight.Check(
                switchesOn: stage != "switches",
                covered: stage != "coverage",
                settings,
                account,
                Credential,
                tokenUsable: stage != "token unusable",
                Now));
    }

    // A proof exactly one minute old still counts.
    [Fact]
    public void Wish_AProofExactlyAMinuteOld_Passes()
    {
        var settings = Settings();
        settings.BotEditorCheckedAtUtc = Now - WishBotPreflight.EditorProofFreshness;
        Assert.Equal(WishBotPreflightOutcome.Pass, WishBotPreflight.Check(true, true, settings, Account(), Credential, true, Now));
    }

    // Plan 2.5: switches → coverage → account stages → consent → editor; the first "no" wins.
    [Fact]
    public void Wish_TheFirstNoWins_InTheFixedOrder()
    {
        var settings = Settings();
        settings.BotConsentSevenTvUserId = "01OTHERBOT";
        settings.BotEditorCheckedAtUtc = null;
        var account = Account() with { KillSwitch = true };

        Assert.Equal(WishBotPreflightOutcome.SwitchedOff, WishBotPreflight.Check(false, false, settings, account, Credential, true, Now));
        Assert.Equal(WishBotPreflightOutcome.NotCovered, WishBotPreflight.Check(true, false, settings, account, Credential, true, Now));
        Assert.Equal(WishBotPreflightOutcome.KillSwitch, WishBotPreflight.Check(true, true, settings, account, Credential, true, Now));
        account = account with { KillSwitch = false };
        Assert.Equal(WishBotPreflightOutcome.BotAccountChanged, WishBotPreflight.Check(true, true, settings, account, Credential, true, Now));
        settings.BotConsentSevenTvUserId = Bot;
        Assert.Equal(WishBotPreflightOutcome.EditorUnproven, WishBotPreflight.Check(true, true, settings, account, Credential, true, Now));
    }

    // Reads for a confirmation or a pre-read run the same chain without the editor stage.
    [Fact]
    public void Wish_ForARead_TheEditorStageIsLeftOut_TheRestStays()
    {
        var settings = Settings();
        settings.BotEditorCheckedAtUtc = null;
        Assert.Equal(
            WishBotPreflightOutcome.Pass,
            WishBotPreflight.Check(true, true, settings, Account(), Credential, true, Now, requireEditorProof: false));

        settings.BotConsentSevenTvUserId = "01OTHERBOT";
        Assert.Equal(
            WishBotPreflightOutcome.BotAccountChanged,
            WishBotPreflight.Check(true, true, settings, Account(), Credential, true, Now, requireEditorProof: false));
    }

    // Only coverage and consent name a reason of their own; every other "no" suspends nothing from the preflight.
    [Theory]
    [InlineData(WishBotPreflightOutcome.NotCovered, WishBotChannelReasons.NotListed)]
    [InlineData(WishBotPreflightOutcome.BotAccountChanged, WishBotChannelReasons.BotAccountChanged)]
    [InlineData(WishBotPreflightOutcome.Pass, null)]
    [InlineData(WishBotPreflightOutcome.SwitchedOff, null)]
    [InlineData(WishBotPreflightOutcome.KillSwitch, null)]
    [InlineData(WishBotPreflightOutcome.Paused, null)]
    [InlineData(WishBotPreflightOutcome.TokenNotValid, null)]
    [InlineData(WishBotPreflightOutcome.CredentialChanged, null)]
    [InlineData(WishBotPreflightOutcome.TokenUnusable, null)]
    [InlineData(WishBotPreflightOutcome.EditorUnproven, null)]
    public void SuspensionReasonOf_NamesOnlyTheChannelReasons(WishBotPreflightOutcome outcome, string? expected) =>
        Assert.Equal(expected, WishBotPreflight.SuspensionReasonOf(outcome));

    private static WishBotPreflightOutcome Check() =>
        WishBotPreflight.Check(true, true, Settings(), Account(), Credential, true, Now);

    private static BotAccountSnapshot Account() =>
        new(BotAccountStatus.Valid, Now.AddDays(30), Bot, KillSwitch: false, PausedUntilUtc: null, Credential, Identity);

    private static ChannelWishSettings Settings() => new()
    {
        ChannelId = "c1",
        BotModeEnabled = true,
        BotConsentSevenTvUserId = Bot,
        BotEditorCheckedAtUtc = Now.AddSeconds(-30),
        BotEditorCheckedIdentityVersion = Identity,
    };
}

using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The global admin allowlist is a pure configuration lookup — no container, no database, no Twitch
// call. Admins are matched on the immutable Twitch user id (a login can be released and re-registered
// after a rename); the login list survives only as a transitional fallback while no id is configured.
//
// The precedence assertions are the point: an environment variable can only ever set the scalar key,
// while a JSON array lands on indexed keys, so the scalar has to win over the indexed keys (see the
// 2026-08-01 review, WB-1).
public class GlobalAdminAllowlistTests
{
    [Fact]
    public void IsAdmin_ReadsTheIdArray_WhenNoScalarIsSet_AndIgnoresTheLogin()
    {
        var (allowlist, _) = Create(new()
        {
            ["Auth:AdminTwitchUserIds:0"] = "1001",
            ["Auth:AdminTwitchUserIds:1"] = "1002",
        });

        Assert.True(allowlist.IsAdmin(Principal("1001", "anything")));
        Assert.True(allowlist.IsAdmin(Principal("1002", "somebody-else")));
        Assert.False(allowlist.IsAdmin(Principal("1003", "anything")));
    }

    [Fact]
    public void IsAdmin_LetsTheScalarOverrideTheArray()
    {
        var (allowlist, _) = Create(new()
        {
            ["Auth:AdminTwitchUserIds:0"] = "1",
            ["Auth:AdminTwitchUserIds"] = "2,3",
        });

        Assert.False(allowlist.IsAdmin(Principal("1")));
        Assert.True(allowlist.IsAdmin(Principal("2")));
        Assert.True(allowlist.IsAdmin(Principal("3")));
    }

    [Theory]
    [InlineData("2, 3")]
    [InlineData(" 2 ,3,")]
    [InlineData("2,,3")]
    public void IsAdmin_TrimsAndSkipsEmptyEntries(string configured)
    {
        var (allowlist, _) = Create(new() { ["Auth:AdminTwitchUserIds"] = configured });

        Assert.True(allowlist.IsAdmin(Principal("2")));
        Assert.True(allowlist.IsAdmin(Principal("3")));
        Assert.False(allowlist.IsAdmin(Principal("")));
    }

    [Fact]
    public void IsAdmin_ComparesIdsOrdinally()
    {
        var (allowlist, _) = Create(new() { ["Auth:AdminTwitchUserIds"] = "42" });

        Assert.True(allowlist.IsAdmin(Principal("42")));
        Assert.False(allowlist.IsAdmin(Principal("042")));
    }

    [Fact]
    public void IsAdmin_LetsIdsDecideAlone_AndWarnsWithCountsOnly_WhenBothListsAreConfigured()
    {
        var (allowlist, logger) = Create(new()
        {
            ["Auth:AdminTwitchUserIds"] = "42",
            ["Auth:AdminTwitchLogins"] = "SecretAdminLogin,other",
        });

        // The login is on the (ignored) list but the id is foreign: no admin.
        Assert.False(allowlist.IsAdmin(Principal("999", "secretadminlogin")));
        Assert.True(allowlist.IsAdmin(Principal("42", "whoever")));

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Auth:AdminTwitchLogins is ignored because Auth:AdminTwitchUserIds is configured", warning.Message);
        Assert.Contains("1 id(s), 2 login(s)", warning.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("42") && !e.Message.Contains("1 id(s)"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("SecretAdminLogin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsAdmin_FallsBackToTheLogin_CaseInsensitively_AndWarnsLoginBased_WhenNoIdIsConfigured()
    {
        var (allowlist, logger) = Create(new() { ["Auth:AdminTwitchLogins"] = "HandOfBlood" });

        Assert.True(allowlist.IsAdmin(Principal("1", "handofblood")));
        Assert.False(allowlist.IsAdmin(Principal("1", "someoneelse")));

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("login-based (1 login(s)); migrate to Auth:AdminTwitchUserIds", warning.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("HandOfBlood", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsAdmin_FallsBackToTheArray_WhenTheScalarIsBlank(string configured)
    {
        // The production shape when ADMIN_TWITCH_USER_IDS is missing from the stack's environment:
        // compose still expands `Auth__AdminTwitchUserIds=${...:-}` to an empty string next to the
        // appsettings.json array. Forgetting the variable must degrade to the array, not lock
        // every admin out.
        var (allowlist, _) = Create(new()
        {
            ["Auth:AdminTwitchUserIds:0"] = "7",
            ["Auth:AdminTwitchUserIds"] = configured,
        });

        Assert.True(allowlist.IsAdmin(Principal("7")));
        Assert.False(allowlist.IsAdmin(Principal("8")));
    }

    [Fact]
    public void IsAdmin_GrantsNothing_AndStaysQuiet_WhenNothingIsConfigured()
    {
        var (allowlist, logger) = Create([]);

        Assert.False(allowlist.IsAdmin(Principal("1", "alice")));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    private static TwitchPrincipalInfo Principal(string id, string login = "login") => new(id, login, AccessToken: null);

    private static (GlobalAdminAllowlist Allowlist, RecordingLogger<GlobalAdminAllowlist> Logger) Create(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var logger = new RecordingLogger<GlobalAdminAllowlist>();
        return (new GlobalAdminAllowlist(configuration, logger), logger);
    }
}

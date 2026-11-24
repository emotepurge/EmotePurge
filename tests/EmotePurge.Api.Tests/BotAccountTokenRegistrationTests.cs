using System.Security.Cryptography;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.BotAccount;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The Api's side of the token trust boundary (spec E7, AK72, plan #487 T2): the Api container seals with the public key
/// and can resolve neither the opener nor the 7TV write client, whatever the configuration says; with the bot account
/// on, a missing or unusable public key stops the start with a message that names the key and never its value.
/// </summary>
public class BotAccountTokenRegistrationTests
{
    // Generated per run, never stored: the 3072-bit pair the tests configure, and a 2048-bit one for the size check.
    private static readonly Lazy<RSA> KeyPair = new(() => RSA.Create(3072));
    private static readonly Lazy<RSA> SmallKeyPair = new(() => RSA.Create(2048));

    private static string PublicKey => Convert.ToBase64String(KeyPair.Value.ExportSubjectPublicKeyInfo());
    private static string PrivateKey => Convert.ToBase64String(KeyPair.Value.ExportPkcs8PrivateKey());

    [Fact]
    public void Disabled_RegistersARefusingSealer_AndNoOpener()
    {
        using var factory = new ApiFactory();
        _ = factory.Server;

        var sealer = factory.Services.GetRequiredService<IBotAccountTokenSealer>();

        Assert.IsType<DisabledBotAccountTokenSealer>(sealer);
        Assert.Null(sealer.KeyFingerprint);
        Assert.Throws<InvalidOperationException>(() => sealer.Seal("a.b.c"));
        AssertNoReadingSide(factory.Services);
    }

    [Fact]
    public void Enabled_SealsWithThePublicKey_AndStillRegistersNoOpener()
    {
        // Even with the private key present in the Api's configuration (a misconfigured stack), the Api never reads it.
        using var root = new ApiFactory();
        using var factory = Enabled(root, publicKey: PublicKey, privateKey: PrivateKey);
        _ = factory.Server;

        var sealer = factory.Services.GetRequiredService<IBotAccountTokenSealer>();
        var sealedToken = sealer.Seal("header.payload.signature");

        Assert.IsType<RsaBotAccountTokenSealer>(sealer);
        Assert.Equal(BotAccountTokenKeyFingerprint.Of(KeyPair.Value), sealer.KeyFingerprint);
        using var opener = new RsaBotAccountTokenOpener(BotAccountTokenKeys.ImportPrivateKey(PrivateKey));
        Assert.Equal("header.payload.signature", opener.TryOpen(sealedToken));
        AssertNoReadingSide(factory.Services);
    }

    [Fact]
    public void Enabled_WithoutPublicKey_StopsTheStart_NamingTheKey()
    {
        using var root = new ApiFactory();
        using var factory = Enabled(root, publicKey: null);

        var message = StartupFailure(factory);

        Assert.Contains(BotAccountTokenKeys.PublicKeyName, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_WithOnlyThePrivateKey_StopsTheStart()
    {
        using var root = new ApiFactory();
        using var factory = Enabled(root, publicKey: null, privateKey: PrivateKey);

        var message = StartupFailure(factory);

        Assert.Contains(BotAccountTokenKeys.PublicKeyName, message, StringComparison.Ordinal);
        AssertContainsNoPartOf(PrivateKey, message);
    }

    [Fact]
    public void Enabled_WithThePrivateKeyAsPublicKey_StopsTheStart_WithoutTheValue()
    {
        using var root = new ApiFactory();
        using var factory = Enabled(root, publicKey: PrivateKey);

        var message = StartupFailure(factory);

        Assert.Contains(BotAccountTokenKeys.PublicKeyName, message, StringComparison.Ordinal);
        AssertContainsNoPartOf(PrivateKey, message);
    }

    [Fact]
    public void Enabled_WithA2048BitKey_StopsTheStart_WithoutTheValue()
    {
        var small = Convert.ToBase64String(SmallKeyPair.Value.ExportSubjectPublicKeyInfo());
        using var root = new ApiFactory();
        using var factory = Enabled(root, publicKey: small);

        var message = StartupFailure(factory);

        Assert.Contains(BotAccountTokenKeys.PublicKeyName, message, StringComparison.Ordinal);
        Assert.Contains("3072", message, StringComparison.Ordinal);
        AssertContainsNoPartOf(small, message);
    }

    private static WebApplicationFactory<Program> Enabled(ApiFactory root, string? publicKey, string? privateKey = null) =>
        root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("BotAccount:Enabled", "true");
            builder.UseSetting(BotAccountTokenKeys.PublicKeyName, publicKey ?? string.Empty);
            builder.UseSetting(BotAccountTokenKeys.PrivateKeyName, privateKey ?? string.Empty);
        });

    // Everything the failed start produced, inner exceptions and stack traces included.
    private static string StartupFailure(WebApplicationFactory<Program> factory)
    {
        var exception = Assert.ThrowsAny<Exception>(() => factory.Server);
        return exception.ToString();
    }

    private static void AssertNoReadingSide(IServiceProvider services)
    {
        Assert.Null(services.GetService<IBotAccountTokenOpener>());

        // The 7TV write client arrives with plan #487 T3 and is registered by the worker's entry point only. Looked up by
        // name so this check already holds the line before the type exists; T3 may replace it with typeof.
        var writeClient = typeof(IBotAccountTokenOpener).Assembly.GetTypes()
            .SingleOrDefault(type => type.IsInterface && type.Name == "ISevenTvWriteClient");
        if (writeClient is not null)
        {
            Assert.Null(services.GetService(writeClient));
        }
    }

    private static void AssertContainsNoPartOf(string value, string message)
    {
        // A stretch from the start, the middle and the end of the base64 value, long enough to be no accident.
        Assert.DoesNotContain(value[..24], message, StringComparison.Ordinal);
        Assert.DoesNotContain(value.Substring(value.Length / 2, 24), message, StringComparison.Ordinal);
        Assert.DoesNotContain(value[^24..], message, StringComparison.Ordinal);
    }
}

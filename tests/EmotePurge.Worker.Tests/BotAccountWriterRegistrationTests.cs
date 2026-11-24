using System.Security.Cryptography;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The worker's side of the token trust boundary (spec E7, plan #487 T2): it opens with the private key, never seals,
// and with the bot account on a missing or unusable private key stops the start without echoing the value.
public class BotAccountWriterRegistrationTests
{
    // Generated per run, never stored.
    private static readonly Lazy<RSA> KeyPair = new(() => RSA.Create(3072));

    private static string PublicKey => Convert.ToBase64String(KeyPair.Value.ExportSubjectPublicKeyInfo());
    private static string PrivateKey => Convert.ToBase64String(KeyPair.Value.ExportPkcs8PrivateKey());

    [Fact]
    public void Disabled_RegistersNeitherOpenerNorSealer()
    {
        var services = WorkerServices(Configuration(enabled: "false", privateKey: PrivateKey));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IBotAccountTokenOpener));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IBotAccountTokenSealer));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISevenTvWriteClient));
    }

    [Fact]
    public void Enabled_RegistersTheWriteClient()
    {
        // Checked by descriptor: resolving it would build the Redis-backed telemetry and dial Redis.
        var services = WorkerServices(Configuration(enabled: "true", privateKey: PrivateKey));

        Assert.Contains(services, d => d.ServiceType == typeof(ISevenTvWriteClient));
    }

    [Fact]
    public void Enabled_OpensWithThePrivateKey_AndRegistersNoSealer()
    {
        var services = WorkerServices(Configuration(enabled: "true", privateKey: PrivateKey, publicKey: PublicKey));
        using var provider = services.BuildServiceProvider();
        using var sealer = new RsaBotAccountTokenSealer(BotAccountTokenKeys.ImportPublicKey(PublicKey));

        var opener = provider.GetRequiredService<IBotAccountTokenOpener>();

        Assert.Equal("header.payload.signature", opener.TryOpen(sealer.Seal("header.payload.signature")));
        Assert.Null(provider.GetService<IBotAccountTokenSealer>());
    }

    [Fact]
    public void Enabled_KeepsTheSixteenHostedServices()
    {
        var configuration = Configuration(enabled: "true", privateKey: PrivateKey);
        var services = WorkerServices(configuration);
        services.AddWorkerHostedServices(configuration);

        Assert.Equal(16, services.Count(d => d.ServiceType == typeof(IHostedService)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Enabled_WithoutPrivateKey_StopsTheStart_NamingTheKey(string? privateKey)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => WorkerServices(Configuration(enabled: "true", privateKey: privateKey)));

        Assert.Contains(BotAccountTokenKeys.PrivateKeyName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_WithThePublicKeyAsPrivateKey_StopsTheStart_WithoutTheValue()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => WorkerServices(Configuration(enabled: "true", privateKey: PublicKey)));

        Assert.Contains(BotAccountTokenKeys.PrivateKeyName, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicKey[..24], exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PublicKey[^24..], exception.ToString(), StringComparison.Ordinal);
    }

    private static ServiceCollection WorkerServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddWorkerCore(configuration);
        return services;
    }

    private static IConfiguration Configuration(string enabled, string? privateKey, string? publicKey = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BotAccount:Enabled"] = enabled,
            [BotAccountTokenKeys.PrivateKeyName] = privateKey,
            [BotAccountTokenKeys.PublicKeyName] = publicKey,
            // AddEmotePurgeInfrastructure throws without these two; nothing is dialed here.
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=x;Username=u;Password=p",
            ["Redis:ConnectionString"] = "localhost:6379"
        }).Build();
}

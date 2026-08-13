using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Tesserafin.Controller.Extensions;
using Tesserafin.Server.Net;
using Xunit;
using TesserafinConfiguration = Tesserafin.Controller.Extensions.ConfigurationExtensions;

namespace Tesserafin.Server.Tests.Net;

/// <summary>
/// Secure bootstrap mode: how the mode is turned on, and what it refuses to be turned on by.
/// </summary>
/// <remarks>
/// Gates for tesserafin-project/tesserafin#242. Each exists because a specific controlled defect
/// must make it red — see the break-control table in the pull request.
/// </remarks>
public sealed class SecureBootstrapActivationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void IsEnabled_IsFalse_WhenTheKeyIsAbsent()
    {
        Assert.False(SecureBootstrap.IsEnabled(Config()));
    }

    [Fact]
    public void IsEnabled_IsFalse_WhenTheConfigurationIsNull()
    {
        // Fails closed to the ORDINARY behaviour: a missing configuration must not silently
        // confine a server to loopback either.
        Assert.False(SecureBootstrap.IsEnabled(null));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void IsEnabled_IsTrue_WhenTheKeyIsSet(string value)
    {
        Assert.True(SecureBootstrap.IsEnabled(Config((TesserafinConfiguration.SecureBootstrapKey, value))));
    }

    [Fact]
    public void IsEnabled_IsFalse_WhenTheKeyIsExplicitlyFalse()
    {
        Assert.False(SecureBootstrap.IsEnabled(Config((TesserafinConfiguration.SecureBootstrapKey, "false"))));
    }

    [Fact]
    public void TheKeySpellingIsTheOperatorFacingContract()
    {
        // Program.StartApp builds the startup configuration with AddEnvironmentVariables("TESSERAFIN_")
        // BEFORE the setup server binds, so this key is reachable as
        // TESSERAFIN_network__secureBootstrap. Pinning the spelling pins the documented
        // activation mechanism.
        Assert.Equal("network:secureBootstrap", TesserafinConfiguration.SecureBootstrapKey);
        Assert.True(Config((TesserafinConfiguration.SecureBootstrapKey, "true")).UseSecureBootstrap());
    }

    [Fact]
    public void TheDefaultStartupConfigurationLeavesTheModeOff()
    {
        // An existing installation that never heard of this feature keeps its current bind
        // derivation. Nothing in the product turns the mode on.
        var defaults = Tesserafin.Server.Core.ConfigurationOptions.DefaultConfiguration;

        Assert.Equal(bool.FalseString, defaults[TesserafinConfiguration.SecureBootstrapKey]);
        Assert.False(Config((TesserafinConfiguration.SecureBootstrapKey, defaults[TesserafinConfiguration.SecureBootstrapKey])).UseSecureBootstrap());
    }
}

using UkuuHr.Models;
using Xunit;

namespace UkuuHr.Tests;

/// <summary>
/// Unit tests for the AttendanceDevice display helpers (Scheme / ConnectionUrl),
/// which render scheme-aware endpoints for the device UI.
/// </summary>
public class AttendanceDeviceTests
{
    private static AttendanceDevice Device(string? ip = "10.0.0.1", int? port = null, bool useHttps = false)
        => new()
        {
            Name = "Test",
            IpAddress = ip,
            Port = port,
            UseHttps = useHttps
        };

    [Fact]
    public void Scheme_Is_Http_When_Https_Disabled()
    {
        Assert.Equal("http://", Device().Scheme);
    }

    [Fact]
    public void Scheme_Is_Https_When_Https_Enabled()
    {
        Assert.Equal("https://", Device(useHttps: true).Scheme);
    }

    [Fact]
    public void ConnectionUrl_Uses_Port_80_Default_When_Http()
    {
        Assert.Equal("http://10.0.0.1:80", Device().ConnectionUrl);
    }

    [Fact]
    public void ConnectionUrl_Uses_Port_443_Default_When_Https()
    {
        Assert.Equal("https://10.0.0.1:443", Device(useHttps: true).ConnectionUrl);
    }

    [Fact]
    public void ConnectionUrl_Honors_Explicit_Port()
    {
        var device = Device(port: 8443, useHttps: true);
        Assert.Equal("https://10.0.0.1:8443", device.ConnectionUrl);
    }

    [Fact]
    public void ConnectionUrl_Honors_Explicit_Port_With_Http()
    {
        var device = Device(port: 8080);
        Assert.Equal("http://10.0.0.1:8080", device.ConnectionUrl);
    }

    [Fact]
    public void ConnectionUrl_Is_EmDash_When_No_Ip_Configured()
    {
        Assert.Equal("—", Device(ip: null).ConnectionUrl);
        Assert.Equal("—", Device(ip: "").ConnectionUrl);
    }
}

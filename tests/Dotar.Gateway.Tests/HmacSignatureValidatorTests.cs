using System.Security.Cryptography;
using System.Text;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Services;

namespace Dotar.Gateway.Tests;

public class HmacSignatureValidatorTests
{
    private const string Secret = "super-secret-value";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"id\":42,\"event\":\"created\"}");

    private readonly HmacSignatureValidator _sut = new();

    [Fact]
    public void Validate_WooCommerce_Base64_Valid()
    {
        var sig = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Body));
        Assert.True(_sut.Validate(SignatureScheme.WooCommerce, Secret, Body, sig));
    }

    [Fact]
    public void Validate_WooCommerce_Base64_Invalid()
    {
        Assert.False(_sut.Validate(SignatureScheme.WooCommerce, Secret, Body, "AAAA"));
    }

    [Fact]
    public void Validate_GitHub_HexWithPrefix_Valid()
    {
        var hex = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Body)).ToLowerInvariant();
        Assert.True(_sut.Validate(SignatureScheme.GitHub, Secret, Body, "sha256=" + hex));
    }

    [Fact]
    public void Validate_GitHub_RejectsMissingPrefix()
    {
        var hex = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Body)).ToLowerInvariant();
        Assert.False(_sut.Validate(SignatureScheme.GitHub, Secret, Body, hex));
    }

    [Fact]
    public void Validate_Generic_Hex_Valid_CaseInsensitive()
    {
        var hex = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Body));
        // upper case should also pass — we lowercase both sides
        Assert.True(_sut.Validate(SignatureScheme.Generic, Secret, Body, hex));
        Assert.True(_sut.Validate(SignatureScheme.Generic, Secret, Body, hex.ToLowerInvariant()));
    }

    [Fact]
    public void Validate_Generic_Hex_Invalid()
    {
        Assert.False(_sut.Validate(SignatureScheme.Generic, Secret, Body, "deadbeef"));
    }

    [Fact]
    public void Validate_None_AlwaysTrue_RegardlessOfInput()
    {
        Assert.True(_sut.Validate(SignatureScheme.None, "", Body, null));
        Assert.True(_sut.Validate(SignatureScheme.None, Secret, Body, "anything"));
    }

    [Fact]
    public void Validate_RejectsEmptyBody()
    {
        Assert.False(_sut.Validate(SignatureScheme.WooCommerce, Secret, [], "x"));
    }

    [Fact]
    public void Validate_RejectsEmptySignature_WhenSchemeRequiresIt()
    {
        Assert.False(_sut.Validate(SignatureScheme.WooCommerce, Secret, Body, null));
        Assert.False(_sut.Validate(SignatureScheme.WooCommerce, Secret, Body, ""));
    }

    [Theory]
    [InlineData(SignatureScheme.WooCommerce, null, "X-WC-Webhook-Signature")]
    [InlineData(SignatureScheme.GitHub, null, "X-Hub-Signature-256")]
    [InlineData(SignatureScheme.Generic, null, "X-Webhook-Signature")]
    [InlineData(SignatureScheme.Generic, "X-Custom", "X-Custom")]
    public void ResolveHeader_ReturnsExpected(SignatureScheme scheme, string? overrideHeader, string expected)
    {
        Assert.Equal(expected, HmacSignatureValidator.ResolveHeader(scheme, overrideHeader));
    }
}

using ExecMcp.Core;
namespace ExecMcp.Tests;

public sealed class SnippingTests
{
    [Theory]
    [InlineData(SnipMode.Rectangle, "/image?rectangle")]
    [InlineData(SnipMode.Freeform, "/image?freeform")]
    [InlineData(SnipMode.Window, "/image?window")]
    [InlineData(SnipMode.Video, "/video?api-version")]
    public void Build_UsesDocumentedProtocol(SnipMode mode, string expected)
    {
        var correlation = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var uri = SnippingUriBuilder.Build(mode, correlation).AbsoluteUri;
        Assert.StartsWith("ms-screenclip://capture", uri, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expected, uri, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api-version=1.2", uri);
        Assert.Contains("auto-save", uri);
        Assert.Contains("user-agent=execmcp", uri);
        Assert.Contains("x-request-correlation-id=01234567-89ab-cdef-0123-456789abcdef", uri);
        Assert.Contains("redirect-uri=execmcp-snip%3A%2F%2Fcomplete", uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CallbackParser_ValidatesSuccess()
    {
        var id = Guid.NewGuid();
        var data = SnippingCallbackValidator.Parse(new Uri($"execmcp-snip://complete?code=200&reason=Success&x-request-correlation-id={id}&file-access-token=token"));
        Assert.Equal(200, data.Code);
        Assert.Equal(id, data.CorrelationId);
        Assert.Equal("token", data.FileAccessToken);
    }


    [Fact]
    public void CallbackParser_PreservesEscapedTokenCharacters()
    {
        var id = Guid.NewGuid();
        var data = SnippingCallbackValidator.Parse(new Uri($"execmcp-snip://complete?code=200&x-request-correlation-id={id}&file-access-token=a%2Bb%2Fc%3D"));
        Assert.Equal("a+b/c=", data.FileAccessToken);
    }

    [Fact]
    public void CallbackParser_RejectsWrongScheme() => Assert.Throws<ArgumentException>(() => SnippingCallbackValidator.Parse(new Uri("other://complete?code=499&x-request-correlation-id=01234567-89ab-cdef-0123-456789abcdef")));
}

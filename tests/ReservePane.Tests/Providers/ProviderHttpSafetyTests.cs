using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ReservePane.Providers;

namespace ReservePane.Tests.Providers;

public sealed class ProviderHttpSafetyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/problem+json")]
    public async Task ReadJsonAsync_JsonMediaTypeReturnsDocument(string mediaType)
    {
        using HttpResponseMessage response = Response("{\"value\":42}", mediaType);

        using var document = await ProviderHttpSafety.ReadJsonAsync(response, CancellationToken.None);

        Assert.Equal(42, document.RootElement.GetProperty("value").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/html")]
    [InlineData("text/json")]
    public async Task ReadJsonAsync_MissingOrUnrelatedMediaTypeIsRejected(string? mediaType)
    {
        using HttpResponseMessage response = Response("<secret>payload</secret>", mediaType);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ProviderHttpSafety.ReadJsonAsync(response, CancellationToken.None));

        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadJsonAsync_MalformedJsonIsRejectedWithoutPayloadText()
    {
        using HttpResponseMessage response = Response("{\"token\":\"secret-value\"", "application/json");

        Exception exception = await Assert.ThrowsAnyAsync<Exception>(
            () => ProviderHttpSafety.ReadJsonAsync(response, CancellationToken.None));

        Assert.True(exception is InvalidDataException or System.Text.Json.JsonException);
        Assert.DoesNotContain("secret-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadJsonAsync_OversizedJsonIsRejected()
    {
        string json = "{\"value\":\"" + new string('x', 1_048_576) + "\"}";
        using HttpResponseMessage response = Response(json, "application/json");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ProviderHttpSafety.ReadJsonAsync(response, CancellationToken.None));
    }

    [Fact]
    public async Task ReadJsonAsync_LimitAppliesToDecompressedStreamBytes()
    {
        byte[] expanded = Encoding.UTF8.GetBytes("{\"value\":\"" + new string('x', 1_048_576) + "\"}");
        byte[] compressed;
        using (var destination = new MemoryStream())
        {
            using (var gzip = new GZipStream(destination, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(expanded);
            }

            compressed = destination.ToArray();
        }

        using var compressedSource = new MemoryStream(compressed);
        using var decompressed = new GZipStream(compressedSource, CompressionMode.Decompress);
        using var content = new StreamContent(decompressed);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = compressed.Length;
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        Assert.True(compressed.Length < 1_048_576);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => ProviderHttpSafety.ReadJsonAsync(response, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(RetryAfterCases))]
    public void GetRetryAfter_UsesSafeBoundedValue(
        HttpStatusCode statusCode,
        string? retryAfter,
        TimeSpan? expected)
    {
        using var response = new HttpResponseMessage(statusCode);
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        Assert.Equal(expected, ProviderHttpSafety.GetRetryAfter(response, Now));
    }

    public static TheoryData<HttpStatusCode, string?, TimeSpan?> RetryAfterCases() => new()
    {
        { HttpStatusCode.TooManyRequests, "120", TimeSpan.FromMinutes(2) },
        { HttpStatusCode.ServiceUnavailable, "Thu, 27 Aug 2026 12:10:00 GMT", TimeSpan.FromMinutes(10) },
        { HttpStatusCode.TooManyRequests, null, TimeSpan.FromMinutes(5) },
        { HttpStatusCode.ServiceUnavailable, "invalid", TimeSpan.FromMinutes(5) },
        { HttpStatusCode.TooManyRequests, "-1", TimeSpan.FromMinutes(5) },
        { HttpStatusCode.ServiceUnavailable, "Thu, 27 Aug 2026 11:59:00 GMT", TimeSpan.FromMinutes(5) },
        { HttpStatusCode.TooManyRequests, "7200", TimeSpan.FromHours(1) },
        { HttpStatusCode.OK, "120", null },
        { HttpStatusCode.InternalServerError, null, null },
    };

    private static HttpResponseMessage Response(string body, string? mediaType)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (mediaType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

using System.Text;
using QuotaGlass.Providers;

namespace QuotaGlass.Tests.Providers;

public sealed class OpenCodeConsoleActiveWorkspaceReaderTests
{
    [Fact]
    public async Task ReadAsync_QueriesOnlyTheActiveConsoleSelectionAndAccessToken()
    {
        // Catches broad account enumeration or selection of refresh tokens and user metadata.
        string? issuedQuery = null;
        var reader = new OpenCodeConsoleActiveWorkspaceReader((query, _) =>
        {
            issuedQuery = query;
            return Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(
                """
                [{"account_id":"account-active","access_token":"access-active","token_expiry":1787832000000,"active_org_id":"org-active"}]
                """));
        });

        OpenCodeConsoleActiveWorkspace? workspace = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(workspace);
        Assert.Equal("account-active", workspace.AccountId);
        Assert.Equal("access-active", workspace.AccessToken);
        Assert.Equal("org-active", workspace.OrganizationId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787832000000), workspace.ExpiresAt);
        Assert.Contains("account_state", issuedQuery, StringComparison.Ordinal);
        Assert.Contains("active_account_id", issuedQuery, StringComparison.Ordinal);
        Assert.Contains("active_org_id", issuedQuery, StringComparison.Ordinal);
        Assert.Contains("a.access_token", issuedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh", issuedQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", issuedQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("[{\"account_id\":\"account\",\"access_token\":\"access\",\"active_org_id\":\"\"}]")]
    public async Task ReadAsync_MissingOrIncompleteActiveSelectionReturnsNull(string json)
    {
        // Catches an incomplete state row being used to issue a request for the wrong workspace.
        var reader = Reader(json);

        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_MultipleOrMalformedRowsAreRejected()
    {
        // Catches ambiguous active selections silently choosing an arbitrary credential.
        OpenCodeConsoleActiveWorkspaceReader multiple = Reader(
            """
            [{"account_id":"one","access_token":"access-one","active_org_id":"org-one"},{"account_id":"two","access_token":"access-two","active_org_id":"org-two"}]
            """);
        OpenCodeConsoleActiveWorkspaceReader malformed = Reader("{broken");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => multiple.ReadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => malformed.ReadAsync(CancellationToken.None));
    }

    private static OpenCodeConsoleActiveWorkspaceReader Reader(string json) => new(
        (_, _) => Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(json)));
}

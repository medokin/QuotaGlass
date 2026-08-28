using System.Text;
using ReservePane.Providers;

namespace ReservePane.Tests.Providers;

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

        OpenCodeConsoleActiveWorkspaceReadResult result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(OpenCodeConsoleActiveWorkspaceReadOutcome.Success, result.Outcome);
        OpenCodeConsoleActiveWorkspace workspace = Assert.IsType<OpenCodeConsoleActiveWorkspace>(result.Workspace);
        Assert.NotNull(workspace);
        Assert.Equal("account-active", workspace.AccountId);
        Assert.Equal("access-active", workspace.AccessToken);
        Assert.Equal("org-active", workspace.OrganizationId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787832000000), workspace.ExpiresAt);
        Assert.Equal(
            "select a.id as account_id, a.access_token, a.token_expiry, s.active_org_id " +
            "from account_state s join account a on a.id = s.active_account_id " +
            "where s.id = 1 and a.url = 'https://opencode.ai/console' " +
            "and s.active_org_id is not null limit 2;",
            issuedQuery);
    }

    [Fact]
    public async Task ReadAsync_EmptyActiveSelectionIsSuccessfulAndNotConfigured()
    {
        OpenCodeConsoleActiveWorkspaceReadResult result = await Reader("[]")
            .ReadAsync(CancellationToken.None);

        Assert.Equal(OpenCodeConsoleActiveWorkspaceReadOutcome.Success, result.Outcome);
        Assert.Null(result.Workspace);
    }

    [Theory]
    [InlineData("[{}]")]
    [InlineData("[{\"account_id\":\"account\",\"access_token\":\"access\",\"active_org_id\":\"\"}]")]
    [InlineData("[{\"account_id\":\"one\",\"access_token\":\"access-one\",\"active_org_id\":\"org-one\"},{\"account_id\":\"two\",\"access_token\":\"access-two\",\"active_org_id\":\"org-two\"}]")]
    [InlineData("{broken")]
    public async Task ReadAsync_IncompleteAmbiguousOrMalformedSelectionIsInvalid(string json)
    {
        // Catches an incomplete state row being treated as a valid unconfigured selection.
        OpenCodeConsoleActiveWorkspaceReadResult result = await Reader(json)
            .ReadAsync(CancellationToken.None);

        Assert.Equal(OpenCodeConsoleActiveWorkspaceReadOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Workspace);
    }

    [Fact]
    public async Task ReadAsync_CommandFailureIsTransient()
    {
        var reader = new OpenCodeConsoleActiveWorkspaceReader(
            (_, _) => Task.FromResult<byte[]?>(null));

        OpenCodeConsoleActiveWorkspaceReadResult result = await reader
            .ReadAsync(CancellationToken.None);

        Assert.Equal(OpenCodeConsoleActiveWorkspaceReadOutcome.TransientFailure, result.Outcome);
        Assert.Null(result.Workspace);
    }

    private static OpenCodeConsoleActiveWorkspaceReader Reader(string json) => new(
        (_, _) => Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(json)));
}

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NodePilot.Api.Security.Scim;
using Xunit;

namespace NodePilot.Api.Tests.Security.Scim;

public sealed class ScimAuthenticationTests
{
    [Fact]
    public void ValidBearerToken_IsAccepted()
    {
        var token = new string('x', 32);
        var auth = new ScimAuthentication(Options.Create(new ScimOptions
        {
            Enabled = true, BearerToken = token,
        }));
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Bearer " + token;

        auth.IsAuthorized(request).Should().BeTrue();
    }

    [Fact]
    public void PreviousBearerToken_IsAcceptedDuringRotation()
    {
        var previous = new string('p', 32);
        var auth = new ScimAuthentication(Options.Create(new ScimOptions
        {
            Enabled = true,
            BearerToken = new string('c', 32),
            PreviousBearerToken = previous,
        }));
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Bearer " + previous;

        auth.IsAuthorized(request).Should().BeTrue();
    }

    [Fact]
    public void RemovedPreviousBearerToken_IsRejected()
    {
        var previous = new string('p', 32);
        var auth = new ScimAuthentication(Options.Create(new ScimOptions
        {
            Enabled = true,
            BearerToken = new string('c', 32),
        }));
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Bearer " + previous;

        auth.IsAuthorized(request).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer wrong")]
    [InlineData("Basic eDp5")]
    public void InvalidAuthorization_IsRejected(string header)
    {
        var auth = new ScimAuthentication(Options.Create(new ScimOptions
        {
            Enabled = true, BearerToken = new string('x', 32),
        }));
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = header;

        auth.IsAuthorized(request).Should().BeFalse();
    }
}

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace NodePilot.Api.Tests.TestSupport;

/// <summary>
/// Plain-property <see cref="IWebHostEnvironment"/> stub for hosting-layer tests that call
/// static setup/validation helpers directly (no <c>WebApplicationFactory</c> host). Defaults to
/// <c>Production</c> because the hardening code paths under test are no-ops in Development;
/// pass or set <see cref="EnvironmentName"/> to opt into the relaxed behavior. Shared here so
/// the Hosting test files do not each keep a private copy.
/// </summary>
public sealed class StubEnvironment(string contentRoot = "", string environmentName = "Production") : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "NodePilot.Api";
    public string WebRootPath { get; set; } = contentRoot;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

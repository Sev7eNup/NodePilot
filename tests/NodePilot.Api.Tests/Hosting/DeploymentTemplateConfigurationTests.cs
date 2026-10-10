using FluentAssertions;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public class DeploymentTemplateConfigurationTests
{
    [Fact]
    public void ClusterTemplate_LoadsThroughTheRealConfigurationProvider()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NodePilot.slnx")))
            root = root.Parent;
        root.Should().NotBeNull();
        var template = File.ReadAllText(Path.Combine(root!.FullName,
            "deploy", "templates", "appsettings.Cluster.json.template"));
        var rendered = Regex.Replace(template, @"\{\{[A-Z0-9_]+\}\}", "fixture");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rendered));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        configuration["Cluster:Enabled"].Should().Be("True");
        configuration["Cluster:NodeId"].Should().Be("fixture");
        configuration["Secrets:Provider"].Should().Be("AesGcm");
        configuration["Jwt:Issuer"].Should().Be("fixture");
    }
}

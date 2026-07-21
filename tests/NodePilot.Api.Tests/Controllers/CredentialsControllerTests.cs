using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class CredentialsControllerTests
{
    [Fact]
    public async Task GetAll_ReturnsCredentials()
    {
        // Arrange
        var mockStore = new Mock<ICredentialStore>();
        var credentials = new List<Credential>
        {
            new() { Id = Guid.NewGuid(), Name = "Cred1", Username = "user1", Domain = "DOMAIN" },
            new() { Id = Guid.NewGuid(), Name = "Cred2", Username = "user2" }
        };
        mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(credentials);

        var controller = new CredentialsController(mockStore.Object, NoopAuditWriter.Instance);

        // Act
        var result = await controller.GetAll(CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeAssignableTo<List<CredentialResponse>>().Subject;
        response.Should().HaveCount(2);
        response[0].Name.Should().Be("Cred1");
        response[1].Name.Should().Be("Cred2");
    }

    [Fact]
    public async Task GetById_Exists_ReturnsCredential()
    {
        // Arrange
        var mockStore = new Mock<ICredentialStore>();
        var credId = Guid.NewGuid();
        var credential = new Credential
        {
            Id = credId,
            Name = "TestCred",
            Username = "admin",
            Domain = "CORP"
        };
        mockStore.Setup(s => s.GetAsync(credId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(credential);

        var controller = new CredentialsController(mockStore.Object, NoopAuditWriter.Instance);

        // Act
        var result = await controller.GetById(credId, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<CredentialResponse>().Subject;
        response.Id.Should().Be(credId);
        response.Name.Should().Be("TestCred");
        response.Username.Should().Be("admin");
        response.Domain.Should().Be("CORP");
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        // Arrange
        var mockStore = new Mock<ICredentialStore>();
        var credId = Guid.NewGuid();
        mockStore.Setup(s => s.GetAsync(credId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var controller = new CredentialsController(mockStore.Object, NoopAuditWriter.Instance);

        // Act
        var result = await controller.GetById(credId, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_ValidRequest_Returns201()
    {
        // Arrange
        var mockStore = new Mock<ICredentialStore>();
        var credId = Guid.NewGuid();
        mockStore.Setup(s => s.CreateAsync("NewCred", "admin", "password", "DOMAIN", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Credential
            {
                Id = credId,
                Name = "NewCred",
                Username = "admin",
                Domain = "DOMAIN"
            });

        var controller = new CredentialsController(mockStore.Object, NoopAuditWriter.Instance);
        var request = new CreateCredentialRequest("NewCred", "admin", "password", "DOMAIN");

        // Act
        var result = await controller.Create(request, CancellationToken.None);

        // Assert
        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var response = created.Value.Should().BeOfType<CredentialResponse>().Subject;
        response.Id.Should().Be(credId);
        response.Name.Should().Be("NewCred");
    }

    [Theory]
    [InlineData("", "admin")]
    [InlineData("Cred", "")]
    public async Task Create_InvalidNameOrUsername_Returns400(string name, string username)
    {
        var mockStore = new Mock<ICredentialStore>();
        var controller = new CredentialsController(mockStore.Object, NoopAuditWriter.Instance);

        var result = await controller.Create(
            new CreateCredentialRequest(name, username, "password", null),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        mockStore.Verify(s => s.CreateAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_OverlongUsername_Returns400()
    {
        var mockStore = new Mock<ICredentialStore>();
        var controller = new CredentialsController(mockStore.Object, NoopAuditWriter.Instance);
        var request = new UpdateCredentialRequest("Cred", new string('u', 201), null, null);

        var result = await controller.Update(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        mockStore.Verify(s => s.UpdateAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_Exists_Returns204()
    {
        // Arrange
        var mockStore = new Mock<ICredentialStore>();
        var credId = Guid.NewGuid();
        mockStore.Setup(s => s.UpdateAsync(credId, "Updated", "newuser", "newpass123", "NEWDOMAIN", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = new CredentialsController(mockStore.Object, NoopAuditWriter.Instance);
        var request = new UpdateCredentialRequest("Updated", "newuser", "newpass123", "NEWDOMAIN");

        // Act
        var result = await controller.Update(credId, request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Update_NotFound_Returns404()
    {
        // Arrange
        var mockStore = new Mock<ICredentialStore>();
        var credId = Guid.NewGuid();
        mockStore.Setup(s => s.UpdateAsync(credId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var controller = new CredentialsController(mockStore.Object, NoopAuditWriter.Instance);
        var request = new UpdateCredentialRequest("Updated", "user", null, null);

        // Act
        var result = await controller.Update(credId, request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_Exists_Returns204()
    {
        // Arrange
        var mockStore = new Mock<ICredentialStore>();
        var credId = Guid.NewGuid();
        mockStore.Setup(s => s.DeleteAsync(credId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = new CredentialsController(mockStore.Object, NoopAuditWriter.Instance);

        // Act
        var result = await controller.Delete(credId, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        // Arrange
        var mockStore = new Mock<ICredentialStore>();
        var credId = Guid.NewGuid();
        mockStore.Setup(s => s.DeleteAsync(credId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var controller = new CredentialsController(mockStore.Object, NoopAuditWriter.Instance);

        // Act
        var result = await controller.Delete(credId, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }
}

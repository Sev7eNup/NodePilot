using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class WorkflowContractAuthorizationTests
{
    [Theory]
    [InlineData(UserRole.Viewer, SharedFolderRole.FolderViewer, false)]
    [InlineData(UserRole.Viewer, SharedFolderRole.FolderEditor, false)]
    [InlineData(UserRole.Operator, SharedFolderRole.FolderViewer, false)]
    [InlineData(UserRole.Operator, SharedFolderRole.FolderOperator, false)]
    [InlineData(UserRole.Operator, SharedFolderRole.FolderEditor, true)]
    [InlineData(UserRole.Admin, SharedFolderRole.FolderViewer, true)]
    public async Task BothContractRoutes_ExposeDefaultsOnlyWithWorkflowEditCapability(
        UserRole globalRole, SharedFolderRole folderRole, bool canEdit)
    {
        await using var db = TestDbFactory.Create();
        var user = new User { Id = Guid.NewGuid(), Username = "contract-reader", Role = globalRole };
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "Protected contract",
            DefinitionJson = """
                {"nodes":[
                  {"id":"trigger","data":{"activityType":"manualTrigger","config":{"parameters":[
                    {"name":"serviceCredential","type":"string","required":true,"default":"opaque-default-credential","description":"Service credential"},
                    {"name":"empty","type":"string","default":""},
                    {"name":"target","type":"string","required":true}
                  ]}}},
                  {"id":"result","data":{"activityType":"returnData","config":{"data":{"completed":"yes"}}}}
                ],"edges":[]}
                """,
        };
        db.AddRange(user, workflow, new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = SharedWorkflowFolder.RootFolderId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = user.Id.ToString("D"), Role = folderRole,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var controller = WorkflowControllerHarnessFactory.Build(db,
            role: globalRole.ToString(), userId: user.Id,
            authz: new ResourceAuthorizationService(db)).Workflows;
        var ct = TestContext.Current.CancellationToken;

        var detail = (await controller.GetById(workflow.Id, ct)).Result.Should()
            .BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<WorkflowResponse>().Subject;
        detail.Capabilities.CanEdit.Should().Be(canEdit);
        if (!canEdit) detail.DefinitionJson.Should().NotContain("opaque-default-credential");

        var byId = await controller.GetContract(workflow.Id, ct);
        var byName = await controller.GetContractByName(workflow.Name, ct);
        foreach (var result in new[] { byId, byName })
        {
            var contract = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
                .BeOfType<WorkflowContractResponse>().Subject;
            contract.Inputs.Should().HaveCount(3);
            var credential = contract.Inputs.Single(input => input.Name == "serviceCredential");
            credential.Default.Should().Be(canEdit ? "opaque-default-credential" : "***");
            credential.Required.Should().BeTrue();
            credential.Description.Should().Be("Service credential");
            contract.Inputs.Single(input => input.Name == "empty").Default.Should().Be(canEdit ? "" : "***");
            contract.Inputs.Single(input => input.Name == "target").Default.Should().BeNull();
            contract.Outputs.Should().Contain(output => output.Name == "completed");
        }
        workflow.DefinitionJson.Should().Contain("opaque-default-credential",
            "response masking must preserve the runtime default stored in the workflow");
    }
}

# Creating a first workflow with NodePilot

A workflow that reads the NodePilot host's name is enough to get started. This example consists of a manual trigger and a PowerShell step. It requires a running NodePilot instance and an Admin or Operator account.

**1. Create the workflow**

Under “Workspace > Workflows”, create a workflow named “Check host” and select “Edit”. The designer is where individual steps are placed and connected.

**2. Connect the trigger and activity**

Drag a “Manual Trigger” and a “Run Script” activity from the node library onto the canvas. Connect the trigger's output to the activity's input. The trigger determines when execution starts, and the activity runs the script.

![Two connected steps in the NodePilot designer](images/designer-beispiel.png)

*Screenshot of a different example workflow using “Greeting Probe”. The connection for “Run Script” is created in the same way.*

**3. Add the PowerShell script**

Select the “Run Script” activity and enter this script:

```powershell
$env:COMPUTERNAME
```

Set the output variable to `hostInfo`. Leave the “Machine” field empty so the script runs locally on the NodePilot host under the service identity. No separate target machine or additional credentials are needed.

**4. Publish and run**

Publish the workflow using “Publish” and start it with “Run”. Open the execution and inspect the “Run Script” activity's output. It contains the name of the computer running NodePilot.

![A successfully completed example run in NodePilot](images/ausfuehrung-beispiel.png)

*Successful execution of the “Greeting Probe” example workflow, showing the execution path and “Succeeded” status.*

If the run fails, the affected step's status and output provide clues to the cause. After a successful test, another step can be added to process the result.

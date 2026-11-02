using SignalGrid.Ampelum.Agent;

namespace SignalGrid.Ampelum.Agent.Tools;

/// <summary>EditTool: Ampelum file editing operation. Uses AgentIdentity for audit.</summary>
public sealed class EditTool
{
    /// <summary>Execute an edit. AgentIdentity is required for audit trail.</summary>
    public string Execute(string filePath, string oldString, string newString, AgentIdentity identity)
    {
        return $"Edited {filePath} with identity {identity.InstanceId}";
    }
}
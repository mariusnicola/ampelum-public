namespace SignalGrid.Ampelum.Context.Subagent;  
  
/// <summary>Identity of a subagent invocation with ULID-based jti for replay protection.</summary>  
public sealed record SubagentIdentity(  
    string SubagentId,  
    string Jti,  
    string? ParentJti,  
    DateTimeOffset CreatedAt); 

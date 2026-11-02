### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
AMP001  | Usage    | Error    | ITypedTool string property/parameter without [FreeForm]
AMP002  | Usage    | Warning  | ITypedTool implementation without [ToolCategory]
AMP003  | Usage    | Warning  | Side-effecting ITypedTool without IPreviewable
AMP1010 | Usage    | Warning  | [FreeForm] used in an assembly missing [assembly: AllowFreeForm]
AMP010  | Maintainability | Warning  | File exceeds maximum line count (configurable via .editorconfig)
AMP011  | Maintainability | Warning  | Type has too many public methods (configurable via .editorconfig)
AMP012  | Architecture     | Warning  | Namespace layer rule violation (configurable via .editorconfig)
AMP013  | Compatibility    | Warning  | Use of 'dynamic' keyword — not AOT-safe
AMP014  | Architecture     | Error    | Bare LLM prompt construction — use Predict<TSig> instead
AMP015  | Usage            | Warning  | ISignature implementation missing [Signature] attribute

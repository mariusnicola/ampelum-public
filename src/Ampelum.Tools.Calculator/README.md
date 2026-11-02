# Ampelum.Tools.Calculator

Pre-built `ITypedTool<CalcResult>` for arithmetic. Uses `DataTable.Compute` (no method calls, no I/O).

## Usage

```csharp
using Ampelum.Tools.Calculator;
var tool = new CalculatorTool("3.14159 * 5 * 5");
var result = await tool.InvokeAsync(ct);
// result.Value == 78.53975
```

## POLC tags

- `[ToolCategory(ToolCategoryKind.Compute)]`
- `compute:pure`
- `data:none`

## Plan deviation

The original plan called for NCalc; this implementation uses `System.Data.DataTable.Compute`
to avoid a third-party dependency for the v0.9f preview. Equivalent feature surface for
basic arithmetic; future versions may swap in NCalc / UnitsNet for unit-aware support.

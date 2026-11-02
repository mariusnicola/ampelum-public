# Ampelum.Tools.CodeInterpreter

`CodeInterpreterTool` evaluates a C# expression or script via
`Microsoft.CodeAnalysis.CSharp.Scripting` and returns the value of the last
expression as `CodeResult(object? Value, string Display)`.

## Security warning

This tool is **unsandboxed**. The script runs in the host process with full
access to the file system, the network, environment variables, and any
assemblies already loaded into the AppDomain. **Do NOT pass untrusted input.**

POLC tags: `compute:host-process`, `risk:high`, `network:any`, `fs:any`.

A sandboxed variant (Docker / WASM / V8 isolate) is post-v1.0.

## Example

```csharp
var tool = new CodeInterpreterTool("Enumerable.Range(1, 10).Sum()");
var result = await tool.InvokeAsync(CancellationToken.None);
Console.WriteLine(result.Display); // "55"
```

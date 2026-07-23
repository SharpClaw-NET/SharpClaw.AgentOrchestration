# SharpClaw Agent Orchestration

SharpClaw Agent Orchestration is a runtime-loadable SharpClaw module package.
The package id is `SharpClaw.Modules.AgentOrchestration`, the module id is
`sharpclaw_agent_orchestration`, and SharpClaw loads the sidecar payload from
the package `sharpclaw` directory through
`SharpClaw.Modules.AgentOrchestration.dll`.

The module contributes the `ao` tool prefix, sub-agent creation and management,
skill access, custom agent and channel headers, and cross-thread context tools.
It is enabled by default in `module.json`, so a compatible SharpClaw host can
discover and activate it without a local source checkout.

Build and package from the repository root with `dotnet restore`,
`dotnet build`, `dotnet test`, and `dotnet pack -c Release`. The NuGet package
intentionally carries the runtime payload under `sharpclaw`, including
`module.json`, the module assembly, the `.deps.json` file, and package-local
dependency assemblies required by the sidecar load path.

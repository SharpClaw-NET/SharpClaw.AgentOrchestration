# SharpClaw Agent Orchestration

This repository provides the package-owned Context, Two Tier Permission, and Agents modules for SharpClaw.

`SharpClaw.Modules.Context` owns Threads, Channels, Contexts, conversation history, and context assembly. `SharpClaw.Modules.TwoTierPermission` owns clearance, scope, denials, delegation, grants, and approvals. `SharpClaw.Modules.Agents` owns Agents, Skills, Memory, profiles, and management tools.

The modules use current `SharpClaw.Contracts` module builders, declared storage contracts, `ModuleDocumentStore<T>`, `IModuleStorageGateway`, and `IModuleDbContextFactory`. Jobs and Events remain kernel-owned. The packages use declared module boundaries and contain no host project references.

All coordinated packages use version `0.5.0-beta.1`. The package payload places each module manifest, module assembly, dependency graph, and package-local dependency assemblies under `sharpclaw\`. The package metadata uses this repository and the `AGPL-3.0-only` license.

Run `dotnet restore`, `dotnet build`, `dotnet test`, and `dotnet pack -c Release` from the repository root. Publication is an owner-controlled step and is outside this repository change.

# SharpClaw Agent Orchestration

This repository provides the package-owned Context, Two Tier Permission, and Agents modules for SharpClaw.

`SharpClaw.Modules.Context` owns Threads, Channels, Contexts, conversation history, and context assembly. `SharpClaw.Modules.TwoTierPermission` owns clearance, scope, denials, delegation, grants, and approvals. `SharpClaw.Modules.Agents` owns Agents, Skills, Memory, profiles, management tools, and module-owned `AgentJob` definitions. The module stores the canonical Jobs identity after kernel scheduling and projects canonical completion through typed module actions.

Agents also accepts a neutral `CanonicalJobsImportSnapshot`. Each source action must have an exact package handler and payload-codec mapping. The converter preserves stable source identities, maps queued and paused records to the canonical handler mode, maps active records to canonical recovery, and rejects unknown actions, codecs, identities, status values, or result authority.

Each import binds its snapshot id, capture time, expected count, ordered source identities, per-source SHA-256 hashes, ordered aggregate hash, and ordered action-mapping hash. The Agents module creates the manifest at revision zero and completes it against the observed revision. Exact replay is accepted. Changed, missing, extra, reordered, or conflicting records and mappings fail closed. An interrupted import resumes the same incomplete manifest.

The modules use current `SharpClaw.Contracts` module builders, declared storage contracts, `ModuleDocumentStore<T>`, and `IModuleStorageGateway`. Jobs and Events remain kernel-owned. The packages use declared module boundaries and contain no host project references.

The application contributions expose package-owned HTTP routes for context thread and history actions, permission evaluation and administration, and agent, skill, and memory actions. Each route creates a caller principal and invokes the owning action executor, so authorization and persistence use the same module path as tools and CLI commands.

The Context and Two Tier Permission packages use version `0.5.0-beta.12`. The Agents package uses version `0.5.0-beta.13`. The package payload places each module manifest, module assembly, dependency graph, and package-local dependency assemblies under `sharpclaw\`. The package metadata uses this repository and the `AGPL-3.0-only` license.

Run `dotnet restore`, `dotnet build`, `dotnet test`, and `dotnet pack -c Release` from the repository root. Publication is an owner-controlled step and is outside this repository change.

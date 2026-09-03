# SharpClaw Agent Orchestration

## Purpose

SharpClaw Agent Orchestration provides optional Context, Two Tier Permission, and Agents modules. Each module uses neutral SharpClaw contracts and its own storage.

## Context

`SharpClaw.Modules.Context` owns threads, channels, contexts, conversation history, and prompt context assembly. It requests access decisions from the active permission module.

## Permissions

`SharpClaw.Modules.TwoTierPermission` supplies the default Agent Orchestration permission policy. A replacement module can provide the same neutral permission contract with one policy implementation.

## Agents

`SharpClaw.Modules.Agents` owns agents, skills, memory, profiles, management tools, and Agent Job definitions. Canonical Jobs remains the kernel scheduler and execution authority.

## Permission Development

The permission authoring API keeps caller authority in `ActionContext`. It hides descriptor, schema, terminal, contract, and relay registration from normal module code. The [permission module guide](docs/permission-modules.md) shows replacement, consumption, storage, testing, and low-level interception.

## Job Import

The Agents module accepts a neutral `CanonicalJobsImportSnapshot`. It verifies source identities, payload hashes, action mappings, status, recovery data, and replay state before writes.

## Build

Run `dotnet restore`, `dotnet build -c Release`, and `dotnet test -c Release` from the repository root. Package publication requires separate owner approval.

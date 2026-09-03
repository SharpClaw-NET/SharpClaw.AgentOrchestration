# SharpClaw Agent Orchestration

## Purpose

SharpClaw Agent Orchestration provides optional Context, Two Tier Permission, and Agents modules. Each module uses neutral SharpClaw contracts and its own storage.

## Context

`SharpClaw.Modules.Context` owns threads, channels, contexts, conversation history, and prompt context assembly. It requests access decisions from the active permission module.

## Permissions

`SharpClaw.Modules.TwoTierPermission` supplies the default permission provider. Another module can replace it through the neutral provider contract.

Independent permission modules can complement either provider through restriction-only hooks. Each hook can preserve or deny access, but it cannot grant access.

## Agents

`SharpClaw.Modules.Agents` owns agents, skills, memory, profiles, management tools, and Agent Job definitions. Canonical Jobs remains the kernel scheduler and execution authority.

## Permission Development

The permission authoring API keeps caller authority in `ActionContext`. It supplies simple helpers for providers, consumers, and independent restrictions.

The [permission module guide](docs/permission-modules.md) shows replacement, restriction composition, storage, testing, and low-level action control.

## Job Import

The Agents module accepts a neutral `CanonicalJobsImportSnapshot`. It verifies source identities, payload hashes, action mappings, status, recovery data, and replay state before writes.

## Build

Run `dotnet restore`, `dotnet build -c Release`, and `dotnet test -c Release` from the repository root. Package publication requires separate owner approval.

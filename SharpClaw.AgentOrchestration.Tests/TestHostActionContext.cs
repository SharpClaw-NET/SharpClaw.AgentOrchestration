using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.AgentOrchestration.Tests;

internal static class TestHostActionContext
{
    public static HostActionEntryRequestContext Create(
        RequestPrincipal caller,
        HostActionEntryIngress ingress = HostActionEntryIngress.Tool)
    {
        var now = DateTimeOffset.UtcNow;
        return new HostActionEntryRequestContext(
            Guid.NewGuid(),
            "test-capability-handle",
            ingress,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            caller,
            new ExtensionFeatureSet([
                new ExtensionFeature(
                    "test.feature",
                    1,
                    "test.module",
                    1024,
                    JsonSerializer.SerializeToElement(new { enabled = true })),
            ]),
            Guid.NewGuid(),
            Guid.NewGuid(),
            now.AddMinutes(1),
            now.AddMinutes(2));
    }
}

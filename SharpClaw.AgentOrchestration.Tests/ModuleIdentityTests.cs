using System.Text.Json;
using NUnit.Framework;
using SharpClaw.Modules.AgentOrchestration;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class ModuleIdentityTests
{
    [Test]
    public void ManifestPreservesRuntimeModuleIdentity()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..",
            "..",
            "..",
            "..",
            "SharpClaw.Modules.AgentOrchestration",
            "module.json"));

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("id").GetString(), Is.EqualTo(AgentOrchestrationModule.ModuleIdValue));
            Assert.That(root.GetProperty("version").GetString(), Is.EqualTo("0.1.1-beta.1"));
            Assert.That(root.GetProperty("toolPrefix").GetString(), Is.EqualTo("ao"));
            Assert.That(root.GetProperty("runtime").GetString(), Is.EqualTo("dotnet"));
            Assert.That(root.GetProperty("hostMode").GetString(), Is.EqualTo("sidecar"));
            Assert.That(root.GetProperty("entryAssembly").GetString(), Is.EqualTo("SharpClaw.Modules.AgentOrchestration.dll"));
            Assert.That(root.GetProperty("moduleType").GetString(), Is.EqualTo(typeof(AgentOrchestrationModule).FullName));
            Assert.That(root.GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("defaultEnabled").GetBoolean(), Is.True);
        });
    }

    [Test]
    public void ModuleReportsExpectedHostIdentity()
    {
        var module = new AgentOrchestrationModule();

        Assert.Multiple(() =>
        {
            Assert.That(module.Id, Is.EqualTo("sharpclaw_agent_orchestration"));
            Assert.That(module.DisplayName, Is.EqualTo("Agent Orchestration"));
            Assert.That(module.ToolPrefix, Is.EqualTo("ao"));
        });
    }

    [Test]
    public void ModuleExposesOnlyRetainedAgentSkillAndContextSurfaces()
    {
        var module = new AgentOrchestrationModule();
        var tools = module.GetToolDefinitions().Select(tool => tool.Name).ToArray();
        var storage = module.GetStorageContracts().Select(contract => contract.StorageName).ToArray();
        var commands = module.GetCliCommands().Select(command => command.Name).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(tools, Is.EquivalentTo(new[]
            {
                "create_sub_agent",
                "ao_manage_agent",
                "ao_access_skill",
                "ao_edit_agent_header",
                "ao_edit_channel_header",
            }));
            Assert.That(storage, Is.EquivalentTo(new[] { "skills" }));
            Assert.That(commands, Is.EquivalentTo(new[] { "aoskill" }));
        });
    }
}

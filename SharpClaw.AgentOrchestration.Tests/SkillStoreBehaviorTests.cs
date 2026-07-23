using System.Text.Json;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration;
using SharpClaw.Modules.AgentOrchestration.Models;
using SharpClaw.Modules.AgentOrchestration.Services;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class SkillStoreBehaviorTests
{
    [Test]
    public async Task SkillStorePersistsSortsUpdatesAndDeletesThroughStorageContracts()
    {
        var gateway = new InMemoryStorageGateway();
        var store = new SkillStore(gateway);
        var zulu = await store.CreateAsync(new SkillDB
        {
            Name = "Zulu",
            SkillText = "Zulu instructions",
        });
        var alpha = await store.CreateAsync(new SkillDB
        {
            Name = "Alpha",
            SkillText = "Alpha instructions",
        });

        var ordered = await store.ListAsync();
        Assert.That(ordered.Select(skill => skill.Name), Is.EqualTo(new[] { "Alpha", "Zulu" }));

        var updated = await store.UpdateAsync(zulu.Id, skill =>
        {
            skill.Name = "Beta";
            skill.SkillText = "Updated instructions";
        });

        Assert.Multiple(() =>
        {
            Assert.That(updated!.Name, Is.EqualTo("Beta"));
            Assert.That(updated.SkillText, Is.EqualTo("Updated instructions"));
            Assert.That(updated.UpdatedAt, Is.GreaterThanOrEqualTo(updated.CreatedAt));
        });

        var fetched = await store.GetByIdAsync(zulu.Id);
        Assert.That(fetched!.SkillText, Is.EqualTo("Updated instructions"));
        Assert.That(await store.DeleteAsync(alpha.Id), Is.True);
        Assert.That(await store.GetByIdAsync(alpha.Id), Is.Null);
        Assert.That(gateway.Operations, Does.Contain(ModuleStorageOperations.Upsert));
        Assert.That(gateway.Operations, Does.Contain(ModuleStorageOperations.List));
        Assert.That(gateway.Operations, Does.Contain(ModuleStorageOperations.Get));
        Assert.That(gateway.Operations, Does.Contain(ModuleStorageOperations.Delete));
    }

    private sealed class InMemoryStorageGateway : IModuleStorageGateway
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _records = new(StringComparer.Ordinal);

        public List<string> Operations { get; } = [];

        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() =>
            new AgentOrchestrationModule().GetStorageContracts();

        public Task<JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            JsonElement payload,
            CancellationToken ct)
        {
            Operations.Add(operation);
            var result = operation switch
            {
                ModuleStorageOperations.Get => Get(payload),
                ModuleStorageOperations.Upsert => Upsert(payload),
                ModuleStorageOperations.Delete => Delete(payload),
                ModuleStorageOperations.List => List(),
                _ => throw new NotSupportedException(operation),
            };

            return Task.FromResult(result);
        }

        private JsonElement Get(JsonElement payload)
        {
            var key = payload.GetProperty("key").GetString()!;
            return _records.TryGetValue(key, out var value)
                ? JsonSerializer.SerializeToElement(new { found = true, key, value }, JsonOptions)
                : JsonSerializer.SerializeToElement(new { found = false }, JsonOptions);
        }

        private JsonElement Upsert(JsonElement payload)
        {
            var key = payload.GetProperty("key").GetString()!;
            _records[key] = payload.GetProperty("value").Clone();
            return JsonSerializer.SerializeToElement(new { saved = true }, JsonOptions);
        }

        private JsonElement Delete(JsonElement payload)
        {
            var key = payload.GetProperty("key").GetString()!;
            return JsonSerializer.SerializeToElement(
                new { deleted = _records.Remove(key) }, JsonOptions);
        }

        private JsonElement List()
        {
            var records = _records.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { key = pair.Key, value = pair.Value });
            return JsonSerializer.SerializeToElement(new { records }, JsonOptions);
        }
    }
}

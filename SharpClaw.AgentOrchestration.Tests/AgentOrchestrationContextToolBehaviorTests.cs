using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Modules.AgentOrchestration;

namespace SharpClaw.AgentOrchestration.Tests;

[TestFixture]
public sealed class AgentOrchestrationContextToolBehaviorTests
{
    [Test]
    public async Task AdminRoleAgentCanListAndReadAnotherDirectChannelThread()
    {
        await using var fixture = AoContextFixture.Create();
        var agent = fixture.SeedAgentWithCrossThreadRole(PermissionClearance.Independent);
        var currentChannel = MakeChannel("Current Channel", agent.Id);
        var sourceChannel = MakeChannel("Source Channel", agent.Id);
        var sourceThread = MakeThread("Source Thread", sourceChannel.Id);
        var sourceMessage = MakeMessage(sourceChannel.Id, sourceThread.Id, "hello from source channel");

        fixture.Db.Channels.AddRange(currentChannel, sourceChannel);
        fixture.Db.ChatThreads.Add(sourceThread);
        fixture.Db.ChatMessages.Add(sourceMessage);
        await fixture.Db.SaveChangesAsync();

        var listResult = await ExecuteInlineToolAsync(
            fixture, "list_accessible_threads", "{}", agent.Id, currentChannel.Id);

        Assert.That(listResult, Does.Contain(sourceThread.Id.ToString("D")));
        Assert.That(listResult, Does.Contain("Source Channel"));

        var readResult = await ExecuteInlineToolAsync(
            fixture,
            "read_thread_history",
            $$"""{"threadId":"{{sourceThread.Id:D}}","maxMessages":10}""",
            agent.Id,
            currentChannel.Id);

        Assert.That(readResult, Does.Contain("hello from source channel"));
    }

    [Test]
    public async Task ContextDefaultAgentCountsAsChannelAssignmentForCrossThreadDiscovery()
    {
        await using var fixture = AoContextFixture.Create();
        var agent = fixture.SeedAgentWithCrossThreadRole(PermissionClearance.Independent);
        var context = MakeContext("Shared Context", agent.Id);
        var currentChannel = MakeChannel("Current Channel", agent.Id);
        var sourceChannel = MakeChannel("Context Source Channel", agentId: null);
        sourceChannel.AgentContextId = context.Id;
        sourceChannel.AgentContext = context;
        var sourceThread = MakeThread("Context Source Thread", sourceChannel.Id);

        fixture.Db.AgentContexts.Add(context);
        fixture.Db.Channels.AddRange(currentChannel, sourceChannel);
        fixture.Db.ChatThreads.Add(sourceThread);
        await fixture.Db.SaveChangesAsync();

        var result = await ExecuteInlineToolAsync(
            fixture, "list_accessible_threads", "{}", agent.Id, currentChannel.Id);

        Assert.That(result, Does.Contain(sourceThread.Id.ToString("D")));
        Assert.That(result, Does.Contain("Context Source Channel"));
    }

    [Test]
    public async Task NonIndependentCrossThreadDiscoveryRequiresSourceChannelOptIn()
    {
        await using var fixture = AoContextFixture.Create();
        var agent = fixture.SeedAgentWithCrossThreadRole(PermissionClearance.ApprovedBySameLevelUser);
        var currentChannel = MakeChannel("Current Channel", agent.Id);
        var hiddenChannel = MakeChannel("Hidden Source Channel", agent.Id);
        var visibleChannel = MakeChannel("Opted In Source Channel", agent.Id);
        var visiblePermissionSet = MakeCrossThreadPermissionSet(
            PermissionClearance.ApprovedBySameLevelUser);
        visibleChannel.PermissionSetId = visiblePermissionSet.Id;
        visibleChannel.PermissionSet = visiblePermissionSet;

        var hiddenThread = MakeThread("Hidden Thread", hiddenChannel.Id);
        var visibleThread = MakeThread("Visible Thread", visibleChannel.Id);

        fixture.Db.PermissionSets.Add(visiblePermissionSet);
        fixture.Db.Channels.AddRange(currentChannel, hiddenChannel, visibleChannel);
        fixture.Db.ChatThreads.AddRange(hiddenThread, visibleThread);
        await fixture.Db.SaveChangesAsync();

        var result = await ExecuteInlineToolAsync(
            fixture, "list_accessible_threads", "{}", agent.Id, currentChannel.Id);

        Assert.That(result, Does.Contain(visibleThread.Id.ToString("D")));
        Assert.That(result, Does.Not.Contain(hiddenThread.Id.ToString("D")));
    }

    [Test]
    public async Task AllowedAgentCanListAndReadCrossThreadHistoryWhenPermissionAllows()
    {
        await using var fixture = AoContextFixture.Create();
        var agent = fixture.SeedAgentWithCrossThreadRole(PermissionClearance.Independent);
        var otherAgent = fixture.SeedAgent("Other Agent");
        var currentChannel = MakeChannel("Current Channel", agent.Id);
        var sourceChannel = MakeChannel("Allowed Source Channel", otherAgent.Id);
        sourceChannel.AllowedAgents.Add(agent);
        var sourceThread = MakeThread("Allowed Source Thread", sourceChannel.Id);
        var sourceMessage = MakeMessage(sourceChannel.Id, sourceThread.Id, "allowed-agent history");

        fixture.Db.Channels.AddRange(currentChannel, sourceChannel);
        fixture.Db.ChatThreads.Add(sourceThread);
        fixture.Db.ChatMessages.Add(sourceMessage);
        await fixture.Db.SaveChangesAsync();

        var listResult = await ExecuteInlineToolAsync(
            fixture, "list_accessible_threads", "{}", agent.Id, currentChannel.Id);
        var readResult = await ExecuteInlineToolAsync(
            fixture,
            "read_thread_history",
            $$"""{"threadId":"{{sourceThread.Id:D}}","maxMessages":10}""",
            agent.Id,
            currentChannel.Id);

        Assert.That(listResult, Does.Contain(sourceThread.Id.ToString("D")));
        Assert.That(readResult, Does.Contain("allowed-agent history"));
    }

    [Test]
    public async Task PrimaryAgentAndAllowedAgentsCanReadButUnassignedAgentCannot()
    {
        await using var fixture = AoContextFixture.Create();
        var primary = fixture.SeedAgentWithCrossThreadRole(PermissionClearance.Independent, "Primary");
        var allowed = fixture.SeedAgentWithCrossThreadRole(PermissionClearance.Independent, "Allowed");
        var unassigned = fixture.SeedAgentWithCrossThreadRole(PermissionClearance.Independent, "Unassigned");
        var current = MakeChannel("Current", primary.Id);
        var primarySource = MakeChannel("Primary Source", primary.Id);
        var allowedSource = MakeChannel("Allowed Source", primary.Id);
        allowedSource.AllowedAgents.Add(allowed);
        var primaryThread = MakeThread("Primary Thread", primarySource.Id);
        var allowedThread = MakeThread("Allowed Thread", allowedSource.Id);

        fixture.Db.Channels.AddRange(current, primarySource, allowedSource);
        fixture.Db.ChatThreads.AddRange(primaryThread, allowedThread);
        await fixture.Db.SaveChangesAsync();

        var primaryList = await ExecuteInlineToolAsync(
            fixture, "list_accessible_threads", "{}", primary.Id, current.Id);
        var allowedList = await ExecuteInlineToolAsync(
            fixture, "list_accessible_threads", "{}", allowed.Id, current.Id);
        var unassignedList = await ExecuteInlineToolAsync(
            fixture, "list_accessible_threads", "{}", unassigned.Id, current.Id);

        Assert.That(primaryList, Does.Contain(primaryThread.Id.ToString("D")));
        Assert.That(allowedList, Does.Contain(allowedThread.Id.ToString("D")));
        Assert.That(unassignedList, Does.Contain("No accessible threads"));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(10)]
    [TestCase(100)]
    [TestCase(1000)]
    public async Task ListAccessibleThreadsScalesAcrossThreadCounts(int threadCount)
    {
        await using var fixture = AoContextFixture.Create();
        var agent = fixture.SeedAgentWithCrossThreadRole(
            PermissionClearance.Independent,
            "Primary Agent");
        var current = MakeChannel("Current", agent.Id);
        var source = MakeChannel("Source", agent.Id);
        fixture.Db.Channels.AddRange(current, source);
        for (var i = 0; i < threadCount; i++)
            fixture.Db.ChatThreads.Add(MakeThread($"Thread {i:D4}", source.Id, i));
        await fixture.Db.SaveChangesAsync();

        var sw = Stopwatch.StartNew();
        var result = await ExecuteInlineToolAsync(
            fixture, "list_accessible_threads", "{}", agent.Id, current.Id);
        sw.Stop();

        if (threadCount == 0)
        {
            Assert.That(result, Does.Contain("No accessible threads"));
        }
        else
        {
            using var parsed = JsonDocument.Parse(result);
            Assert.That(parsed.RootElement.GetArrayLength(), Is.EqualTo(threadCount));
        }

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2_000));
    }

    [Test]
    public async Task DisabledAccessibleThreadsHeaderReturnsEmptyButExplicitToolsStillWork()
    {
        await using var fixture = AoContextFixture.Create(new Dictionary<string, string?>
        {
            ["AgentOrchestration:DisableAccessibleThreadsHeader"] = "true",
        });
        var agent = fixture.SeedAgentWithCrossThreadRole(
            PermissionClearance.Independent,
            "Header Disabled Agent");
        var current = MakeChannel("Current", agent.Id);
        var source = MakeChannel("Source", agent.Id);
        var thread = MakeThread("Visible Thread", source.Id);
        fixture.Db.Channels.AddRange(current, source);
        fixture.Db.ChatThreads.Add(thread);
        await fixture.Db.SaveChangesAsync();

        var tag = fixture.Module.GetHeaderTags()!.Single(t => t.Name == "accessible-threads");
        var header = await tag.ResolveWithContext!(
            fixture.Services,
            new ModuleHeaderTagContext(
                current.Id,
                current.Title,
                agent.Id,
                agent.Name,
                "api",
                UserId: null,
                CompletionParameters: null,
                ProviderKey: "test"),
            default);
        var explicitList = await ExecuteInlineToolAsync(
            fixture, "list_accessible_threads", "{}", agent.Id, current.Id);

        Assert.That(header, Is.EqualTo(""));
        Assert.That(explicitList, Does.Contain(thread.Id.ToString("D")));
    }

    [Test]
    public async Task ReadThreadHistoryReturnsStableErrorsForMissingMalformedAndDeniedThreadArgs()
    {
        await using var fixture = AoContextFixture.Create();
        var agent = fixture.SeedAgentWithCrossThreadRole(PermissionClearance.Independent);
        var current = MakeChannel("Current", agent.Id);
        var source = MakeChannel("Source", agent.Id);
        var denied = MakeThread("Denied", source.Id);
        fixture.Db.Channels.AddRange(current, source);
        fixture.Db.ChatThreads.Add(denied);
        await fixture.Db.SaveChangesAsync();

        var missing = await ExecuteInlineToolAsync(
            fixture, "read_thread_history", "{}", agent.Id, current.Id);
        var malformed = await ExecuteInlineToolAsync(
            fixture, "read_thread_history", """{"threadId":"not-a-guid"}""", agent.Id, current.Id);
        var inaccessible = await ExecuteInlineToolAsync(
            fixture,
            "read_thread_history",
            $$"""{"threadId":"{{Guid.NewGuid():D}}"}""",
            agent.Id,
            current.Id);

        Assert.That(missing, Is.EqualTo("Error: threadId is required."));
        Assert.That(malformed, Is.EqualTo("Error: threadId is required."));
        Assert.That(inaccessible, Is.EqualTo("Error: thread not found or not accessible to this agent."));
    }

    [Test]
    public async Task ReadThreadHistoryClampsLargeReadsToTwoHundredNewestMessages()
    {
        await using var fixture = AoContextFixture.Create();
        var agent = fixture.SeedAgentWithCrossThreadRole(
            PermissionClearance.Independent,
            "History Agent");
        var current = MakeChannel("Current", agent.Id);
        var source = MakeChannel("Source", agent.Id);
        var thread = MakeThread("History", source.Id);
        fixture.Db.Channels.AddRange(current, source);
        fixture.Db.ChatThreads.Add(thread);
        for (var i = 0; i < 500; i++)
            fixture.Db.ChatMessages.Add(MakeMessage(source.Id, thread.Id, $"message-{i:D4}", i));
        await fixture.Db.SaveChangesAsync();

        var result = await ExecuteInlineToolAsync(
            fixture,
            "read_thread_history",
            $$"""{"threadId":"{{thread.Id:D}}","maxMessages":1000}""",
            agent.Id,
            current.Id);

        using var parsed = JsonDocument.Parse(result);
        var messages = parsed.RootElement.EnumerateArray().ToArray();
        Assert.That(messages, Has.Length.EqualTo(200));
        Assert.That(messages.First().GetProperty("content").GetString(), Is.EqualTo("message-0300"));
        Assert.That(messages.Last().GetProperty("content").GetString(), Is.EqualTo("message-0499"));
    }

    [Test]
    public async Task ReadThreadHistoryReportsEmptyThreadSeparatelyFromDeniedThread()
    {
        await using var fixture = AoContextFixture.Create();
        var agent = fixture.SeedAgentWithCrossThreadRole(PermissionClearance.Independent);
        var current = MakeChannel("Current", agent.Id);
        var source = MakeChannel("Source", agent.Id);
        var thread = MakeThread("Empty", source.Id);
        fixture.Db.Channels.AddRange(current, source);
        fixture.Db.ChatThreads.Add(thread);
        await fixture.Db.SaveChangesAsync();

        var result = await ExecuteInlineToolAsync(
            fixture,
            "read_thread_history",
            $$"""{"threadId":"{{thread.Id:D}}","maxMessages":10}""",
            agent.Id,
            current.Id);

        Assert.That(result, Is.EqualTo("Thread exists but has no messages."));
    }

    private static async Task<string> ExecuteInlineToolAsync(
        AoContextFixture fixture,
        string toolName,
        string json,
        Guid agentId,
        Guid channelId)
    {
        using var doc = JsonDocument.Parse(json);
        return await fixture.Module.ExecuteInlineToolAsync(
            toolName,
            doc.RootElement,
            new InlineToolContext(agentId, channelId, null, "call"),
            fixture.Services,
            default);
    }

    private static PermissionSetDB MakeCrossThreadPermissionSet(PermissionClearance clearance)
    {
        var permissionSet = new PermissionSetDB
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        permissionSet.GlobalFlags.Add(new GlobalFlagDB
        {
            Id = Guid.NewGuid(),
            FlagKey = ContextToolsPermissionKeys.CanReadCrossThreadHistory,
            Clearance = clearance,
            PermissionSetId = permissionSet.Id,
            PermissionSet = permissionSet,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return permissionSet;
    }

    private static ChannelDB MakeChannel(string title, Guid? agentId) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        AgentId = agentId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ChannelContextDB MakeContext(string name, Guid agentId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        AgentId = agentId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ChatThreadDB MakeThread(string name, Guid channelId, int offset = 0)
    {
        var timestamp = DateTimeOffset.UtcNow.AddMilliseconds(offset);
        return new ChatThreadDB
        {
            Id = Guid.NewGuid(),
            Name = name,
            ChannelId = channelId,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
    }

    private static ChatMessageDB MakeMessage(
        Guid channelId,
        Guid threadId,
        string content,
        int offset = 0)
    {
        var timestamp = DateTimeOffset.UtcNow.AddMilliseconds(offset);
        return new ChatMessageDB
        {
            Id = Guid.NewGuid(),
            Role = "user",
            Content = content,
            ChannelId = channelId,
            ThreadId = threadId,
            SenderUsername = "tester",
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
    }

    private sealed class AoContextFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;

        private AoContextFixture(ServiceProvider provider, AsyncServiceScope scope, AgentOrchestrationModule module)
        {
            _provider = provider;
            _scope = scope;
            Module = module;
        }

        public IServiceProvider Services => _scope.ServiceProvider;
        public TestSharpClawDataContext Db => Services.GetRequiredService<TestSharpClawDataContext>();
        public AgentOrchestrationModule Module { get; }

        public static AoContextFixture Create(IReadOnlyDictionary<string, string?>? settings = null)
        {
            var module = new AgentOrchestrationModule();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
                .Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddDbContext<TestSharpClawDataContext>(
                options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
            services.AddScoped<ISharpClawDataContext>(
                sp => sp.GetRequiredService<TestSharpClawDataContext>());
            services.AddScoped<IContextDataReader, ContextDataReader>();
            module.ConfigureServices(services);

            var provider = services.BuildServiceProvider();
            return new AoContextFixture(provider, provider.CreateAsyncScope(), module);
        }

        public AgentDB SeedAgentWithCrossThreadRole(
            PermissionClearance clearance,
            string name = "CrossThreadAgent")
        {
            var permissionSet = MakeCrossThreadPermissionSet(clearance);
            var role = new RoleDB
            {
                Id = Guid.NewGuid(),
                Name = name + " Role",
                PermissionSetId = permissionSet.Id,
                PermissionSet = permissionSet,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var agent = new AgentDB
            {
                Id = Guid.NewGuid(),
                Name = name,
                ModelId = Guid.NewGuid(),
                RoleId = role.Id,
                Role = role,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            Db.PermissionSets.Add(permissionSet);
            Db.Roles.Add(role);
            Db.Agents.Add(agent);
            return agent;
        }

        public AgentDB SeedAgent(string name)
        {
            var agent = new AgentDB
            {
                Id = Guid.NewGuid(),
                Name = name,
                ModelId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            Db.Agents.Add(agent);
            return agent;
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    private sealed class TestSharpClawDataContext(DbContextOptions<TestSharpClawDataContext> options)
        : DbContext(options), ISharpClawDataContext
    {
        public DbSet<AgentDB> Agents => Set<AgentDB>();
        public DbSet<ChannelDB> Channels => Set<ChannelDB>();
        public DbSet<ChannelContextDB> AgentContexts => Set<ChannelContextDB>();
        public DbSet<ChatThreadDB> ChatThreads => Set<ChatThreadDB>();
        public DbSet<ChatMessageDB> ChatMessages => Set<ChatMessageDB>();
        public DbSet<PermissionSetDB> PermissionSets => Set<PermissionSetDB>();
        public DbSet<GlobalFlagDB> GlobalFlags => Set<GlobalFlagDB>();
        public DbSet<RoleDB> Roles => Set<RoleDB>();

        IQueryable<AgentDB> ISharpClawDataContext.Agents => Agents;
        IQueryable<ChannelDB> ISharpClawDataContext.Channels => Channels;
        IQueryable<ChannelContextDB> ISharpClawDataContext.AgentContexts => AgentContexts;
        IQueryable<ChatThreadDB> ISharpClawDataContext.ChatThreads => ChatThreads;
        IQueryable<ChatMessageDB> ISharpClawDataContext.ChatMessages => ChatMessages;
        IQueryable<PermissionSetDB> ISharpClawDataContext.PermissionSets => PermissionSets;
        IQueryable<GlobalFlagDB> ISharpClawDataContext.GlobalFlags => GlobalFlags;
        IQueryable<RoleDB> ISharpClawDataContext.Roles => Roles;

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<AgentDB>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.Model);
                entity.Ignore(e => e.ToolAwarenessSet);
                entity.Ignore(e => e.ProviderParameters);
                entity.Ignore(e => e.ResponseFormat);
                entity.Ignore(e => e.Stop);
                entity.Ignore(e => e.Contexts);
                entity.Ignore(e => e.Channels);
                entity.HasOne(e => e.Role).WithMany().HasForeignKey(e => e.RoleId);
            });

            model.Entity<RoleDB>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.Users);
                entity.HasOne(e => e.PermissionSet).WithMany().HasForeignKey(e => e.PermissionSetId);
            });

            model.Entity<PermissionSetDB>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.ResourceAccesses);
                entity.Ignore(e => e.ClearanceUserWhitelist);
                entity.Ignore(e => e.ClearanceAgentWhitelist);
                entity.HasMany(e => e.GlobalFlags).WithOne(e => e.PermissionSet).HasForeignKey(e => e.PermissionSetId);
            });

            model.Entity<GlobalFlagDB>(entity => entity.HasKey(e => e.Id));

            model.Entity<ChannelDB>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.Agent);
                entity.Ignore(e => e.DefaultResourceSet);
                entity.Ignore(e => e.ToolAwarenessSet);
                entity.Ignore(e => e.ChatMessages);
                entity.Ignore(e => e.Threads);
                entity.HasOne(e => e.PermissionSet).WithMany().HasForeignKey(e => e.PermissionSetId);
                entity.HasOne(e => e.AgentContext).WithMany(e => e.Channels).HasForeignKey(e => e.AgentContextId);
                entity.HasMany(e => e.AllowedAgents).WithMany(e => e.AllowedChannels);
            });

            model.Entity<ChannelContextDB>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.Agent);
                entity.Ignore(e => e.DefaultResourceSet);
                entity.HasOne(e => e.PermissionSet).WithMany().HasForeignKey(e => e.PermissionSetId);
                entity.HasMany(e => e.AllowedAgents).WithMany(e => e.AllowedContexts);
            });

            model.Entity<ChatThreadDB>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.Channel);
                entity.Ignore(e => e.ChatMessages);
            });

            model.Entity<ChatMessageDB>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.Channel);
                entity.Ignore(e => e.Thread);
            });
        }
    }
}

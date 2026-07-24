using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Shiny;

namespace GitHubShine;

/// <summary>
/// Shiny.Json registration for the types Shiny.Mediator's <c>StorageCacheService</c> — i.e.
/// <c>AddMauiPersistentCache</c> behind <c>[Cache]</c> — serialises to persist repo snapshots across
/// launches: the cached value (<see cref="RepoSnapshot"/> and its nested records) plus the cache's
/// own key-index dictionary. It's a normal source-generated <see cref="JsonSerializerContext"/>; the
/// extra <c>[ShinyJsonContext]</c> registers it in the global resolver the cache service reads from.
/// </summary>
[ShinyJsonContext]
[JsonSerializable(typeof(RepoSnapshot))]
[JsonSerializable(typeof(PullRequestSummary))]
[JsonSerializable(typeof(WorkflowRunSummary))]
[JsonSerializable(typeof(MonitoredRepo))]
[JsonSerializable(typeof(ConcurrentDictionary<string, ConcurrentDictionary<string, string>>))]
internal sealed partial class GitHubShineCacheJsonContext : JsonSerializerContext;

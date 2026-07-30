using System.Text.Json.Serialization;

namespace GitHubShine;

/// <summary>
/// The single JSON serializer context for the app. Wired into DocumentDb via
/// AddDocumentStore in MauiProgram — store call sites never pass JsonTypeInfo.
/// </summary>
[JsonSerializable(typeof(MonitoredAccount))]
[JsonSerializable(typeof(MonitoredRepo))]
[JsonSerializable(typeof(StoredToken))]
[JsonSerializable(typeof(SeenFailedRun))]
[JsonSerializable(typeof(SeenInboxItem))]
[JsonSerializable(typeof(DashboardPrefs))]
[JsonSerializable(typeof(SyncMapping))]
[JsonSerializable(typeof(AutoSyncPrefs))]
internal sealed partial class GitHubShineJsonContext : JsonSerializerContext;

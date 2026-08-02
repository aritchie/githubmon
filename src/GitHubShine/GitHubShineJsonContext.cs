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
[JsonSerializable(typeof(SeenWorkflowState))]
[JsonSerializable(typeof(DashboardPrefs))]
[JsonSerializable(typeof(NotificationPrefs))]
[JsonSerializable(typeof(SyncMapping))]
[JsonSerializable(typeof(AutoSyncPrefs))]
[JsonSerializable(typeof(PollState))]
[JsonSerializable(typeof(FollowedPerson))]
internal sealed partial class GitHubShineJsonContext : JsonSerializerContext;

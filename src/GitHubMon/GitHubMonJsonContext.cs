using System.Text.Json.Serialization;

namespace GitHubMon;

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
internal sealed partial class GitHubMonJsonContext : JsonSerializerContext;

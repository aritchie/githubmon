using System.Text.Json.Serialization;

namespace GitHubMon.Dashboard;

[JsonSerializable(typeof(SeenFailedRun))]
[JsonSerializable(typeof(SeenInboxItem))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;

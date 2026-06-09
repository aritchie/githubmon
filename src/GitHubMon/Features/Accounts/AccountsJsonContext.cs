using System.Text.Json.Serialization;

namespace GitHubMon.Accounts;

[JsonSerializable(typeof(MonitoredAccount))]
[JsonSerializable(typeof(MonitoredRepo))]
[JsonSerializable(typeof(StoredToken))]
internal sealed partial class AccountsJsonContext : JsonSerializerContext;

using System.Text.Json;
using System.Text.Json.Serialization;
using Postern.Core.Audit;
using Postern.Core.RateLimiting;

namespace Postern.Broker.Http;

// The wire contract of /_postern/*. Postern.Client mirrors these shapes; keep them in step.

internal sealed record RegisterRequest(string Name, string[] Scopes);

internal sealed record RegisterResponse(string Id, string Claim, string Status);

internal sealed record ClaimRequest(string Claim);

internal sealed record ClaimResponse(string Status, string? Token, string Detail);

internal sealed record AppStatus(string Id, string Name, string Status, string[] Scopes);

internal sealed record StatusResponse(string Version, string Pipe, int ProcessId, string[] Providers, AppStatus? App);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ProblemBody))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(RegisterResponse))]
[JsonSerializable(typeof(ClaimRequest))]
[JsonSerializable(typeof(ClaimResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(IReadOnlyList<RateLimitSnapshot>))]
[JsonSerializable(typeof(IReadOnlyList<AuditEntry>))]
internal sealed partial class BrokerJson : JsonSerializerContext;

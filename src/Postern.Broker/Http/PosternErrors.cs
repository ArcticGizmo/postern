using Microsoft.AspNetCore.Http;

namespace Postern.Broker.Http;

/// <summary>
/// Errors Postern raises itself: problem+json with a Postern-Error header, so a client can tell "Postern said no" from
/// "the provider said no" without parsing the body.
/// </summary>
internal static class PosternErrors
{
    public const string Header = "Postern-Error";

    public static Task Write(HttpContext context, int status, string code, string detail)
    {
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.Headers[Header] = code;
        return context.Response.WriteAsJsonAsync(
            new ProblemBody($"urn:postern:error:{code}", code, status, detail),
            BrokerJson.Default.ProblemBody,
            contentType: "application/problem+json");
    }
}

internal sealed record ProblemBody(string Type, string Title, int Status, string Detail);

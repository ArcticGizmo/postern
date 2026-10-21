namespace Spike.Broker;

/// <summary>
/// Errors Postern raises itself. Always problem+json plus a Postern-Error header, so a client can tell
/// "Postern said no" from "the provider said no" without parsing the body.
/// </summary>
internal static class PosternErrors
{
    public static Task Write(HttpContext context, int status, string code, string detail)
    {
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.Headers["Postern-Error"] = code;
        return context.Response.WriteAsJsonAsync(
            new { type = $"urn:postern:error:{code}", title = code, status, detail },
            options: null,
            contentType: "application/problem+json");
    }
}

using Microsoft.AspNetCore.Http;
using Postern.Broker.Identity;
using Postern.Core.Apps;

namespace Postern.Broker.Http;

/// <summary>Runs before every endpoint: the request must be addressed to "postern" and come from an identifiable process.</summary>
internal sealed class BrokerMiddleware(CallerResolver resolver) : IMiddleware
{
    const string CallerKey = "postern.caller";

    public static CallerInfo Caller(HttpContext context) => (CallerInfo)context.Items[CallerKey]!;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // A different host means the request reached the pipe by accident: typically a client HttpClient that followed
        // a redirect, since its pipe ConnectCallback dials Postern whatever the URI's host is.
        if (!string.Equals(context.Request.Host.Host, "postern", StringComparison.OrdinalIgnoreCase))
        {
            await PosternErrors.Write(context, 421, "misdirected-host",
                $"Host '{context.Request.Host}' is not postern. Turn off automatic redirects in the client.");
            return;
        }

        try
        {
            context.Items[CallerKey] = resolver.Resolve(context);
        }
        catch (Exception ex)
        {
            await PosternErrors.Write(context, 403, "caller-unidentified", ex.Message);
            return;
        }

        await next(context);
    }
}

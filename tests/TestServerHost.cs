using Microsoft.AspNetCore.Builder;
using Sockseek.Server;

namespace Tests;

internal static class TestServerHost
{
    public static WebApplication Build(string[] args, ServerOptions? options = null, string? url = null)
        // Both the host logger and the core bridge otherwise install console outputs.
        => ServerHost.Build(args, options, url, logToConsole: false);
}

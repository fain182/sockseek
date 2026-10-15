using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;

namespace Tests.Server;

[TestClass]
public class TestServerHostTests
{
    [TestMethod]
    public async Task Build_DoesNotWriteCoreOrHostLogsToConsole()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        SockseekLog.RemoveNonFileOutputs();
        try
        {
            Console.SetOut(output);
            Console.SetError(output);
            await using (var app = TestServerHost.Build([]))
            {
                SockseekLog.Jobs.Info("core log must not escape");
                app.Logger.LogInformation("host log must not escape");
            }

            Assert.AreEqual("", output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            SockseekLog.RemoveNonFileOutputs();
        }
    }
}

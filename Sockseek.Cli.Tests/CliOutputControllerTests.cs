using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core;

namespace Tests.Cli;

[TestClass]
public class CliOutputControllerTests
{
    [TestMethod]
    public void Dispose_DetachesOnlyItsOwnConsoleSink()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var console = new StringWriter();
        var otherMessages = new List<string>();
        SockseekLog.RemoveNonFileOutputs();
        try
        {
            Console.SetOut(console);
            Console.SetError(console);
            using var output = CliOutputController.Install([]);
            SockseekLog.AddStructuredConsoleSink((entry, _) => otherMessages.Add(entry.Message));

            SockseekLog.Info("before disposal");
            StringAssert.Contains(console.ToString(), "before disposal");
            console.GetStringBuilder().Clear();

            output.Dispose();
            output.Dispose();
            SockseekLog.Info("after disposal");

            Assert.AreEqual("", console.ToString(), "A disposed controller must not print later logs.");
            CollectionAssert.AreEqual(new[] { "before disposal", "after disposal" }, otherMessages);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            SockseekLog.RemoveNonFileOutputs();
        }
    }
}

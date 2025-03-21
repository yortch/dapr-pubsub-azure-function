using Azure.Messaging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Dapr;
using Microsoft.Extensions.Logging;

public class DaprConsumeTopicMessage
{
    /// <summary>
    /// Sample to use Dapr Publish trigger to print any new message arrived on the subscribed topic.
    /// </summary>
    [Function("ConsumeTopicMessage")]
    public static void Run(
        [DaprTopicTrigger("%PubSubName%", Topic = "a")] string message, FunctionContext functionContext)
    {
        var log = functionContext.GetLogger("PrintTopicMessage");
        log.LogInformation("C# function processed a ConsumeTopicMessage request from the Dapr Runtime.");
        log.LogInformation($"Topic received a message: {message}.");
    }
}
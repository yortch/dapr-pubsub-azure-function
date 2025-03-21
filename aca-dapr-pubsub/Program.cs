using System;
using Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();
app.UseCloudEvents();

using var client = new DaprClientBuilder().Build();

app.MapPost("/publish", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var requestBody = await reader.ReadToEndAsync();

    //publish new MessageEvent to the topic
    var messageEvent = new MessageEvent("OrderCreated", requestBody);
    var topicName = "a";
    await client.PublishEventAsync("messagebus", topicName, messageEvent);
    Console.WriteLine("Published MessageEvent Type: " + messageEvent.MessageType 
        + " " + messageEvent.Message + " to Topic: " + topicName);
    return Results.Ok();
});

app.Run();
internal record MessageEvent(string MessageType, string Message);
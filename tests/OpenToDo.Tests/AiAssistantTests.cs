using OpenToDo.Core;
using OpenToDo.Plugins.Ai;
using Xunit;

namespace OpenToDo.Tests;

public sealed class AiAssistantTests
{
    [Fact]
    public void Parse_ReadsMessageAndActions()
    {
        var reply = AiAssistant.Parse(
            """
            {"message":"All set!","actions":[{"op":"add","title":"Pay rent","due":"tomorrow"},{"op":"complete","match":"report"}]}
            """);

        Assert.Equal("All set!", reply.Message);
        Assert.Equal(2, reply.Commands.Count);
        Assert.Equal(TaskCommandKind.Add, reply.Commands[0].Kind);
        Assert.Equal("Pay rent", reply.Commands[0].Title);
        Assert.Equal("tomorrow", reply.Commands[0].Due);
        Assert.Equal(TaskCommandKind.Complete, reply.Commands[1].Kind);
        Assert.Equal("report", reply.Commands[1].Match);
    }

    [Fact]
    public void Parse_ExtractsJsonFromSurroundingProse()
    {
        var reply = AiAssistant.Parse("Sure thing.\n{\"message\":\"Added it\",\"actions\":[]}\nAnything else?");

        Assert.Equal("Added it", reply.Message);
        Assert.Empty(reply.Commands);
    }

    [Fact]
    public void Parse_PlainTextFallsBackToTheRawMessage()
    {
        var reply = AiAssistant.Parse("You're doing great — three tasks done today.");

        Assert.Equal("You're doing great — three tasks done today.", reply.Message);
        Assert.Empty(reply.Commands);
    }

    [Fact]
    public void Parse_IgnoresUnknownOperations()
    {
        var reply = AiAssistant.Parse("""{"message":"ok","actions":[{"op":"explode"}]}""");

        Assert.Equal("ok", reply.Message);
        Assert.Empty(reply.Commands);
    }

    [Fact]
    public void Parse_HandlesEmptyInput()
    {
        var reply = AiAssistant.Parse("   ");

        Assert.False(string.IsNullOrWhiteSpace(reply.Message));
        Assert.Empty(reply.Commands);
    }

    [Fact]
    public void Parse_TreatsDeleteOpAsDelete()
    {
        var reply = AiAssistant.Parse("""{"message":"Removed","actions":[{"op":"delete","match":"old note"}]}""");

        var command = Assert.Single(reply.Commands);
        Assert.Equal(TaskCommandKind.Delete, command.Kind);
        Assert.Equal("old note", command.Match);
    }
}

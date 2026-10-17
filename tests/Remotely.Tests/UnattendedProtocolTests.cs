using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Remotely.Unattended;

namespace Remotely.Tests;

/// <summary>The pipe between the app and the host service (agent A13): framing, JSON, and the client's refusal to trust a program that merely owns the pipe name.</summary>
public class UnattendedProtocolTests
{
    private static MemoryStream Frame(byte[] payload)
    {
        var bytes = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)payload.Length);
        payload.CopyTo(bytes, 4);
        return new MemoryStream(bytes);
    }

    [Fact]
    public async Task A_message_survives_framing_and_JSON()
    {
        var reply = new PipeReply
        {
            Ok = true,
            Status = new UnattendedStatus { Listening = true, Token = "K7P4-4NX2-QMH9", Port = 47810, CaptureDesktop = "Winlogon", Question = new ConsentQuestion { Id = "abc", ControllerName = "Dad's laptop" } },
        };
        using var stream = new MemoryStream();
        await PipeFraming.WriteAsync(stream, PipeJson.Encode(reply), CancellationToken.None);
        stream.Position = 0;
        var back = PipeJson.Decode<PipeReply>((await PipeFraming.ReadAsync(stream, CancellationToken.None))!);
        Assert.True(back.Ok);
        Assert.Equal("K7P4-4NX2-QMH9", back.Status!.Token);
        Assert.Equal("Winlogon", back.Status.CaptureDesktop);
        Assert.Equal("Dad's laptop", back.Status.Question!.ControllerName);
        Assert.Equal(UnattendedPipe.Version, back.Status.Version);
    }

    [Fact]
    public async Task A_closed_pipe_between_messages_is_a_clean_end_but_a_cut_message_is_an_error()
    {
        Assert.Null(await PipeFraming.ReadAsync(new MemoryStream(), CancellationToken.None));
        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeFraming.ReadAsync(new MemoryStream([0, 0]), CancellationToken.None));
        var cut = Frame(new byte[10]).ToArray()[..8];
        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => PipeFraming.ReadAsync(new MemoryStream(cut), CancellationToken.None));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u + 16 * 1024)]
    [InlineData(uint.MaxValue)]
    public async Task A_length_of_zero_or_above_the_limit_is_refused_before_anything_is_allocated_or_read(uint announced)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, announced);
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFraming.ReadAsync(new MemoryStream(header), CancellationToken.None));
    }

    [Fact]
    public async Task The_largest_allowed_message_goes_through_and_one_byte_more_cannot_be_written()
    {
        using var stream = new MemoryStream();
        await PipeFraming.WriteAsync(stream, new byte[UnattendedPipe.MaxMessageBytes], CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFraming.WriteAsync(stream, new byte[UnattendedPipe.MaxMessageBytes + 1], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFraming.WriteAsync(stream, [], CancellationToken.None));
    }

    [Fact]
    public void Hostile_bytes_are_either_a_request_or_an_InvalidDataException_and_nothing_else()
    {
        var random = new Random(13);
        for (var i = 0; i < 2000; i++)
        {
            var junk = new byte[random.Next(1, 300)];
            random.NextBytes(junk);
            try { PipeJson.Decode<PipeRequest>(junk); } catch (InvalidDataException) { }
        }
        foreach (var text in new[] { "null", "[]", "42", "\"x\"", "{\"cmd\":5}", "{\"cmd\":[1]}", "{" })
        {
            try { PipeJson.Decode<PipeRequest>(Encoding.UTF8.GetBytes(text)); } catch (InvalidDataException) { }
        }
    }

    [Fact]
    public void Missing_or_extra_fields_in_a_request_are_harmless()
    {
        var request = PipeJson.Decode<PipeRequest>("""{"cmd":"status","somethingNew":{"a":1}}"""u8);
        Assert.Equal(PipeCommands.Status, request.Cmd);
        Assert.Null(request.QuestionId);
        Assert.Equal("", PipeJson.Decode<PipeRequest>("{}"u8).Cmd);
    }

    [Fact]
    public void Absent_values_are_left_out_of_the_JSON_so_a_status_without_a_token_cannot_leak_one()
    {
        var json = Encoding.UTF8.GetString(PipeJson.Encode(new UnattendedStatus()));
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("null", json);
    }

    // ---- the client over a real pipe (Linux uses a Unix socket, Windows a named pipe: the framing and the client logic are the same) ----

    private static string NewPipeName() => "rmt-unattended-test-" + Guid.NewGuid().ToString("N");

    private static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static NamedPipeClientStream Client(string name) => new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

    [Fact]
    public async Task The_client_asks_and_gets_the_answer_and_can_ask_again_on_the_same_connection()
    {
        var name = NewPipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var served = Task.Run(async () =>
        {
            await using var server = Server(name);
            await server.WaitForConnectionAsync(cts.Token);
            for (var i = 0; i < 2; i++)
            {
                var request = PipeJson.Decode<PipeRequest>((await PipeFraming.ReadAsync(server, cts.Token))!);
                await PipeFraming.WriteAsync(server, PipeJson.Encode(new PipeReply { Ok = true, Error = request.Cmd + i }), cts.Token);
            }
        });
        using var client = new UnattendedPipeClient(() => Client(name));
        Assert.Equal("status0", (await client.CallAsync(new PipeRequest { Cmd = "status" }, cts.Token)).Error);
        Assert.Equal("start1", (await client.CallAsync(new PipeRequest { Cmd = "start" }, cts.Token)).Error);
        await served;
    }

    [Fact]
    public async Task Nothing_is_sent_to_a_program_that_is_not_the_service()
    {
        var name = NewPipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var impostor = Server(name);
        var listening = impostor.WaitForConnectionAsync(cts.Token);
        using var client = new UnattendedPipeClient(() => Client(name), _ => "not the service");

        var ex = await Assert.ThrowsAsync<UnattendedPipeException>(() => client.CallAsync(new PipeRequest { Cmd = PipeCommands.Status }, cts.Token));
        Assert.Equal(PipeProblem.UntrustedServer, ex.Problem);
        await listening;   // it connected ...
        var buffer = new byte[16];
        using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var read = 0;
        try { read = await impostor.ReadAsync(buffer, brief.Token); } catch (Exception e) when (e is OperationCanceledException or IOException) { }
        Assert.Equal(0, read);   // ... and received no request
    }

    [Fact]
    public async Task No_pipe_means_unreachable_not_a_crash()
    {
        using var client = new UnattendedPipeClient(() => Client(NewPipeName()));
        var ex = await Assert.ThrowsAsync<UnattendedPipeException>(() => client.CallAsync(new PipeRequest { Cmd = PipeCommands.Status }, CancellationToken.None));
        Assert.Equal(PipeProblem.Unreachable, ex.Problem);
    }

    [Fact]
    public async Task A_reply_that_is_not_a_reply_is_reported_as_bad_and_the_client_recovers_on_the_next_call()
    {
        var name = NewPipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var firstAnswered = new TaskCompletionSource();
        var secondListening = new TaskCompletionSource();
        var served = Task.Run(async () =>
        {
            await using (var first = Server(name))
            {
                await first.WaitForConnectionAsync(cts.Token);
                await PipeFraming.ReadAsync(first, cts.Token);
                await PipeFraming.WriteAsync(first, Encoding.UTF8.GetBytes("this is not json"), cts.Token);
                await firstAnswered.Task.WaitAsync(cts.Token);   // keep it open until the client has read the junk
            }
            await using var second = Server(name);
            var connected = second.WaitForConnectionAsync(cts.Token);
            secondListening.SetResult();
            await connected;
            await PipeFraming.ReadAsync(second, cts.Token);
            await PipeFraming.WriteAsync(second, PipeJson.Encode(new PipeReply { Ok = true }), cts.Token);
            await Task.Delay(200, cts.Token);
        });
        using var client = new UnattendedPipeClient(() => Client(name));
        var ex = await Assert.ThrowsAsync<UnattendedPipeException>(() => client.CallAsync(new PipeRequest { Cmd = "status" }, cts.Token));
        Assert.Equal(PipeProblem.BadReply, ex.Problem);
        firstAnswered.SetResult();
        await secondListening.Task.WaitAsync(cts.Token);
        Assert.True((await client.CallAsync(new PipeRequest { Cmd = "status" }, cts.Token)).Ok);
        await served;
    }
}

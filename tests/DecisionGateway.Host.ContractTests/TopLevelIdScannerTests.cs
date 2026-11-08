using System.Buffers;
using System.Text;
using AwesomeAssertions;
using DecisionGateway.Host.Transport;
using ModelContextProtocol.Protocol;

namespace DecisionGateway.Host.ContractTests;

/// <summary>Incremental search for the top-level id of a rejected frame.</summary>
public sealed class TopLevelIdScannerTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("""{"jsonrpc":"2.0","id":7,"method":"ping"}""", 7L)]
    [InlineData("""{"jsonrpc":"2.0","method":"tools/call","params":{"id":1,"a":[{"id":2}],"s":"\"id\":3"},"id":42}""", 42L)]
    [InlineData("""  {  "id"  :  -5  }""", -5L)]
    [InlineData("""{"id":8}""", 8L)]
    [InlineData("""{"x":true,"y":null,"z":1.5,"id":9}""", 9L)]
    [InlineData("""{"jsonrpc":"2.0","id":11""", 11L)]
    public void FindsNumericId(string json, long expected)
    {
        Scan(json, chunk: int.MaxValue).Should().Be(new RequestId(expected));
        Scan(json, chunk: 1).Should().Be(new RequestId(expected), "the result must not depend on chunk boundaries");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("""{"params":{"k":"v\\\"}"},"id":"abc"}""", "abc")]
    [InlineData("""{"id":"a\"b"}""", "a\"b")]
    public void FindsStringId(string json, string expected)
    {
        Scan(json, chunk: int.MaxValue).Should().Be(new RequestId(expected));
        Scan(json, chunk: 1).Should().Be(new RequestId(expected));
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")]
    [InlineData("""{"params":{"id":1}}""")]
    [InlineData("""{"id":{"x":1}}""")]
    [InlineData("""{"id":null}""")]
    [InlineData("""[{"id":1}]""")]
    [InlineData("﻿{\"id\":1}")]
    [InlineData("""{"jsonrpc":"2.0","params":{"image_payload":"AAAA""")]
    public void ReturnsNotFound(string json)
    {
        Scan(json, chunk: int.MaxValue).Should().BeNull();
        Scan(json, chunk: 3).Should().BeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void FindsIdAfterMultiMegabyteArgument_AcrossManySegments()
    {
        var json = """{"jsonrpc":"2.0","method":"tools/call","params":{"name":"evaluate_decision","arguments":{"image_payload":"""
            + "\"" + new string('+', 3_000_000) + "\"}},\"id\":123}";
        Scan(json, chunk: 4093).Should().Be(new RequestId(123));
    }

    [Fact, Trait("Category", "Unit")]
    public void Reset_StartsANewSearch()
    {
        var scanner = new TopLevelIdScanner();
        scanner.Reset();
        scanner.Feed("""{"id":1}"""u8);
        scanner.TryGetId(out _).Should().BeTrue();

        scanner.Reset();
        scanner.Feed("""{"method":"x"}"""u8);
        scanner.TryGetId(out _).Should().BeFalse();
    }

    private static RequestId? Scan(string json, int chunk)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var scanner = new TopLevelIdScanner();
        scanner.Reset();
        if (chunk == int.MaxValue)
        {
            scanner.Feed(bytes);
        }
        else
        {
            scanner.Feed(ToSequence(bytes, chunk));
        }

        return scanner.TryGetId(out var id) ? id : null;
    }

    private static ReadOnlySequence<byte> ToSequence(byte[] bytes, int chunk)
    {
        Segment? first = null;
        Segment? last = null;
        foreach (var part in bytes.Chunk(chunk))
        {
            var segment = new Segment(part);
            if (last is null)
            {
                first = segment;
            }
            else
            {
                last.Append(segment);
            }

            last = segment;
        }

        return first is null ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public void Append(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}

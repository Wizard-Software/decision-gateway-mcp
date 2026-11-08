using System.Buffers;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace DecisionGateway.Host.Transport;

/// <summary>
/// Finds the top-level <c>id</c> of a JSON object that is fed in pieces, in constant memory. The transport uses it for a
/// frame it does not keep whole (over the size limit) and for a frame that failed to parse. The position of <c>id</c>
/// is not fixed: the SDK C# client writes it after <c>params</c>, so it can follow megabytes of argument text.
/// </summary>
/// <remarks>
/// Best effort, not a validator: the scanner tracks only object depth, strings and escapes, and anything it does not
/// expect at the top level ends the search with "not found". The first top-level <c>id</c> wins. A key or value longer
/// than the small capture buffers is treated as "not the id" or "not found".
/// </remarks>
internal sealed class TopLevelIdScanner
{
    /// <summary>A key longer than this (quotes included) cannot be <c>"id"</c>, even escaped.</summary>
    private const int MaxKeyBytes = 64;

    /// <summary>An id value longer than this is not recovered.</summary>
    private const int MaxIdValueBytes = 1024;

    private readonly byte[] _key = new byte[MaxKeyBytes];
    private readonly byte[] _idValue = new byte[MaxIdValueBytes];

    private State _state;
    private int _keyLength;
    private bool _keyOverflow;
    private bool _keyIsId;
    private int _idValueLength;
    private bool _escape;
    private bool _inString;
    private int _depth;
    private RequestId _id;
    private bool _found;

    private enum State
    {
        BeforeRoot,
        ExpectKey,
        InKey,
        ExpectColon,
        ExpectValue,
        InIdString,
        InIdLiteral,
        SkipValue,
        SkipLiteral,
        AfterValue,
        Done,
    }

    /// <summary>True once the id is found or the search has ended; further input is ignored.</summary>
    public bool IsComplete => _state == State.Done;

    /// <summary>Starts a new search.</summary>
    public void Reset()
    {
        _state = State.BeforeRoot;
        _keyLength = 0;
        _keyOverflow = false;
        _keyIsId = false;
        _idValueLength = 0;
        _escape = false;
        _inString = false;
        _depth = 0;
        _id = default;
        _found = false;
    }

    /// <summary>Feeds the next bytes of the frame.</summary>
    public void Feed(in ReadOnlySequence<byte> bytes)
    {
        foreach (var segment in bytes)
        {
            if (IsComplete)
            {
                return;
            }

            Feed(segment.Span);
        }
    }

    /// <summary>Feeds the next bytes of the frame.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        while (i < bytes.Length && _state != State.Done)
        {
            var b = bytes[i];
            switch (_state)
            {
                case State.BeforeRoot:
                    if (b == (byte)'{')
                    {
                        _state = State.ExpectKey;
                    }
                    else if (!IsWhiteSpace(b))
                    {
                        Stop();
                    }

                    i++;
                    break;

                case State.ExpectKey:
                    if (b == (byte)'"')
                    {
                        _key[0] = b;
                        _keyLength = 1;
                        _keyOverflow = false;
                        _escape = false;
                        _state = State.InKey;
                    }
                    else if (!IsWhiteSpace(b))
                    {
                        // '}' ends the object without an id; anything else is not a key.
                        Stop();
                    }

                    i++;
                    break;

                case State.InKey:
                    AppendKey(b);
                    if (_escape)
                    {
                        _escape = false;
                    }
                    else if (b == (byte)'\\')
                    {
                        _escape = true;
                    }
                    else if (b == (byte)'"')
                    {
                        _keyIsId = !_keyOverflow && IsIdKey(_key.AsSpan(0, _keyLength));
                        _state = State.ExpectColon;
                    }

                    i++;
                    break;

                case State.ExpectColon:
                    if (b == (byte)':')
                    {
                        _state = State.ExpectValue;
                    }
                    else if (!IsWhiteSpace(b))
                    {
                        Stop();
                    }

                    i++;
                    break;

                case State.ExpectValue:
                    i++;
                    if (IsWhiteSpace(b))
                    {
                        break;
                    }

                    if (_keyIsId)
                    {
                        if (b is (byte)'{' or (byte)'[')
                        {
                            // An object or array is not a valid JSON-RPC id.
                            Stop();
                            break;
                        }

                        _idValue[0] = b;
                        _idValueLength = 1;
                        _escape = false;
                        _state = b == (byte)'"' ? State.InIdString : State.InIdLiteral;
                        break;
                    }

                    _escape = false;
                    if (b == (byte)'"')
                    {
                        _inString = true;
                        _depth = 0;
                        _state = State.SkipValue;
                    }
                    else if (b is (byte)'{' or (byte)'[')
                    {
                        _inString = false;
                        _depth = 1;
                        _state = State.SkipValue;
                    }
                    else
                    {
                        _state = State.SkipLiteral;
                    }

                    break;

                case State.InIdString:
                    if (!AppendIdValue(b))
                    {
                        break;
                    }

                    if (_escape)
                    {
                        _escape = false;
                    }
                    else if (b == (byte)'\\')
                    {
                        _escape = true;
                    }
                    else if (b == (byte)'"')
                    {
                        FinishId();
                    }

                    i++;
                    break;

                case State.InIdLiteral:
                    if (b is (byte)',' or (byte)'}' or (byte)']' || IsWhiteSpace(b))
                    {
                        FinishId();
                        break;
                    }

                    if (AppendIdValue(b))
                    {
                        i++;
                    }

                    break;

                case State.SkipValue:
                    i = SkipValue(bytes, i);
                    break;

                case State.SkipLiteral:
                    if (b == (byte)',')
                    {
                        _state = State.ExpectKey;
                    }
                    else if (b == (byte)'}')
                    {
                        Stop();
                    }
                    else if (IsWhiteSpace(b))
                    {
                        _state = State.AfterValue;
                    }

                    i++;
                    break;

                case State.AfterValue:
                    if (b == (byte)',')
                    {
                        _state = State.ExpectKey;
                    }
                    else if (!IsWhiteSpace(b))
                    {
                        // '}' ends the object without an id; anything else is malformed.
                        Stop();
                    }

                    i++;
                    break;
            }
        }
    }

    /// <summary>Ends the input and returns the id, if one was found.</summary>
    public bool TryGetId(out RequestId id)
    {
        if (_state == State.InIdLiteral)
        {
            // A number at the very end of the input has no delimiter after it.
            FinishId();
        }

        id = _id;
        return _found;
    }

    /// <summary>
    /// Skips the body of a value that is not the id. Inside a string, jumps to the next quote or backslash with a
    /// vectorized search, so a multi-megabyte argument is not walked byte by byte.
    /// </summary>
    private int SkipValue(ReadOnlySpan<byte> bytes, int i)
    {
        while (i < bytes.Length)
        {
            if (_inString)
            {
                if (_escape)
                {
                    _escape = false;
                    i++;
                    continue;
                }

                var next = bytes[i..].IndexOfAny((byte)'"', (byte)'\\');
                if (next < 0)
                {
                    return bytes.Length;
                }

                i += next;
                if (bytes[i] == (byte)'\\')
                {
                    _escape = true;
                    i++;
                    continue;
                }

                // Closing quote.
                _inString = false;
                i++;
                if (_depth == 0)
                {
                    _state = State.AfterValue;
                    return i;
                }

                continue;
            }

            var b = bytes[i++];
            switch (b)
            {
                case (byte)'"':
                    _inString = true;
                    break;
                case (byte)'{' or (byte)'[':
                    _depth++;
                    break;
                case (byte)'}' or (byte)']':
                    _depth--;
                    if (_depth == 0)
                    {
                        _state = State.AfterValue;
                        return i;
                    }

                    break;
            }
        }

        return i;
    }

    private void AppendKey(byte b)
    {
        if (_keyLength < MaxKeyBytes)
        {
            _key[_keyLength++] = b;
        }
        else
        {
            _keyOverflow = true;
        }
    }

    private bool AppendIdValue(byte b)
    {
        if (_idValueLength >= MaxIdValueBytes)
        {
            Stop();
            return false;
        }

        _idValue[_idValueLength++] = b;
        return true;
    }

    private void FinishId()
    {
        _state = State.Done;
        try
        {
            var reader = new Utf8JsonReader(_idValue.AsSpan(0, _idValueLength), isFinalBlock: true, state: default);
            if (!reader.Read())
            {
                return;
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    _id = new RequestId(reader.GetString()!);
                    _found = true;
                    break;
                case JsonTokenType.Number when reader.TryGetInt64(out var number):
                    _id = new RequestId(number);
                    _found = true;
                    break;
            }
        }
        catch (JsonException)
        {
            // Not a valid string or number: no id.
        }
        catch (InvalidOperationException)
        {
            // A string with an invalid escape sequence: no id.
        }
    }

    private void Stop() => _state = State.Done;

    /// <summary>Compares a quoted key with <c>"id"</c>, unescaping it first (e.g. <c>"id"</c>).</summary>
    private static bool IsIdKey(ReadOnlySpan<byte> quotedKey)
    {
        if (quotedKey.SequenceEqual("\"id\""u8))
        {
            return true;
        }

        if (quotedKey.IndexOf((byte)'\\') < 0)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(quotedKey, isFinalBlock: true, state: default);
            return reader.Read() && reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("id"u8);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsWhiteSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}

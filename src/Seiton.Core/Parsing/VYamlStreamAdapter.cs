using System.Buffers.Text;
using VYaml.Parser;

namespace Seiton.Core.Parsing;

internal ref struct VYamlStreamAdapter : IYamlStreamReader
{
    private YamlParser _parser;
    private readonly Memory<byte> _source;
    private int _scalarSliceCursor;

    // Anchor/alias resolution state (all null/false until first anchor is encountered)
    private Dictionary<int, List<AnchorEvent>>? _anchorStore;    // anchor id → recorded events
    private List<AnchorEvent>? _currentRecording;                // non-null while inside an anchor
    private int _recordingId;                                    // anchor id being recorded
    private int _recordingDepth;                                 // nesting depth inside anchor
    private Queue<AnchorEvent>? _pendingReplays;                 // events queued for alias replay
    private bool _isReplaying;                                   // true when serving a virtual event
    private AnchorEvent _virtualCurrent;                         // current event when _isReplaying

    // Anchor usage tracking for unused-anchor detection
    private Dictionary<int, (string Name, TextPosition Position)>? _definedAnchors;  // anchor id → (name, position)
    private HashSet<int>? _referencedAnchorIds;                                       // anchor ids used by aliases
    private List<(string Name, TextPosition Position)>? _recursiveAliases;            // recursive alias occurrences
    private List<(int Id, List<AnchorEvent> Events, int Depth)>? _nestedRecordings;  // nested anchors inside outer recording

    /// <summary>Creates a new adapter wrapping the given UTF-8 YAML bytes for pull-based parsing.</summary>
    public VYamlStreamAdapter(Memory<byte> bytes)
    {
        _source = bytes;
        _scalarSliceCursor = 0;
        _parser = YamlParser.FromBytes(bytes);
    }

    public YamlEventKind CurrentKind =>
        _isReplaying ? _virtualCurrent.Kind : MapEventKind(_parser.CurrentEventType);

    public bool End =>
        (_pendingReplays == null || _pendingReplays.Count == 0) && !_isReplaying && _parser.End;

    public TextPosition CurrentStart
    {
        get
        {
            if (_isReplaying)
                return _virtualCurrent.Start;

            var mark = _parser.CurrentMark;
            // VYaml's CurrentMark for scalars advances past the token to the next token's
            // position rather than staying at the scalar itself. For empty/null scalars, use
            // the backward-scan helper. For non-empty scalars, locate the content in source
            // bytes by searching backward from the mark position.
            if (_parser.CurrentEventType == ParseEventType.Scalar)
            {
                if (_parser.IsNullScalar())
                {
                    // Implicit null — backward-scan from next token.
                    var correctedOffset = ResolveEmptyScalarStart(mark.Position);
                    return ComputeTextPositionFromOffset(_source.Span, correctedOffset);
                }

                if (_parser.GetScalarAsUtf8().Length == 0)
                {
                    var correctedOffset = ResolveEmptyScalarStart(mark.Position);
                    return ComputeTextPositionFromOffset(_source.Span, correctedOffset);
                }

                // Non-empty scalar: mark may point past the scalar. Search backward for the content.
                return ResolveNonEmptyScalarStart(mark.Position);
            }

            return new TextPosition(mark.Position, mark.Line, mark.Col);
        }
    }

    public TextPosition CurrentEnd => CurrentStart;

    public bool Read()
    {
        // Case 1: Pending replay events from alias resolution — serve the next one.
        if (_pendingReplays is { Count: > 0 })
        {
            _virtualCurrent = _pendingReplays.Dequeue();
            _isReplaying = true;
            // If inside a recording (alias-within-anchor), track depth.
            if (_currentRecording != null)
            {
                _currentRecording.Add(_virtualCurrent);
                ForwardToNestedRecordings(_virtualCurrent);
                TrackAnchorDepth(_virtualCurrent.Kind);
            }
            return true;
        }
        _isReplaying = false;

        if (!_parser.Read())
            return false;

        var eventType = _parser.CurrentEventType;

        // Case 2: Alias event — resolve and replay anchor events.
        if (eventType == ParseEventType.Alias)
        {
            if (_parser.TryGetCurrentAnchor(out var aliasAnchor)
                && _anchorStore != null
                && _anchorStore.TryGetValue(aliasAnchor.Id, out var snapshots)
                && snapshots.Count > 0)
            {
                // Track alias reference for unused-anchor detection
                _referencedAnchorIds ??= new HashSet<int>();
                _referencedAnchorIds.Add(aliasAnchor.Id);

                _pendingReplays ??= new Queue<AnchorEvent>();
                for (int i = 1; i < snapshots.Count; i++)
                    _pendingReplays.Enqueue(snapshots[i]);
                _virtualCurrent = snapshots[0];
                _isReplaying = true;
                // Record snap[0] if inside a recording.
                // snap[1..n] will be recorded individually in Case 1 as they are dequeued,
                // so we must NOT bulk-add them here (that would double-add them).
                if (_currentRecording != null)
                {
                    _currentRecording.Add(_virtualCurrent);
                    TrackAnchorDepth(_virtualCurrent.Kind);
                }
                return true;
            }
            // Unresolvable alias: surface as-is so the parser can emit an error.
            // When we reach this path and _currentRecording is active, the alias references an
            // anchor that hasn't finished recording — this is a recursive self-reference.
            // (Truly undefined anchors cause VYaml to throw before reaching here.)
            if (_currentRecording != null && _parser.TryGetCurrentAnchor(out var unresolvableAnchor))
            {
                _recursiveAliases ??= new List<(string, TextPosition)>();
                var mark = _parser.CurrentMark;
                _recursiveAliases.Add((unresolvableAnchor.Name.ToString(), new TextPosition(mark.Position, mark.Line, mark.Col)));
            }
            // Record an Alias placeholder into the current recording (if any) so that the
            // stored event sequence remains structurally complete and can be replayed correctly.
            if (_currentRecording != null)
            {
                _currentRecording.Add(new AnchorEvent { Kind = YamlEventKind.Alias });
                // Alias is a leaf node — _recordingDepth does not change.
            }
            return true;
        }

        // Case 3: Normal non-alias event.
        // Start recording if:
        //   1. This event carries an anchor that we have not yet recorded.
        //   2. No recording is already in progress.
        //   3. The event is an "opener" (Scalar, MappingStart, SequenceStart).
        //      VYaml keeps TryGetCurrentAnchor() returning the LAST seen anchor ID for ALL
        //      subsequent events (including MappingEnd, SequenceEnd, etc.), so we must
        //      restrict new recordings to opener events only to avoid mis-attributing anchor
        //      IDs to closer events and producing broken/overwritten recordings.
        bool hasAnchor = _parser.TryGetCurrentAnchor(out var currentAnchor);
        var currentKind = MapEventKind(eventType);
        bool isAnchorOpener = currentKind is YamlEventKind.Scalar
            or YamlEventKind.MappingStart or YamlEventKind.SequenceStart;
        if (hasAnchor && _currentRecording == null && isAnchorOpener
            && (_anchorStore == null || !_anchorStore.ContainsKey(currentAnchor.Id)))
        {
            _currentRecording = new List<AnchorEvent>(16);
            _recordingId = currentAnchor.Id;
            _recordingDepth = 0;

            // Track anchor definition for unused-anchor detection
            _definedAnchors ??= new Dictionary<int, (string Name, TextPosition Position)>();
            var anchorPos = ResolveAnchorPosition(currentAnchor.Name.ToString());
            _definedAnchors[currentAnchor.Id] = (currentAnchor.Name.ToString(), anchorPos);
        }

        if (_currentRecording != null)
        {
            var snapshot = SnapshotCurrentEvent(eventType);
            _currentRecording.Add(snapshot);

            // Nested anchor inside existing recording: store independently so aliases
            // to it can resolve within the same or later recordings.
            if (hasAnchor && isAnchorOpener
                && (_anchorStore == null || !_anchorStore.ContainsKey(currentAnchor.Id)))
            {
                _anchorStore ??= new Dictionary<int, List<AnchorEvent>>();
                _definedAnchors ??= new Dictionary<int, (string Name, TextPosition Position)>();
                var nestedAnchorPos = ResolveAnchorPosition(currentAnchor.Name.ToString());
                _definedAnchors[currentAnchor.Id] = (currentAnchor.Name.ToString(), nestedAnchorPos);

                if (currentKind == YamlEventKind.Scalar)
                {
                    // Scalar: single event — store immediately.
                    _anchorStore[currentAnchor.Id] = new List<AnchorEvent> { snapshot };
                }
                else
                {
                    // Mapping/Sequence: start nested recording to capture all child events.
                    _nestedRecordings ??= new List<(int Id, List<AnchorEvent> Events, int Depth)>();
                    _nestedRecordings.Add((currentAnchor.Id, new List<AnchorEvent> { snapshot }, 1));
                }
            }

            ForwardToNestedRecordings(snapshot);

            TrackAnchorDepth(currentKind);
        }

        return true;
    }

    public void SkipHeader() => _parser.SkipAfter(ParseEventType.DocumentStart);

    public void SkipCurrentNode()
    {
        if (_isReplaying)
        {
            // Drain the composite virtual node's events from the replay queue.
            if (_virtualCurrent.Kind is YamlEventKind.SequenceStart or YamlEventKind.MappingStart)
            {
                var depth = 1;
                while (_pendingReplays is { Count: > 0 } && depth > 0)
                {
                    var e = _pendingReplays.Dequeue();
                    if (e.Kind is YamlEventKind.SequenceStart or YamlEventKind.MappingStart) depth++;
                    else if (e.Kind is YamlEventKind.SequenceEnd or YamlEventKind.MappingEnd) depth--;
                }
            }
            // After draining (or for a leaf), advance to the next virtual event so that
            // CurrentKind reflects the event immediately following the skipped node.
            // If the queue still has events, keep _isReplaying=true with the new current;
            // otherwise end replay so the next Read() pulls from the real VYaml parser.
            if (_pendingReplays is { Count: > 0 })
            {
                _virtualCurrent = _pendingReplays.Dequeue();
                // _isReplaying stays true — there is a new current virtual event.
            }
            else
            {
                // Replay is exhausted. If the underlying VYaml parser is still positioned
                // at the Alias event that triggered this replay, advance it so that
                // CurrentKind reflects the event immediately following the Alias
                // (the same post-skip invariant that VYaml's SkipCurrentNode upholds
                // for every non-Alias node type).
                if (_parser.CurrentEventType == ParseEventType.Alias)
                    _parser.Read();
                _isReplaying = false;
            }
            return;
        }
        // VYaml's SkipCurrentNode throws for Alias events (the event is already consumed by
        // Read() and the parser state machine cannot advance from it). An Alias is a leaf node,
        // so we advance manually — same effective behavior as SkipCurrentNode on a scalar leaf.
        if (_parser.CurrentEventType == ParseEventType.Alias)
        {
            if (_parser.Read() && _currentRecording != null)
            {
                // Snapshot the event we just advanced to so the recording stays structurally
                // faithful (mirrors what Read() does for every non-alias event).
                var snapshot = SnapshotCurrentEvent(_parser.CurrentEventType);
                _currentRecording.Add(snapshot);
                TrackAnchorDepth(snapshot.Kind);
            }
            return;
        }
        _parser.SkipCurrentNode();
    }

    public void SkipAfter(YamlEventKind kind) => _parser.SkipAfter(MapEventKind(kind));

    public ReadOnlySpan<byte> GetScalarUtf8() =>
        _isReplaying
            ? (_virtualCurrent.ScalarBytes is { } b ? b.AsSpan() : ReadOnlySpan<byte>.Empty)
            : _parser.IsNullScalar() ? ReadOnlySpan<byte>.Empty : _parser.GetScalarAsUtf8();

    public Utf8Slice GetScalarSlice()
    {
        if (_isReplaying)
            return _virtualCurrent.Slice;

        if (_parser.IsNullScalar())
        {
            var emptyStart = _scalarSliceCursor <= _source.Length ? _scalarSliceCursor : _source.Length;
            return new Utf8Slice(emptyStart, 0);
        }

        var utf8 = _parser.GetScalarAsUtf8();
        if (utf8.IndexOf((byte)'\n') >= 0
            && TryResolveNormalizedSlice(utf8, out var normalizedStart, out var normalizedLength))
        {
            _scalarSliceCursor = normalizedStart + normalizedLength;
            return new Utf8Slice(normalizedStart, normalizedLength);
        }

        if (_parser.TryGetScalarAsSpan(out var raw) && TryResolveRawStart(raw, out var rawStart))
        {
            _scalarSliceCursor = rawStart + raw.Length;
            return new Utf8Slice(rawStart, raw.Length);
        }

        if (utf8.Length == 0)
        {
            // For empty scalars, return the current cursor position without advancing it.
            // CurrentStart handles the backward-scan for accurate position reporting of empty scalars.
            var emptyStart = _scalarSliceCursor <= _source.Length ? _scalarSliceCursor : _source.Length;
            return new Utf8Slice(emptyStart, 0);
        }

        var source = _source.Span;
        var start = -1;
        if (_scalarSliceCursor <= source.Length - utf8.Length)
        {
            var searchStart = _scalarSliceCursor;
            while (searchStart <= source.Length - utf8.Length)
            {
                var idx = source[searchStart..].IndexOf(utf8);
                if (idx < 0) break;
                var candidate = searchStart + idx;
                if (!IsInsideYamlComment(source, candidate))
                {
                    start = candidate;
                    break;
                }
                searchStart = candidate + 1;
            }
        }

        if (start < 0)
        {
            var mark = _parser.CurrentMark;
            var maxStart = source.Length - utf8.Length;
            if (maxStart < 0)
            {
                maxStart = 0;
            }

            start = mark.Position;
            if (start < 0)
            {
                start = 0;
            }
            else if (start > maxStart)
            {
                start = maxStart;
            }
        }

        _scalarSliceCursor = start + utf8.Length;
        return new Utf8Slice(start, utf8.Length);
    }

    private bool TryResolveNormalizedSlice(ReadOnlySpan<byte> utf8, out int start, out int length)
    {
        start = 0;
        length = 0;

        var source = _source.Span;
        if (utf8.Length == 0 || source.Length < utf8.Length)
        {
            return false;
        }

        var anchorLength = utf8.IndexOf((byte)'\n');
        if (anchorLength < 0)
        {
            anchorLength = utf8.Length;
        }

        if (anchorLength == 0)
        {
            anchorLength = Math.Min(utf8.Length, 32);
        }

        anchorLength = Math.Min(anchorLength, 32);
        var anchor = utf8[..anchorLength];

        if (TryResolveNormalizedSliceFrom(_scalarSliceCursor, source, anchor, utf8, out start, out length))
        {
            return true;
        }

        return _scalarSliceCursor > 0
            && TryResolveNormalizedSliceFrom(0, source, anchor, utf8, out start, out length);
    }

    private bool TryResolveNormalizedSliceFrom(int searchStart, ReadOnlySpan<byte> source, ReadOnlySpan<byte> anchor, ReadOnlySpan<byte> utf8, out int start, out int length)
    {
        start = 0;
        length = 0;

        if (anchor.Length == 0 || searchStart < 0 || searchStart > source.Length - anchor.Length)
        {
            return false;
        }

        var relativeStart = 0;
        var searchSpan = source[searchStart..];
        while (relativeStart <= searchSpan.Length - anchor.Length)
        {
            var next = searchSpan[relativeStart..].IndexOf(anchor);
            if (next < 0)
            {
                return false;
            }

            var candidate = searchStart + relativeStart + next;
            var lineIndentWidth = CountLineIndent(source, candidate);
            if (TryMeasureSourceLength(candidate, utf8, lineIndentWidth, out length))
            {
                start = candidate;
                return true;
            }

            relativeStart += next + 1;
        }

        return false;
    }

    private static int CountLineIndent(ReadOnlySpan<byte> source, int contentStart)
    {
        var lineStart = contentStart;
        while (lineStart > 0)
        {
            var b = source[lineStart - 1];
            if (b is (byte)'\n' or (byte)'\r')
            {
                break;
            }

            lineStart--;
        }

        var indentWidth = 0;
        for (var index = lineStart; index < contentStart; index++)
        {
            var b = source[index];
            if (b is not ((byte)' ' or (byte)'\t'))
            {
                return 0;
            }

            indentWidth++;
        }

        return indentWidth;
    }

    private bool TryMeasureSourceLength(int start, ReadOnlySpan<byte> utf8, int lineIndentWidth, out int length)
    {
        length = 0;

        var source = _source.Span;
        if ((uint)start >= (uint)source.Length)
        {
            return false;
        }

        var sourceIndex = start;
        var atLineStart = false;
        for (var valueIndex = 0; valueIndex < utf8.Length; valueIndex++)
        {
            if (atLineStart)
            {
                var skipped = 0;
                while (skipped < lineIndentWidth
                    && sourceIndex < source.Length
                    && (source[sourceIndex] == (byte)' ' || source[sourceIndex] == (byte)'\t'))
                {
                    sourceIndex++;
                    skipped++;
                }

                atLineStart = false;
            }

            if (sourceIndex >= source.Length)
            {
                // At source EOF, trailing newlines from block scalar clip chomping are allowed.
                for (var k = valueIndex; k < utf8.Length; k++)
                {
                    if (utf8[k] != (byte)'\n') return false;
                }

                length = sourceIndex - start;
                return true;
            }

            var valueByte = utf8[valueIndex];
            if (valueByte == (byte)'\n')
            {
                if (source[sourceIndex] == (byte)'\r')
                {
                    if (sourceIndex + 1 >= source.Length || source[sourceIndex + 1] != (byte)'\n')
                    {
                        return false;
                    }

                    sourceIndex += 2;
                    atLineStart = true;
                    continue;
                }

                if (source[sourceIndex] != (byte)'\n')
                {
                    return false;
                }

                sourceIndex++;
                atLineStart = true;
                continue;
            }

            if (source[sourceIndex] != valueByte)
            {
                return false;
            }

            sourceIndex++;
        }

        length = sourceIndex - start;
        return true;
    }

    private bool TryResolveRawStart(ReadOnlySpan<byte> raw, out int start)
    {
        if (raw.Length == 0)
        {
            start = _scalarSliceCursor <= _source.Length ? _scalarSliceCursor : _source.Length;
            return true;
        }

        var source = _source.Span;
        if (source.Length < raw.Length)
        {
            start = 0;
            return false;
        }

        if (_scalarSliceCursor <= source.Length - raw.Length)
        {
            var searchStart = _scalarSliceCursor;
            while (searchStart <= source.Length - raw.Length)
            {
                var offsetFromCursor = source[searchStart..].IndexOf(raw);
                if (offsetFromCursor < 0) break;
                var candidate = searchStart + offsetFromCursor;
                if (!IsInsideYamlComment(source, candidate))
                {
                    start = candidate;
                    return true;
                }
                searchStart = candidate + 1;
            }
        }

        var searchFromStart = 0;
        while (searchFromStart <= source.Length - raw.Length)
        {
            var offsetFromStart = source[searchFromStart..].IndexOf(raw);
            if (offsetFromStart < 0) break;
            var candidate = searchFromStart + offsetFromStart;
            if (!IsInsideYamlComment(source, candidate))
            {
                start = candidate;
                return true;
            }
            searchFromStart = candidate + 1;
        }

        start = 0;
        return false;
    }

    /// <summary>
    /// Returns true when the byte at <paramref name="offset"/> is inside a YAML comment
    /// (i.e. preceded by an unquoted '#' on the same line).
    /// </summary>
    private static bool IsInsideYamlComment(ReadOnlySpan<byte> source, int offset)
    {
        // Walk backward to the start of the line looking for '#'.
        // This is a simplified heuristic: it does not track quoted regions, but YAML comments
        // cannot appear inside quoted scalars so this is accurate for the fallback search path
        // (which only runs when TryGetScalarAsSpan and TryResolveNormalizedSlice both failed).
        for (var i = offset - 1; i >= 0; i--)
        {
            var ch = source[i];
            if (ch == (byte)'\n') return false;
            if (ch == (byte)'#') return true;
        }

        return false;
    }

    public string? GetScalarString()
    {
        if (_isReplaying)
        {
            if (_virtualCurrent.ScalarBytes == null) return null;
            return System.Text.Encoding.UTF8.GetString(_virtualCurrent.ScalarBytes);
        }
        return _parser.IsNullScalar() ? null : _parser.GetScalarAsString();
    }

    public ScalarTag GetScalarTag()
    {
        if (_isReplaying)
            return _virtualCurrent.Tag;

        // Check VYaml's internal null-scalar flag before GetScalarUtf8() — null scalars
        // return an empty span but should be tagged as Null, not Str.
        if (_parser.IsNullScalar())
            return ScalarTag.Null;

        var value = GetScalarUtf8();
        if (value.Length == 0)
        {
            return ScalarTag.Str;
        }

        if (value.SequenceEqual("null"u8) || value.SequenceEqual("~"u8))
        {
            return ScalarTag.Null;
        }

        if (value.SequenceEqual("true"u8) || value.SequenceEqual("false"u8))
        {
            return ScalarTag.Bool;
        }

        if (Utf8Parser.TryParse(value, out long _, out var consumedInt) && consumedInt == value.Length)
        {
            return ScalarTag.Int;
        }

        if (Utf8Parser.TryParse(value, out double _, out var consumedFloat) && consumedFloat == value.Length)
        {
            return ScalarTag.Float;
        }

        return ScalarTag.Str;
    }

    public bool IsScalarQuoted() => _isReplaying ? _virtualCurrent.IsQuoted : false;

    // Anchor / alias resolution helpers

    /// <summary>
    /// Snapshots the current VYaml parser event for anchor recording.
    /// Captures the <see cref="Utf8Slice"/> by peeking (save/restore cursor) so that
    /// replayed scalars carry a valid offset+length into the source buffer, and the
    /// <see cref="WorkflowParser"/> can still call <see cref="GetScalarSlice"/> normally
    /// afterwards.  When replayed, positions point to the anchor definition site, which is
    /// acceptable for our diagnostic ranges.
    /// </summary>
    private AnchorEvent SnapshotCurrentEvent(ParseEventType eventType)
    {
        var kind = MapEventKind(eventType);
        if (kind == YamlEventKind.Scalar)
        {
            // Peek the slice (save/restore cursor) so the subsequent GetScalarSlice() call
            // from WorkflowParser operates from the same cursor position as if we hadn't
            // looked at the slice here.
            var savedCursor = _scalarSliceCursor;
            var slice = GetScalarSlice();
            _scalarSliceCursor = savedCursor;

            return new AnchorEvent
            {
                Kind = kind,
                ScalarBytes = _parser.IsNullScalar() ? [] : _parser.GetScalarAsUtf8().ToArray(),
                Slice = slice,
                Tag = GetScalarTag(),
                IsQuoted = false,
                Start = CurrentStart,
            };
        }
        return new AnchorEvent { Kind = kind, Start = CurrentStart };
    }

    /// <summary>
    /// Updates <see cref="_recordingDepth"/> and completes the current anchor recording when
    /// the anchor's top-level node is fully consumed.
    /// </summary>
    private void TrackAnchorDepth(YamlEventKind kind)
    {
        if (kind is YamlEventKind.SequenceStart or YamlEventKind.MappingStart)
        {
            _recordingDepth++;
        }
        else if (kind is YamlEventKind.SequenceEnd or YamlEventKind.MappingEnd)
        {
            _recordingDepth--;
            if (_recordingDepth == 0)
                CompleteAnchorRecording();
        }
        else if (kind == YamlEventKind.Scalar && _recordingDepth == 0)
        {
            CompleteAnchorRecording();
        }
    }

    private void CompleteAnchorRecording()
    {
        _anchorStore ??= new Dictionary<int, List<AnchorEvent>>();
        _anchorStore[_recordingId] = _currentRecording!;
        _currentRecording = null;
        _recordingDepth = 0;

        // Discard any nested recordings that weren't completed (shouldn't happen in well-formed YAML).
        _nestedRecordings?.Clear();
    }

    /// <summary>
    /// Forwards an event to any active nested recordings (mapping/sequence anchors inside outer recording).
    /// Completes a nested recording when its depth reaches zero.
    /// </summary>
    private void ForwardToNestedRecordings(AnchorEvent snapshot)
    {
        if (_nestedRecordings is not { Count: > 0 })
            return;

        for (var i = _nestedRecordings.Count - 1; i >= 0; i--)
        {
            var (nId, nEvents, nDepth) = _nestedRecordings[i];
            // Skip if this is the anchor opener event that was already added when the nested recording started.
            if (nEvents.Count == 1 && nDepth == 1
                && nEvents[0].Kind == snapshot.Kind && nEvents[0].Start == snapshot.Start)
                continue;
            nEvents.Add(snapshot);
            if (snapshot.Kind is YamlEventKind.SequenceStart or YamlEventKind.MappingStart)
                nDepth++;
            else if (snapshot.Kind is YamlEventKind.SequenceEnd or YamlEventKind.MappingEnd)
            {
                nDepth--;
                if (nDepth == 0)
                {
                    _anchorStore ??= new Dictionary<int, List<AnchorEvent>>();
                    _anchorStore[nId] = nEvents;
                    _nestedRecordings.RemoveAt(i);
                    continue;
                }
            }
            _nestedRecordings[i] = (nId, nEvents, nDepth);
        }
    }

    /// <summary>
    /// Returns anchors that were defined but never referenced by an alias.
    /// Each entry contains the anchor name and the position where it was defined.
    /// Returns an empty span if all anchors are referenced or no anchors exist.
    /// </summary>
    public ReadOnlySpan<(string Name, TextPosition Position)> GetUnusedAnchors(Span<(string Name, TextPosition Position)> buffer)
    {
        if (_definedAnchors == null || _definedAnchors.Count == 0)
            return ReadOnlySpan<(string Name, TextPosition Position)>.Empty;

        int count = 0;
        foreach (var (id, info) in _definedAnchors)
        {
            if (_referencedAnchorIds == null || !_referencedAnchorIds.Contains(id))
            {
                if (count < buffer.Length)
                    buffer[count] = info;
                count++;
            }
        }
        return buffer[..Math.Min(count, buffer.Length)];
    }

    /// <summary>
    /// Returns recursive alias occurrences detected during parsing.
    /// Each entry contains the anchor name and the position where the recursive alias was found.
    /// </summary>
    public ReadOnlySpan<(string Name, TextPosition Position)> GetRecursiveAliases(Span<(string Name, TextPosition Position)> buffer)
    {
        if (_recursiveAliases == null || _recursiveAliases.Count == 0)
            return ReadOnlySpan<(string Name, TextPosition Position)>.Empty;

        var count = Math.Min(_recursiveAliases.Count, buffer.Length);
        for (int i = 0; i < count; i++)
            buffer[i] = _recursiveAliases[i];
        return buffer[..count];
    }

    /// <summary>
    /// Converts a UTF-8 byte offset in <see cref="_source"/> to a 1-based line / column position.
    /// Used by the parser core via <see cref="IYamlStreamReader.ComputePositionFromOffset"/> to derive
    /// accurate positions from <see cref="GetScalarSlice"/> offsets, which are more reliable than
    /// VYaml's <see cref="YamlParser.CurrentMark"/> (which advances to the next token for scalars).
    /// </summary>
    public TextPosition ComputePositionFromOffset(int offset)
        => ComputeTextPositionFromOffset(_source.Span, offset);

    /// <summary>
    /// Locates the `&amp;name` anchor tag in the source bytes by searching forward from the
    /// current scalar slice cursor. Returns the position of the `&amp;` character.
    /// Falls back to <see cref="CurrentStart"/> if not found.
    /// </summary>
    private TextPosition ResolveAnchorPosition(string anchorName)
    {
        var source = _source.Span;
        var mark = _parser.CurrentMark;
        var searchEnd = mark.Position;
        if (searchEnd > source.Length) searchEnd = source.Length;

        // Search for &anchorName in source from _scalarSliceCursor
        var anchorBytes = System.Text.Encoding.UTF8.GetBytes("&" + anchorName);
        var anchorSpan = anchorBytes.AsSpan();

        for (var i = _scalarSliceCursor; i <= searchEnd - anchorSpan.Length; i++)
        {
            if (source[i] == (byte)'&' && source.Slice(i, anchorSpan.Length).SequenceEqual(anchorSpan))
            {
                return ComputeTextPositionFromOffset(source, i);
            }
        }

        return CurrentStart;
    }

    /// <summary>
    /// For non-empty scalars, VYaml's <see cref="YamlParser.CurrentMark"/> may point past the
    /// scalar content to the next token. This helper locates the scalar content in the source
    /// by searching backward from <paramref name="markPosition"/> for the scalar bytes.
    /// For quoted scalars, the position is set to the opening quote character.
    /// </summary>
    private TextPosition ResolveNonEmptyScalarStart(int markPosition)
    {
        var source = _source.Span;
        var utf8 = _parser.GetScalarAsUtf8();

        if (utf8.Length == 0)
        {
            return ComputeTextPositionFromOffset(source, markPosition);
        }

        // Search forward from _scalarSliceCursor for the scalar content.
        var searchEnd = markPosition;
        if (searchEnd > source.Length) searchEnd = source.Length;
        var maxStart = searchEnd - utf8.Length;
        if (maxStart < 0) maxStart = 0;

        var searchFrom = _scalarSliceCursor;
        if (searchFrom > maxStart) searchFrom = 0;

        var bestStart = -1;
        for (var i = searchFrom; i <= maxStart; i++)
        {
            if (source[i] == utf8[0] && source.Slice(i, utf8.Length).SequenceEqual(utf8))
            {
                if (!IsInsideYamlComment(source, i))
                {
                    bestStart = i;
                }
            }
        }

        if (bestStart < 0)
        {
            return new TextPosition(markPosition, _parser.CurrentMark.Line, _parser.CurrentMark.Col);
        }

        // Check for a leading quote character
        if (bestStart > 0 && source[bestStart - 1] is (byte)'\'' or (byte)'"')
        {
            bestStart--;
        }

        return ComputeTextPositionFromOffset(source, bestStart);
    }

    /// <summary>
    /// VYaml advances its scanner past an empty scalar to the next meaningful token, so
    /// <see cref="YamlParser.CurrentMark"/> for an empty-scalar event points at that next token.
    /// This helper walks backward through <see cref="_source"/> from <paramref name="nextTokenPosition"/>,
    /// skips whitespace/newlines, and – if it finds an adjacent pair of matching quotes ('''' or &quot;&quot;) –
    /// returns the offset of the opening quote.  Otherwise it returns the backward-walked position.
    /// <para>
    /// When VYaml's mark has advanced past the next key's <c>key:</c> separator (common for null
    /// scalars like <c>permissions:\n    runs-on: value</c>), the initial whitespace scan stops at
    /// that colon without crossing a newline.  We detect this in two ways:
    /// <list type="number">
    /// <item>The colon has a value on the same line (e.g. <c>runs-on: ubuntu-latest</c>).</item>
    /// <item>The colon is on a different line than <see cref="_scalarSliceCursor"/>
    ///        (e.g. <c>jobs:</c> with a mapping value on the next line).</item>
    /// </list>
    /// In both cases we skip the entire <c>key:</c> pattern and repeat.
    /// </para>
    /// </summary>
    private int ResolveEmptyScalarStart(int nextTokenPosition)
    {
        var source = _source.Span;
        var pos = nextTokenPosition;
        if (pos > source.Length)
        {
            pos = source.Length;
        }

        // Walk backward past trailing whitespace and line endings, tracking newline crossings.
        var crossedNewline = false;
        while (pos > 0 && source[pos - 1] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
        {
            if (source[pos - 1] is (byte)'\n' or (byte)'\r')
                crossedNewline = true;
            pos--;
        }

        // If the initial whitespace scan found no whitespace at all, VYaml's mark may be
        // positioned inside the next token (e.g. at the ':' of "steps:" after a null mapping
        // value). Walk backward past the token characters and then through whitespace/newlines
        // to locate the empty scalar's actual line.
        if (pos == nextTokenPosition && pos > 0
            && source[pos - 1] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
        {
            while (pos > 0 && source[pos - 1] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
                pos--;
            while (pos > 0 && source[pos - 1] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
            {
                if (source[pos - 1] is (byte)'\n' or (byte)'\r')
                    crossedNewline = true;
                pos--;
            }
        }

        // If the whitespace scan did NOT cross a newline and we stopped at a ':', VYaml's mark
        // may have landed right after the next key's colon.
        // Two detection strategies:
        //   (a) The colon has non-whitespace content before the next newline (inline value).
        //   (b) There is a newline between _scalarSliceCursor and the colon, meaning the colon
        //       is on a different source line than the previous key we parsed (the one whose
        //       null value we are resolving).
        if (!crossedNewline && pos > 0 && source[pos - 1] == (byte)':')
        {
            var colonPos = pos - 1;
            var isNextKeyColon = false;

            // Strategy (a): colon has inline value
            for (var i = pos; i < source.Length; i++)
            {
                var b = source[i];
                if (b is (byte)'\n' or (byte)'\r') break;
                if (b is not ((byte)' ' or (byte)'\t'))
                {
                    isNextKeyColon = true;
                    break;
                }
            }

            // Strategy (b): newline between cursor and colon position
            if (!isNextKeyColon)
            {
                for (var i = _scalarSliceCursor; i < colonPos; i++)
                {
                    if (source[i] is (byte)'\n')
                    {
                        isNextKeyColon = true;
                        break;
                    }
                }
            }

            if (isNextKeyColon)
            {
                pos--; // skip ':'
                // Skip backward past the key name.
                while (pos > 0 && source[pos - 1] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
                    pos--;
                // Skip whitespace/newlines again.
                while (pos > 0 && source[pos - 1] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
                    pos--;
            }
        }

        // If we stopped at a '-' (YAML block sequence indicator), speculatively skip over it
        // to look for quotes (e.g. - '' or - ""). If no quotes are found, the '-' is the
        // sequence indicator for this null entry — return the position right after it.
        if (pos > 0 && source[pos - 1] == (byte)'-')
        {
            var afterDash = pos;
            pos--;
            while (pos > 0 && source[pos - 1] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
            {
                pos--;
            }

            // Check for '' or "" before the dash
            if (pos >= 2
                && source[pos - 1] == source[pos - 2]
                && source[pos - 1] is (byte)'\'' or (byte)'"')
            {
                return pos - 2;
            }

            // No quotes found; return position right after the '-'.
            return afterDash;
        }

        // Check for '' (two single-quotes) or "" (two double-quotes) immediately before pos.
        if (pos >= 2
            && source[pos - 1] == source[pos - 2]
            && source[pos - 1] is (byte)'\'' or (byte)'"')
        {
            return pos - 2;  // offset of the opening quote
        }

        // Check for explicit YAML null text ("null", "~", "Null", "NULL") that the backward
        // scan stopped right after. Return the start of the null keyword instead of the end.
        if (pos >= 4)
        {
            var s = source.Slice(pos - 4, 4);
            if ((s[0] == (byte)'n' && s[1] == (byte)'u' && s[2] == (byte)'l' && s[3] == (byte)'l')
                || (s[0] == (byte)'N' && s[1] == (byte)'u' && s[2] == (byte)'l' && s[3] == (byte)'l')
                || (s[0] == (byte)'N' && s[1] == (byte)'U' && s[2] == (byte)'L' && s[3] == (byte)'L'))
            {
                if (pos - 4 == 0 || source[pos - 5] is (byte)' ' or (byte)'\t' or (byte)'-')
                    return pos - 4;
            }
        }

        if (pos >= 1 && source[pos - 1] == (byte)'~'
            && (pos - 1 == 0 || source[pos - 2] is (byte)' ' or (byte)'\t' or (byte)'-'))
        {
            return pos - 1;
        }

        return pos;
    }

    private static TextPosition ComputeTextPositionFromOffset(ReadOnlySpan<byte> source, int offset)
    {
        var end = offset;
        if (end > source.Length)
        {
            end = source.Length;
        }

        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < end; i++)
        {
            if (source[i] == (byte)'\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return new TextPosition(offset, line, (end - lineStart) + 1);
    }

    private static YamlEventKind MapEventKind(ParseEventType vt)
    {
        return vt switch
        {
            ParseEventType.StreamStart => YamlEventKind.StreamStart,
            ParseEventType.StreamEnd => YamlEventKind.StreamEnd,
            ParseEventType.DocumentStart => YamlEventKind.DocumentStart,
            ParseEventType.DocumentEnd => YamlEventKind.DocumentEnd,
            ParseEventType.MappingStart => YamlEventKind.MappingStart,
            ParseEventType.MappingEnd => YamlEventKind.MappingEnd,
            ParseEventType.SequenceStart => YamlEventKind.SequenceStart,
            ParseEventType.SequenceEnd => YamlEventKind.SequenceEnd,
            ParseEventType.Scalar => YamlEventKind.Scalar,
            ParseEventType.Alias => YamlEventKind.Alias,
            _ => YamlEventKind.None,
        };
    }

    private static ParseEventType MapEventKind(YamlEventKind kind)
    {
        return kind switch
        {
            YamlEventKind.StreamStart => ParseEventType.StreamStart,
            YamlEventKind.StreamEnd => ParseEventType.StreamEnd,
            YamlEventKind.DocumentStart => ParseEventType.DocumentStart,
            YamlEventKind.DocumentEnd => ParseEventType.DocumentEnd,
            YamlEventKind.MappingStart => ParseEventType.MappingStart,
            YamlEventKind.MappingEnd => ParseEventType.MappingEnd,
            YamlEventKind.SequenceStart => ParseEventType.SequenceStart,
            YamlEventKind.SequenceEnd => ParseEventType.SequenceEnd,
            YamlEventKind.Scalar => ParseEventType.Scalar,
            YamlEventKind.Alias => ParseEventType.Alias,
            _ => ParseEventType.Nothing,
        };
    }
}

/// <summary>
/// Snapshot of a single YAML event captured during anchor recording.
/// Used by <see cref="VYamlStreamAdapter"/> to replay alias-referenced content.
/// </summary>
internal struct AnchorEvent
{
    public YamlEventKind Kind;
    // Scalar data (null for non-scalar events)
    public byte[]? ScalarBytes;
    public Utf8Slice Slice;
    public ScalarTag Tag;
    public bool IsQuoted;
    // Source position (from the anchor definition site)
    public TextPosition Start;
}

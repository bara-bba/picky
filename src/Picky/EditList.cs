namespace Picky;

/// <summary>One kept piece of the source clip, as a range in <b>source</b> time.</summary>
internal sealed record Segment(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Length => End - Start;
}

/// <summary>
/// The edit as an ordered list of kept <see cref="Segment"/>s (a simple edit decision list), the way
/// an NLE timeline holds clips. The <i>program</i> is those segments played back to back, so a
/// program time maps to one segment plus an offset inside it.
///
/// <para>Every operation is a pure function returning a new list and never mutates its input, so
/// undo/redo is just a stack of earlier lists.</para>
/// </summary>
internal static class EditList
{
    /// <summary>Cuts closer than this to a block edge are ignored (they'd create a sliver block).</summary>
    private static readonly TimeSpan MinBlock = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<Segment> Whole(TimeSpan duration) => new[] { new Segment(TimeSpan.Zero, duration) };

    public static TimeSpan Total(IReadOnlyList<Segment> segments)
        => TimeSpan.FromTicks(segments.Sum(s => s.Length.Ticks));

    /// <summary>Program time at which block <paramref name="index"/> starts.</summary>
    public static TimeSpan ProgramStart(IReadOnlyList<Segment> segments, int index)
        => TimeSpan.FromTicks(segments.Take(index).Sum(s => s.Length.Ticks));

    /// <summary>The block a program time falls in, and the matching source time.</summary>
    public static (int Index, TimeSpan Source) ToSource(IReadOnlyList<Segment> segments, TimeSpan program)
    {
        if (program < TimeSpan.Zero)
        {
            program = TimeSpan.Zero;
        }

        var start = TimeSpan.Zero;
        for (int i = 0; i < segments.Count; i++)
        {
            var end = start + segments[i].Length;
            if (program < end || i == segments.Count - 1)
            {
                var offset = program - start;
                if (offset > segments[i].Length)
                {
                    offset = segments[i].Length;
                }
                return (i, segments[i].Start + offset);
            }
            start = end;
        }

        return (0, TimeSpan.Zero);
    }

    /// <summary>Program time of a source time inside block <paramref name="index"/>.</summary>
    public static TimeSpan ToProgram(IReadOnlyList<Segment> segments, int index, TimeSpan source)
    {
        var seg = segments[index];
        var clamped = source < seg.Start ? seg.Start : (source > seg.End ? seg.End : source);
        return ProgramStart(segments, index) + (clamped - seg.Start);
    }

    /// <summary>
    /// Splits the block under <paramref name="program"/> into two at that point. Returns the input
    /// unchanged when the cut would land on (or within <see cref="MinBlock"/> of) a block edge.
    /// </summary>
    public static IReadOnlyList<Segment> Split(IReadOnlyList<Segment> segments, TimeSpan program)
    {
        var (index, source) = ToSource(segments, program);
        var seg = segments[index];

        if (source - seg.Start < MinBlock || seg.End - source < MinBlock)
        {
            return segments;
        }

        var result = segments.ToList();
        result[index] = seg with { End = source };
        result.Insert(index + 1, new Segment(source, seg.End));
        return result;
    }

    /// <summary>
    /// Ripple-deletes the given blocks (the rest close up). Refuses to delete every block, which
    /// would leave an empty clip, by returning the input unchanged.
    /// </summary>
    public static IReadOnlyList<Segment> Remove(IReadOnlyList<Segment> segments, IReadOnlyCollection<int> indices)
    {
        if (indices.Count == 0 || indices.Count >= segments.Count)
        {
            return segments;
        }

        return segments.Where((_, i) => !indices.Contains(i)).ToList();
    }

    /// <summary>True when the list is still the untouched whole clip (one segment, nothing removed).</summary>
    public static bool IsWhole(IReadOnlyList<Segment> segments, TimeSpan duration)
    {
        if (segments.Count == 0)
        {
            return false;
        }

        // Adjacent segments that still touch are just an un-removed cut: same output as the whole clip.
        for (int i = 1; i < segments.Count; i++)
        {
            if (segments[i].Start != segments[i - 1].End)
            {
                return false;
            }
        }

        return segments[0].Start == TimeSpan.Zero
            && (duration - segments[^1].End).Duration() < TimeSpan.FromMilliseconds(50);
    }
}

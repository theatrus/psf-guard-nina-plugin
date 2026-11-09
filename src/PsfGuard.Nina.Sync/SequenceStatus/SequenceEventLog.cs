using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.SequenceStatus;

/// <summary>
/// The latest time of each N.I.N.A. event the report lists. N.I.N.A. raises
/// the events on its own threads; recording one only stores a number.
/// </summary>
public sealed class SequenceEventLog
{
    private long imageSaved = long.MinValue;
    private long dither = long.MinValue;
    private long autofocus = long.MinValue;
    private long meridianFlip = long.MinValue;

    public void ImageSaved(DateTimeOffset at) => Record(ref imageSaved, at);

    public void Dithered(DateTimeOffset at) => Record(ref dither, at);

    public void AutofocusFinished(DateTimeOffset at) => Record(ref autofocus, at);

    public void MeridianFlipFinished(DateTimeOffset at) => Record(ref meridianFlip, at);

    /// <summary>The events at or after <paramref name="sinceMs"/>; earlier ones are left out.</summary>
    public SequenceRecentEvents Since(long sinceMs) => new()
    {
        ImageSavedMs = Read(ref imageSaved, sinceMs),
        DitherMs = Read(ref dither, sinceMs),
        AutofocusMs = Read(ref autofocus, sinceMs),
        MeridianFlipMs = Read(ref meridianFlip, sinceMs),
    };

    private static void Record(ref long field, DateTimeOffset at)
    {
        var value = at.ToUnixTimeMilliseconds();
        var current = Volatile.Read(ref field);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref field, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    private static long? Read(ref long field, long sinceMs)
    {
        var value = Volatile.Read(ref field);
        return value >= sinceMs && value != long.MinValue ? value : null;
    }
}

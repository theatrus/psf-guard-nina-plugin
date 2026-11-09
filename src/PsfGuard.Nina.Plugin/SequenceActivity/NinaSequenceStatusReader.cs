using System.Reflection;
using NINA.Core.Enum;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using PsfGuard.Nina.Sync.SequenceStatus;

namespace PsfGuard.Nina.Plugin.SequenceActivity;

/// <summary>
/// Reads what the Advanced Sequencer is doing. Call it on N.I.N.A.'s UI
/// thread. It reads instruction and container names, a target's name, and
/// exposure, wait and cooling settings; never coordinates, a
/// <c>WaitUntil</c> expression, a <c>Break</c> reason or message text.
/// </summary>
internal static class NinaSequenceStatusReader
{
    private const int MaxDepth = 64;

    /// <param name="currentFilter">The filter wheel's current filter, used when an exposure names none.</param>
    internal static SequenceObservation? Read(ISequenceMediator sequence, Func<string?>? currentFilter = null)
    {
        if (!sequence.Initialized)
        {
            return null;
        }

        if (!sequence.IsAdvancedSequenceRunning())
        {
            return new SequenceObservation(false, null, null);
        }

        var running = (sequence.GetAdvancedSequencerCurrentRunningItems() ?? [])
            .Where(item => item is not null && item.Status == SequenceEntityStatus.RUNNING)
            .ToArray();
        var operation = SequenceText.Operation(running.Select(PathOf));
        return new SequenceObservation(true, operation, ActivityOf(running, currentFilter));
    }

    /// <summary>The item's path as the Director plugin builds it.</summary>
    private static string PathOf(ISequenceItem item) =>
        SequenceText.Path(
            Ancestry(item).TakeWhile(entry => entry is not ISequenceRootContainer).Select(entry => entry.Name),
            item.GetType().Name);

    /// <summary>
    /// A running trigger first, since the sequence waits for it. Otherwise the
    /// most specific running instruction: one that is not a container, the
    /// deepest, and then the first N.I.N.A. lists. An instruction inside a
    /// Smart Exposure or Take Many Exposures reports as that instruction.
    /// </summary>
    private static SequenceActivityObservation? ActivityOf(
        IReadOnlyList<ISequenceItem> running,
        Func<string?>? currentFilter)
    {
        foreach (var item in running)
        {
            foreach (var scope in Ancestry(item))
            {
                if (scope is not ITriggerable triggerable)
                {
                    continue;
                }

                var trigger = (triggerable.GetTriggersSnapshot() ?? [])
                    .FirstOrDefault(entry => entry is not null && entry.Status == SequenceEntityStatus.RUNNING);
                if (trigger is not null)
                {
                    return new SequenceActivityObservation(
                        trigger,
                        SequenceActivityKinds.ForTrigger(TypeNames(trigger.GetType())),
                        trigger.Name,
                        TargetName: TargetNameOf(scope));
                }
            }
        }

        var chosen = running
            .Select(item => Ancestry(item).FirstOrDefault(entry => SequenceActivityKinds.IsAggregateExposure(entry.GetType().Name)) ?? item)
            .Distinct<ISequenceItem>(ReferenceEqualityComparer.Instance)
            .Select((item, index) => (Item: item, Index: index, Depth: Ancestry(item).Count()))
            .OrderBy(entry => IsContainer(entry.Item) ? 1 : 0)
            .ThenByDescending(entry => entry.Depth)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .FirstOrDefault();
        return chosen is null ? null : Describe(chosen, currentFilter);
    }

    private static SequenceActivityObservation Describe(ISequenceItem item, Func<string?>? currentFilter)
    {
        var typeName = item.GetType().Name;
        var kind = SequenceActivityKinds.ForInstruction(TypeNames(item.GetType()));
        var activity = new SequenceActivityObservation(item, kind, item.Name, TargetName: TargetNameOf(item));
        try
        {
            return kind switch
            {
                SequenceActivityKinds.Exposing => DescribeExposure(item, typeName, activity, currentFilter),
                SequenceActivityKinds.CameraCooling => activity with
                {
                    TargetTemperatureC = Number(OptionalProperty(item, "Temperature")),
                },
                SequenceActivityKinds.TimeWait => DescribeTimeWait(item, typeName, activity),
                SequenceActivityKinds.AstronomicalWait => activity with
                {
                    UntilMs = UnixMilliseconds(OptionalProperty(OptionalProperty(item, "Data"), "ExpectedDateTime")),
                },
                _ => activity,
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A plugin's instruction may not look like N.I.N.A.'s; report it without details.
            return activity;
        }
    }

    private static SequenceActivityObservation DescribeExposure(
        ISequenceItem item,
        string typeName,
        SequenceActivityObservation activity,
        Func<string?>? currentFilter)
    {
        string? filter = null;
        if (SequenceActivityKinds.IsAggregateExposure(typeName))
        {
            var exposure = OptionalMethod(item, "GetTakeExposure");
            var loop = OptionalMethod(item, "GetLoopCondition");
            var switchFilter = OptionalMethod(item, "GetSwitchFilter");
            filter = OptionalProperty(OptionalProperty(switchFilter, "Filter"), "Name") as string;
            activity = activity with
            {
                ExposureSeconds = Number(OptionalProperty(exposure, "ExposureTime")),
                Completed = Integer(OptionalProperty(loop, "CompletedIterations")),
                Total = Integer(OptionalProperty(loop, "Iterations")),
            };
        }
        else
        {
            activity = activity with
            {
                ExposureSeconds = Number(OptionalProperty(item, "ExposureTime")),
                Completed = Integer(OptionalProperty(item, "ExposureCount")),
            };
        }

        if (string.IsNullOrWhiteSpace(filter))
        {
            filter = ReadCurrentFilter(currentFilter);
        }

        return activity with { Filter = filter };
    }

    private static SequenceActivityObservation DescribeTimeWait(
        ISequenceItem item,
        string typeName,
        SequenceActivityObservation activity)
    {
        if (OptionalMethod(item, "GetEstimatedDuration") is not TimeSpan duration || duration < TimeSpan.Zero)
        {
            return activity;
        }

        // Wait for Time estimates the time left; Wait for Time Span its whole length.
        return typeName == "WaitForTime"
            ? activity with { UntilMs = DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds() }
            : activity with { RunsFor = duration };
    }

    /// <summary>The nearest enclosing target's name, including Target Scheduler's current target.</summary>
    private static string? TargetNameOf(ISequenceItem item)
    {
        foreach (var entry in Ancestry(item))
        {
            if (entry is IDeepSkyObjectContainer container
                && container.Target?.TargetName is { } name
                && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    private static string? ReadCurrentFilter(Func<string?>? currentFilter)
    {
        try
        {
            return currentFilter?.Invoke();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The item, then each parent up to the sequence root.</summary>
    private static IEnumerable<ISequenceItem> Ancestry(ISequenceItem item)
    {
        var depth = 0;
        for (ISequenceItem? current = item; current is not null && depth < MaxDepth; current = current.Parent, depth++)
        {
            yield return current;
        }
    }

    private static bool IsContainer(ISequenceItem item) =>
        item is ISequenceContainer && !SequenceActivityKinds.IsAggregateExposure(item.GetType().Name);

    private static IEnumerable<string> TypeNames(Type type)
    {
        for (Type? current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            yield return current.Name;
        }
    }

    private static double? Number(object? value) => value switch
    {
        double number when double.IsFinite(number) => number,
        float number when float.IsFinite(number) => number,
        int number => number,
        long number => number,
        _ => null,
    };

    private static int? Integer(object? value) => value switch
    {
        int number => number,
        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        _ => null,
    };

    /// <summary>
    /// N.I.N.A. computes waits in local time and some values carry no kind;
    /// read those as local time. Unset values are left out.
    /// </summary>
    private static long? UnixMilliseconds(object? value)
    {
        switch (value)
        {
            case DateTime dateTime when dateTime != DateTime.MinValue && dateTime != DateTime.MaxValue:
                var offset = dateTime.Kind switch
                {
                    DateTimeKind.Utc => new DateTimeOffset(dateTime, TimeSpan.Zero),
                    DateTimeKind.Local => new DateTimeOffset(dateTime),
                    _ => new DateTimeOffset(dateTime, TimeZoneInfo.Local.GetUtcOffset(dateTime)),
                };
                return offset.ToUnixTimeMilliseconds();
            case DateTimeOffset dateTimeOffset when dateTimeOffset != DateTimeOffset.MinValue && dateTimeOffset != DateTimeOffset.MaxValue:
                return dateTimeOffset.ToUnixTimeMilliseconds();
            default:
                return null;
        }
    }

    private static object? OptionalProperty(object? source, string name)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return source.GetType()
                .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(source);
        }
        catch (Exception exception) when (exception is TargetInvocationException or AmbiguousMatchException)
        {
            return null;
        }
    }

    private static object? OptionalMethod(object? source, string name)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return source.GetType()
                .GetMethod(name, BindingFlags.Instance | BindingFlags.Public, binder: null, types: Type.EmptyTypes, modifiers: null)
                ?.Invoke(source, null);
        }
        catch (Exception exception) when (exception is TargetInvocationException or AmbiguousMatchException)
        {
            return null;
        }
    }
}

using System.Reflection;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Trigger;
using PsfGuard.Nina.Plugin.SequenceActivity;
using PsfGuard.Nina.Sync.SequenceStatus;

namespace PsfGuard.Nina.Plugin.Tests;

public sealed class SequenceStatusReaderTests
{
    [Fact]
    public void ReadsNothingBeforeTheSequencerIsReadyAndStopsWhenItIsNotRunning()
    {
        Assert.Null(NinaSequenceStatusReader.Read(Mediator(initialized: false, running: false, [])));
        Assert.Equal(
            new SequenceObservation(false, null, null),
            NinaSequenceStatusReader.Read(Mediator(initialized: true, running: false, [])));
    }

    [Fact]
    public void BuildsOperationLikeDirectorAndPicksTheDeepestInstruction()
    {
        var root = new SequenceRootContainer { Name = "Root" };
        var target = new TargetContainer("Imaging", "M 31");
        var inner = new SequentialContainer { Name = "Loop", Status = SequenceEntityStatus.RUNNING };
        var exposure = new TakeExposure
        {
            Name = "Take Exposure",
            ExposureTime = 300,
            ExposureCount = 4,
            Status = SequenceEntityStatus.RUNNING,
        };
        var wait = new WaitForTimeSpan { Name = "Settle", Time = 30, Status = SequenceEntityStatus.RUNNING };
        root.Add(target);
        target.Add(inner);
        inner.Add(exposure);
        target.Add(wait);

        var observation = NinaSequenceStatusReader.Read(
            Mediator(initialized: true, running: true, [wait, exposure, inner]),
            () => "Ha")!;

        Assert.True(observation.Running);
        Assert.Equal("Imaging › Loop · Imaging › Loop › Take Exposure · Imaging › Settle", observation.Operation);
        var activity = observation.Activity!;
        Assert.Same(exposure, activity.Identity);
        Assert.Equal(SequenceActivityKinds.Exposing, activity.Kind);
        Assert.Equal("Take Exposure", activity.Name);
        Assert.Equal("M 31", activity.TargetName);
        Assert.Equal("Ha", activity.Filter);
        Assert.Equal(300, activity.ExposureSeconds);
        Assert.Equal(4, activity.Completed);
        Assert.Null(activity.Total);
    }

    [Fact]
    public void ATimeSpanWaitReportsItsLength()
    {
        var root = new SequenceRootContainer();
        var wait = new WaitForTimeSpan { Name = "Wait", Time = 90, Status = SequenceEntityStatus.RUNNING };
        root.Add(wait);

        var activity = NinaSequenceStatusReader.Read(Mediator(initialized: true, running: true, [wait]))!.Activity!;

        Assert.Equal(SequenceActivityKinds.TimeWait, activity.Kind);
        Assert.Equal(TimeSpan.FromSeconds(90), activity.RunsFor);
        Assert.Null(activity.TargetName);
    }

    [Fact]
    public void ARunningTriggerIsTheActivity()
    {
        var root = new SequenceRootContainer();
        var target = new TargetContainer("Target", "NGC 7000");
        var flip = new TestMeridianFlipTrigger { Status = SequenceEntityStatus.RUNNING };
        var exposure = new TakeExposure { Name = "Take Exposure", Status = SequenceEntityStatus.RUNNING };
        root.Add(target);
        target.Add(flip);
        target.Add(exposure);

        var activity = NinaSequenceStatusReader.Read(Mediator(initialized: true, running: true, [exposure]))!.Activity!;

        Assert.Same(flip, activity.Identity);
        Assert.Equal(SequenceActivityKinds.MeridianFlip, activity.Kind);
        Assert.Equal("NGC 7000", activity.TargetName);
    }

    private static ISequenceMediator Mediator(bool initialized, bool running, ISequenceItem[] items)
    {
        var mediator = DispatchProxy.Create<ISequenceMediator, MediatorProxy>();
        var proxy = (MediatorProxy)(object)mediator;
        proxy.Values["get_Initialized"] = initialized;
        proxy.Values["IsAdvancedSequenceRunning"] = running;
        proxy.Values["GetAdvancedSequencerCurrentRunningItems"] = items.ToList();
        return mediator;
    }

    public class MediatorProxy : DispatchProxy
    {
        public Dictionary<string, object?> Values { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Values.TryGetValue(targetMethod!.Name, out var value)
                ? value
                : throw new NotSupportedException(targetMethod.Name);
    }

    /// <summary>Carries N.I.N.A.'s type name and the properties the reader looks for.</summary>
    private sealed class TakeExposure : SequenceItem
    {
        public double ExposureTime { get; set; }

        public int ExposureCount { get; set; }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) =>
            Task.CompletedTask;

        public override object Clone() => new TakeExposure();
    }

    private sealed class TestMeridianFlipTrigger : SequenceTrigger
    {
        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) =>
            Task.CompletedTask;

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => false;

        public override object Clone() => new TestMeridianFlipTrigger();
    }

    private sealed class TargetContainer : SequenceContainer, IDeepSkyObjectContainer
    {
        public TargetContainer(string name, string targetName)
            : base(new SequentialStrategy())
        {
            Name = name;
            Status = SequenceEntityStatus.RUNNING;
            Target = new InputTarget(Angle.Zero, Angle.Zero, null!) { TargetName = targetName };
        }

        public InputTarget Target { get; set; }

        public NighttimeData NighttimeData => null!;

        public override object Clone() => new TargetContainer(Name, Target.TargetName);
    }
}

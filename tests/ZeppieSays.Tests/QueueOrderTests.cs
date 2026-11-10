using ZeppieSays.Notes;

namespace ZeppieSays.Tests;

public class QueueOrderTests
{
    static List<NoteJob> Queue(params string[] names) =>
        names.Select((n, i) => new NoteJob($@"C:\Notes\{n}.wav") { QueueOrder = i + 1 }).ToList();

    static string Order(IEnumerable<NoteJob> queue) => string.Join(" ", queue.OrderBy(j => j.QueueOrder).Select(j => j.Name));

    [Fact]
    public void Moves_a_recording_above_another()
    {
        var queue = Queue("a", "b", "c", "d");
        var last = Notetaker.Reorder(queue, queue[3], queue[0], after: false);
        Assert.Equal("d a b c", Order(queue));
        Assert.Equal(4, last);
    }

    [Fact]
    public void Moves_a_recording_below_another()
    {
        var queue = Queue("a", "b", "c", "d");
        Notetaker.Reorder(queue, queue[0], queue[2], after: true);
        Assert.Equal("b c a d", Order(queue));
    }

    [Fact]
    public void Without_a_target_it_goes_to_the_end()
    {
        var queue = Queue("a", "b", "c");
        Notetaker.Reorder(queue, queue[0], null, after: true);
        Assert.Equal("b c a", Order(queue));
    }

    [Fact]
    public void Numbers_the_queue_again_when_there_were_gaps()
    {
        var queue = Queue("a", "b", "c");
        queue[1].QueueOrder = 40;
        queue[2].QueueOrder = 90;
        Notetaker.Reorder(queue, queue[2], queue[1], after: false);
        Assert.Equal([1, 3, 2], queue.Select(j => j.QueueOrder));
    }

    [Fact]
    public void Only_unfinished_recordings_are_in_the_queue_and_it_heads_the_list()
    {
        var job = new NoteJob(@"C:\Notes\Call 2026-11-10 09.30.wav");
        Assert.True(job.InQueue);
        Assert.True(job.ListGroup.IsQueue);
        job.Status = NoteStatus.Done;
        Assert.False(job.InQueue);
        Assert.Equal(new ListHeading(job.DayGroup, new DateTime(2026, 11, 10)), job.ListGroup);
        Assert.True(ListHeading.Queue.Day > job.ListGroup.Day);
    }
}

using FileCopy.Client.Transfers;

namespace FileCopy.Client.Tests;

public class TransferQueueTests
{
    [Fact]
    public void ClearFinished_KeepsRunningTransfers()
    {
        var queue = new TransferQueue();
        var done = new TransferItem("a.txt", TransferDirection.Upload, 10);
        var failed = new TransferItem("b.txt", TransferDirection.Download, 10);
        var running = new TransferItem("c.txt", TransferDirection.Upload, 10);
        var retrying = new TransferItem("d.txt", TransferDirection.Download, 10);
        foreach (var item in new[] { done, failed, running, retrying }) queue.Items.Add(item);
        done.Complete();
        failed.Fail("boom");
        retrying.Retrying(1, 4, "timeout");

        queue.ClearFinished();

        Assert.Equal([running, retrying], queue.Items);
    }
}

namespace BabelQueue.Outbox;

/// <summary>
/// Summary of one <see cref="OutboxRelay.FlushAsync"/> pass (or a whole
/// <see cref="OutboxRelay.DrainAsync"/>): how many pending rows were published and how many
/// failed (and were left pending for a later retry).
/// </summary>
/// <param name="Published">Rows the transport accepted and that were marked published.</param>
/// <param name="Failed">Rows whose publish threw; left pending, attempt count bumped.</param>
public sealed record OutboxRelayResult(int Published, int Failed)
{
    /// <summary>Total rows the relay attempted in this pass / run.</summary>
    public int Attempted => Published + Failed;
}

namespace Noogen.Backlog
{
    /// <summary>
    /// The single place a ticket crosses tabs. Because the tab is the state, a transition is a
    /// row move rather than a cell write — two API calls that are not atomic.
    ///
    /// The ordering below is the whole point of this class: <b>append to the destination first,
    /// delete from the source second</b>. An interruption between them leaves the ticket on two
    /// tabs, which <c>doctor</c> detects and a human can fix in seconds. The reverse ordering
    /// would lose the ticket entirely.
    /// </summary>
    public class TicketMover
    {
        readonly SheetIndex _index;

        public TicketMover(SheetIndex index)
        {
            _index = index;
        }

        public async Task MoveAsync(Ticket ticket, BacklogPhase destinationPhase, CancellationToken cancellationToken = default)
        {
            var sourcePhase = ticket.Phase;

            if (!sourcePhase.CanTransitionTo(destinationPhase))
            {
                throw new BacklogTransitionException(
                    $"'{ticket.Id}' cannot move from {sourcePhase.TabName()} to {destinationPhase.TabName()}.");
            }

            var source = await _index.LoadAsync(sourcePhase, cancellationToken);
            var sourceRow = source.FindByIdOrDefault(ticket.Id)
                ?? throw new InvalidOperationException($"'{ticket.Id}' was not found on the {sourcePhase.TabName()} tab.");

            var destination = await _index.LoadAsync(destinationPhase, cancellationToken);

            ticket.Phase = destinationPhase;

            await _index.AppendAsync(destination, ticket, cancellationToken);
            await _index.DeleteRowAsync(source, sourceRow, cancellationToken);
        }
    }
}

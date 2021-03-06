using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.PostgreSql
{
    public abstract class TransactionalStateStorage<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        protected ITransactionMetadataEntity Metadata { get; private set; }
        private List<ITransactionStateEntity<TState>> _states;
        protected IEnumerable<ITransactionStateEntity<TState>> States => _states;

        public async Task<TransactionalStorageLoadResponse<TState>> Load()
        {
            Metadata = await ReadMetadata().ConfigureAwait(false);
            _states = (await ReadStates(Metadata.CommittedSequenceId).ConfigureAwait(false)).ToList();

            if (string.IsNullOrEmpty(Metadata.ETag))
            {
                return new TransactionalStorageLoadResponse<TState>();
            }

            TState committedState;
            if (Metadata.CommittedSequenceId == 0)
            {
                committedState = new TState();
            }
            else
            {
                if (!FindState(Metadata.CommittedSequenceId, out var pos))
                {
                    var error =
                        $"Storage state corrupted: no record for committed state v{Metadata.CommittedSequenceId}";
                    throw new InvalidOperationException(error);
                }

                committedState = _states[pos].Value;
            }

            var prepareRecordsToRecover = _states.Where(x => x.SequenceId > Metadata.CommittedSequenceId)
                .TakeWhile(x => x.TransactionManager.HasValue)
                .Select(x => new PendingTransactionState<TState>
                {
                    SequenceId = x.SequenceId,
                    TransactionManager = x.TransactionManager.Value,
                    State = x.Value,
                    TimeStamp = x.Timestamp.UtcDateTime,
                    TransactionId = x.TransactionId
                })
                .ToArray();

            foreach (var state in _states)
            {
                state.ClearValue();
            }
            
            var metadata = Metadata.Value;
            return new TransactionalStorageLoadResponse<TState>(Metadata.ETag, committedState,
                Metadata.CommittedSequenceId, metadata, prepareRecordsToRecover);
        }

        public async Task<string> Store(string expectedETag, TransactionalStateMetaData metadata,
            List<PendingTransactionState<TState>> statesToPrepare, long? commitUpTo,
            long? abortAfter)
        {
            if (abortAfter.HasValue && _states.Any())
            {
                while (_states.Count > 0 && _states[_states.Count - 1].SequenceId > abortAfter)
                {
                    var entity = _states[_states.Count - 1];
                    await RemoveAbortedState(entity).ConfigureAwait(false);
                    _states.RemoveAt(_states.Count - 1);
                }
            }

            if (statesToPrepare != null)
            {
                foreach (var s in statesToPrepare)
                {
                    ITransactionStateEntity<TState> existingState = null;
                    if (FindState(s.SequenceId, out var pos))
                    {
                        existingState = _states[pos];
                    }

                    var persistedState = await PersistState(s, commitUpTo, existingState).ConfigureAwait(false);
                    if (existingState == null)
                    {
                        _states.Insert(pos, persistedState);
                    }
                    else
                    {
                        _states[pos] = persistedState;
                    }
                }
            }

            Metadata = await PersistMetadata(metadata, commitUpTo ?? Metadata.CommittedSequenceId)
                .ConfigureAwait(false);
            await StoreFinalize(commitUpTo).ConfigureAwait(false);
            return Metadata.ETag;
        }

        private bool FindState(long sequenceId, out int pos)
        {
            pos = 0;
            foreach (var stateSequenceId in _states.Select(x => x.SequenceId))
            {
                switch (stateSequenceId.CompareTo(sequenceId))
                {
                    case 0:
                        return true;
                    case -1:
                        pos++;
                        continue;
                    case 1:
                        return false;
                }
            }

            return false;
        }

        protected abstract Task<ITransactionMetadataEntity> ReadMetadata();
        protected abstract Task<ITransactionStateEntity<TState>[]> ReadStates(long fromSequenceId);

        protected abstract Task<ITransactionStateEntity<TState>> PersistState(PendingTransactionState<TState> pendingState,
            long? commitUpTo,
            ITransactionStateEntity<TState> existingState = null);

        protected abstract Task RemoveAbortedState(ITransactionStateEntity<TState> state);

        protected abstract Task<ITransactionMetadataEntity> PersistMetadata(TransactionalStateMetaData value,
            long commitSequenceId);

        protected virtual Task StoreFinalize(long? commitUpTo) => Task.CompletedTask;
    }
}
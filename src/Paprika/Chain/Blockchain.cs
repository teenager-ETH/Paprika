using System.Buffers;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Merkle;
using Paprika.Store;
using Paprika.Utils;
using BitFilter = Paprika.Data.BitMapFilter<Paprika.Data.BitMapFilter.OfN<Paprika.Data.BitMapFilter.OfNSize128>>;

namespace Paprika.Chain;

/// <summary>
/// The blockchain is the main component of Paprika, that can deal with latest, safe and finalized blocks.
///
/// For latest and safe, it uses a notion of block, that allows switching heads, querying from different heads etc.
/// For the finalized blocks, they are queued to a <see cref="Channel"/> that is consumed by a flushing mechanism
/// using the <see cref="PagedDb"/>.
/// </summary>
public class Blockchain : IAsyncDisposable
{
    // allocate 1024 pages (4MB) at once
    private readonly BufferPool _pool;

    /// <summary>
    /// 512 kb gives 4 million buckets.
    /// </summary>
    private const int BitMapFilterSizePerBlock = 512 * 1024 / Page.PageSize;

    private readonly object _blockLock = new();
    private readonly Dictionary<uint, List<CommittedBlockState>> _blocksByNumber = new();
    private readonly Dictionary<Keccak, CommittedBlockState> _blocksByHash = new();

    private volatile ReadOnlyWorldStateAccessor? _accessor;

    // finalization
    private readonly Channel<CommittedBlockState> _finalizedChannel;
    private readonly Task _flusher;
    private readonly TimeSpan _minFlushDelay;
    private uint _lastFinalized;
    private static readonly TimeSpan DefaultFlushDelay = TimeSpan.FromSeconds(1);

    // metrics
    private readonly Meter _meter;
    private readonly Histogram<int> _flusherBlockPerS;
    private readonly Histogram<int> _flusherBlockApplicationInMs;
    private readonly Histogram<int> _flusherFlushInMs;
    private readonly Counter<long> _bloomMissedReads;
    private readonly Histogram<int> _cacheUsageState;
    private readonly Histogram<int> _cacheUsagePreCommit;
    private readonly Histogram<int> _prefetchCount;
    private readonly MetricsExtensions.IAtomicIntGauge _flusherQueueCount;

    private readonly IDb _db;
    private readonly IPreCommitBehavior _preCommit;
    private readonly CacheBudget.Options _cacheBudgetStateAndStorage;
    private readonly CacheBudget.Options _cacheBudgetPreCommit;
    private readonly Action? _beforeMetricsDisposed;
    private bool _verify;

    public Blockchain(IDb db, IPreCommitBehavior preCommit, TimeSpan? minFlushDelay = null,
        CacheBudget.Options cacheBudgetStateAndStorage = default,
        CacheBudget.Options cacheBudgetPreCommit = default,
        int? finalizationQueueLimit = null, Action? beforeMetricsDisposed = null)
    {
        _db = db;
        _preCommit = preCommit;
        _cacheBudgetStateAndStorage = cacheBudgetStateAndStorage;
        _cacheBudgetPreCommit = cacheBudgetPreCommit;
        _minFlushDelay = minFlushDelay ?? DefaultFlushDelay;
        _beforeMetricsDisposed = beforeMetricsDisposed;

        _finalizedChannel = CreateChannel(finalizationQueueLimit);
        Debug.Assert(_finalizedChannel.Reader is { CanCount: true, CanPeek: true }, "Should be able to peek and count");

        _flusher = FlusherTask();

        // metrics
        _meter = new Meter("Paprika.Chain.Blockchain");

        _flusherBlockPerS = _meter.CreateHistogram<int>("Blocks stored / s", "Blocks/s",
            "The number of blocks stored by the flushing task in one second");
        _flusherBlockApplicationInMs = _meter.CreateHistogram<int>("Block data application in ms", "ms",
            "The amortized time it takes for one block to apply on PagedDb");
        _flusherFlushInMs = _meter.CreateHistogram<int>("FSYNC time", "ms",
            "The time it took to synchronize the file");
        _flusherQueueCount = _meter.CreateAtomicObservableGauge("Flusher queue size", "Blocks",
            "The number of the blocks in the flush queue");
        _bloomMissedReads = _meter.CreateCounter<long>("Bloom missed reads", "Reads",
            "Number of reads that passed bloom but missed in dictionary");
        _cacheUsageState = _meter.CreateHistogram<int>("State transient cache usage per commit", "%",
            "How much used was the transient cache");
        _cacheUsagePreCommit = _meter.CreateHistogram<int>("PreCommit transient cache usage per commit", "%",
            "How much used was the transient cache");
        _prefetchCount = _meter.CreateHistogram<int>("Prefetch count",
            "Key count", "Keys prefetched in the background by the prefetcher");

        // pool
        _pool = new(1024, BufferPool.PageTracking.AssertCount, _meter);

        using var batch = _db.BeginReadOnlyBatch();
        _lastFinalized = batch.Metadata.BlockNumber;
    }

    public void VerifyDbIntegrityOnCommit()
    {
        _verify = true;
    }

    public int PoolAllocatedMB => _pool.AllocatedMB ?? int.MaxValue;

    private static Channel<CommittedBlockState> CreateChannel(int? finalizationQueueLimit)
    {
        if (finalizationQueueLimit == null)
        {
            return Channel.CreateUnbounded<CommittedBlockState>(new UnboundedChannelOptions
            {
                // Don't make the single reader to allow counting
                // SingleReader = true,
                SingleWriter = true,
            });
        }

        return Channel.CreateBounded<CommittedBlockState>(
            new BoundedChannelOptions(finalizationQueueLimit.Value)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
    }

    /// <summary>
    /// The flusher method run as a reader of the <see cref="_finalizedChannel"/>.
    /// </summary>
    private async Task FlusherTask()
    {
        var reader = _finalizedChannel.Reader;

        try
        {
            while (await reader.WaitToReadAsync())
            {
                var flushed = new List<uint>();
                var timer = Stopwatch.StartNew();

                (uint _blocksByNumber, Keccak blockHash) last = default;

                while (timer.Elapsed < _minFlushDelay && reader.TryRead(out var block))
                {
                    last = (block.BlockNumber, block.Hash);

                    using var batch = _db.BeginNextBatch();

                    if (_verify)
                    {
                        batch.VerifyDbPagesOnCommit();
                    }

                    // apply
                    var application = Stopwatch.StartNew();

                    flushed.Add(block.BlockNumber);

                    var flushedTo = block.BlockNumber;

                    batch.SetMetadata(block.BlockNumber, block.Hash);

                    block.Apply(batch);

                    // only for debugging if needed
                    //block.Assert(batch);

                    application.Stop();
                    _flusherBlockApplicationInMs.Record((int)application.ElapsedMilliseconds);

                    // If there's something in the queue, don't flush. Flush here only where there's nothing to read from the reader.
                    var readerCount = reader.Count;

                    _flusherQueueCount.Set(readerCount);

                    var noMoreBlocksToApply = readerCount == 0;

                    // Commit, but flush only if there's nothing more to apply.
                    // If there are more blocks, leave it to the external _db.Flush() called outside of this loop.
                    await batch.Commit(noMoreBlocksToApply
                        ? CommitOptions.FlushDataOnly
                        : CommitOptions.DangerNoFlush);

                    // inform blocks about flushing
                    lock (_blockLock)
                    {
                        if (!_blocksByNumber.TryGetValue(flushedTo, out var removedBlocks))
                        {
                            ThrowMissingBlocks(flushedTo);
                        }

                        var cloned = removedBlocks.ToArray();

                        _accessor?.OnCommitToDatabase(block, cloned);

                        foreach (var removedBlock in cloned)
                        {
                            // dispose one to allow leases to do the count
                            removedBlock.Dispose();
                        }
                    }
                }

                timer.Stop();

                // measure
                var count = flushed.Count;

                if (count == 0)
                {
                    // nothing
                    continue;
                }

                var flushWatch = Stopwatch.StartNew();

                _db.Flush();

                _flusherFlushInMs.Record((int)flushWatch.ElapsedMilliseconds);

                Flushed?.Invoke(this, last);

                if (timer.ElapsedMilliseconds > 0)
                {
                    _flusherBlockPerS.Record((int)(count * 1000 / timer.ElapsedMilliseconds));
                }
            }
        }
        catch (Exception e)
        {
            FlusherFailure?.Invoke(this, e);
            Console.WriteLine(e);
            throw;
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowMissingBlocks(uint flushedTo)
        {
            throw new Exception($"Missing blocks at block number {flushedTo}");
        }
    }

    /// <summary>
    /// Announces the last block number that was flushed to disk.
    /// </summary>
    public event EventHandler<(uint blockNumber, Keccak blockHash)> Flushed;

    /// <summary>
    /// The flusher failed.
    /// </summary>
    public event EventHandler<Exception> FlusherFailure;

    private void Add(CommittedBlockState state)
    {
        // allocate before lock
        var list = new List<CommittedBlockState> { state };

        lock (_blockLock)
        {
            if (_blocksByHash.TryGetValue(state.Hash, out var committed))
            {
                if (committed.BlockNumber == state.BlockNumber)
                {
                    // There is an already existing state at the same block number.
                    // Just accept it and dispose the added.
                    state.MakeDiscardable();

                    state.Dispose();
                    return;
                }
            }

            // blocks by number first
            ref var blocks =
                ref CollectionsMarshal.GetValueRefOrAddDefault(_blocksByNumber, state.BlockNumber, out var exists);

            if (exists == false)
            {
                blocks = list;
            }
            else
            {
                blocks!.Add(state);
            }

            // blocks by hash
            _blocksByHash.Add(state.Hash, state);

            _accessor?.OnCommitToBlockchain(state.Hash);
        }
    }

    private void Remove(CommittedBlockState blockState)
    {
        lock (_blockLock)
        {
            // blocks by number, use remove first as usually it should be the case
            if (!_blocksByNumber.Remove(blockState.BlockNumber, out var blocks))
            {
                ThrowBlocksNotFound(blockState);
                return;
            }

            blocks.Remove(blockState);
            if (blocks.Count > 0)
            {
                // re-add only if not empty
                _blocksByNumber.Add(blockState.BlockNumber, blocks);
            }

            // blocks by hash
            _blocksByHash.Remove(blockState.Hash);
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowBlocksNotFound(CommittedBlockState blockState)
        {
            throw new Exception($"Blocks @ {blockState.BlockNumber} should not be empty");
        }
    }

    public IWorldState StartNew(Keccak parentKeccak)
    {
        var (batch, ancestors) = BuildBlockDataDependencies(parentKeccak);
        return new BlockState(parentKeccak, batch, ancestors, this);
    }

    public IRawState StartRaw()
    {
        return new RawState(this, _db);
    }

    public IReadOnlyWorldState StartReadOnly(Keccak keccak)
    {
        var (batch, ancestors) = BuildBlockDataDependencies(keccak);
        var filter = CreateAncestorsFilter(ancestors);

        return new ReadOnlyState(keccak, new ReadOnlyBatchCountingRefs(batch), ancestors, filter, _pool);
    }

    public static IReadOnlyWorldState StartReadOnlyLatestFromDb(IDb db)
    {
        var batch = db.BeginReadOnlyBatch($"Blockchain dependency LATEST");
        return new ReadOnlyState(batch.Metadata.StateHash, new ReadOnlyBatchCountingRefs(batch), []);
    }

    public IReadOnlyWorldState StartReadOnlyLatestFromDb() => StartReadOnlyLatestFromDb(_db);

    private (IReadOnlyBatch batch, CommittedBlockState[] ancestors) BuildBlockDataDependencies(Keccak parentKeccak)
    {
        parentKeccak = Normalize(parentKeccak);

        if (parentKeccak == Keccak.Zero)
        {
            return (EmptyReadOnlyBatch.Instance, []);
        }

        lock (_blockLock)
        {
            // the most recent finalized batch
            var batch = _db.BeginReadOnlyBatchOrLatest(parentKeccak, "Blockchain dependency");

            // batch matches the parent, return
            try
            {
                var ancestors = FindAncestors(parentKeccak, batch);
                return (batch, ancestors);
            }
            catch
            {
                batch.Dispose();
                throw;
            }
        }
    }

    private CommittedBlockState[] FindAncestors(in Keccak keccak, IReadOnlyBatch batch)
    {
        if (batch.Metadata.StateHash == keccak)
        {
            return [];
        }

        var parent = keccak;

        // no match, find chain
        var ancestors = new List<CommittedBlockState>();
        while (batch.Metadata.StateHash != parent)
        {
            if (_blocksByHash.TryGetValue(parent, out var ancestor) == false)
            {
                ThrowParentStateNotFound(parent);
            }

            ancestor.AcquireLease(); // lease it!
            ancestors.Add(ancestor);
            parent = Normalize(ancestor.ParentHash);
        }

        return ancestors.ToArray();

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowParentStateNotFound(in Keccak parentKeccak)
        {
            throw new Exception(
                $"Failed to build dependencies. Parent state with hash {parentKeccak} was not found");
        }
    }

    private static Keccak Normalize(in Keccak keccak)
    {
        // pages are zeroed before, return zero on empty tree
        return keccak == Keccak.EmptyTreeHash ? Keccak.Zero : keccak;
    }

    public void Finalize(Keccak keccak)
    {
        Stack<CommittedBlockState> finalized;
        uint count;

        // gather all the blocks to finalize
        lock (_blockLock)
        {
            if (_blocksByHash.TryGetValue(keccak, out var block) == false)
            {
                ThrowFinalizedBlockMissing();
            }

            Debug.Assert(block.BlockNumber > _lastFinalized,
                "Block that is finalized should have a higher number than the last finalized");

            // gather all the blocks between last finalized and this.

            count = block.BlockNumber - _lastFinalized;

            finalized = new((int)count);
            for (var blockNumber = block.BlockNumber; blockNumber > _lastFinalized; blockNumber--)
            {
                // no need to acquire lease here, the block is already leased for the blockchain before Add(block)
                finalized.Push(block);
                if (_blocksByHash.TryGetValue(block.ParentHash, out block) == false)
                {
                    break;
                }
            }

            _lastFinalized += count;
        }

        // push them!
        var writer = _finalizedChannel.Writer;

        while (finalized.TryPop(out var block))
        {
            if (writer.TryWrite(block) == false)
            {
                // hard spin wait on breaching the size
                SpinWait.SpinUntil(() => writer.TryWrite(block));
            }
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowFinalizedBlockMissing()
        {
            throw new Exception("Block that is marked as finalized is not present");
        }
    }

    private BitFilter CreateBitFilter() => BitMapFilter.CreateOfN<BitMapFilter.OfNSize128>(_pool);

    /// <summary>
    /// Represents a block that is a result of ExecutionPayload.
    /// </summary>
    private class BlockState : RefCountingDisposable, IWorldState, ICommitWithStats, IProvideDescription, IStateStats
    {
        /// <summary>
        /// A simple set filter to assert whether the given key was set in a given block, used to speed up getting the keys.
        /// </summary>
        private readonly BitFilter _filter;

        // stats
        private readonly HashSet<Keccak> _touchedAccounts = new();
        private readonly Dictionary<Keccak, (List<Keccak> set, List<Keccak> deleted)> _storageSlots = new();

        /// <summary>
        /// Stores information about contracts that should have their previous incarnations destroyed.
        /// </summary>
        private HashSet<Keccak>? _destroyed;

        private readonly ReadOnlyBatchCountingRefs _batch;
        private readonly CommittedBlockState[] _ancestors;
        private readonly BitFilter? _ancestorsFilter;

        private readonly Blockchain _blockchain;

        /// <summary>
        /// The maps mapping accounts information, written in this block.
        /// </summary>
        private PooledSpanDictionary _state = null!;

        /// <summary>
        /// The maps mapping storage information, written in this block.
        /// </summary>
        private PooledSpanDictionary _storage = null!;

        /// <summary>
        /// The values set the <see cref="IPreCommitBehavior"/> during the <see cref="ICommit.Visit"/> invocation.
        /// It's both storage & state as it's metadata for the pre-commit behavior.
        /// </summary>
        private PooledSpanDictionary _preCommit = null!;

        private PreCommitPrefetcher? _prefetcher;

        private readonly DelayedMetrics.DelayedCounter<long, DelayedMetrics.LongIncrement> _xorMissed;
        private readonly CacheBudget _cacheBudgetStorageAndStage;
        private readonly CacheBudget _cacheBudgetPreCommit;

        private Keccak? _hash;

        private int _dbReads;

        public BlockState(Keccak parentStateRoot, IReadOnlyBatch batch, CommittedBlockState[] ancestors,
            Blockchain blockchain)
        {
            _batch = new ReadOnlyBatchCountingRefs(batch);

            _ancestors = ancestors;

            // ancestors filter
            _ancestorsFilter = blockchain.CreateAncestorsFilter(ancestors);
            _blockchain = blockchain;

            ParentHash = parentStateRoot;

            _filter = _blockchain.CreateBitFilter();
            _destroyed = null;
            _hash = ParentHash;

            _cacheBudgetStorageAndStage = blockchain._cacheBudgetStateAndStorage.Build();
            _cacheBudgetPreCommit = blockchain._cacheBudgetPreCommit.Build();

            _xorMissed = _blockchain._bloomMissedReads.Delay();

            CreateDictionaries();
        }

        private void CreateDictionaries()
        {
            CreateDict(ref _state, Pool);
            CreateDict(ref _storage, Pool);
            CreateDict(ref _preCommit, Pool);
            return;

            // as pre-commit can use parallelism, make the pooled dictionaries concurrent friendly:
            // 1. make the dictionary preserve once written values, which means that it can repeatedly read and set without worrying of ordering operations
            // 2. set dictionary so that it allows concurrent readers
            static void CreateDict(ref PooledSpanDictionary dict, BufferPool pool)
            {
                // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                // ReSharper disable once UseNullPropagation
                if (dict != null)
                {
                    // dispose previous
                    dict.Dispose();
                }

                dict = new PooledSpanDictionary(pool, true);
            }
        }

        public Keccak ParentHash { get; }


        /// <summary>
        /// Commits the block to the blockchain.
        /// </summary>
        public Keccak Commit(uint blockNumber)
        {
            var committed = CommitImpl(blockNumber, false);

            ReportCacheUsage(_blockchain._cacheBudgetStateAndStorage, _cacheBudgetStorageAndStage,
                _blockchain._cacheUsageState);
            ReportCacheUsage(_blockchain._cacheBudgetPreCommit, _cacheBudgetPreCommit,
                _blockchain._cacheUsagePreCommit);

            if (committed != null)
            {
                _blockchain.Add(committed);
            }

            return Hash;
        }

        /// <summary>
        /// Reports the given cache usage.
        /// </summary>
        private static void ReportCacheUsage(in CacheBudget.Options budget, CacheBudget actual, Histogram<int> reportTo)
        {
            var total = budget.EntriesPerBlock;
            if (total <= 0)
            {
                // disabled, nothing to report
                return;
            }

            var percentageLeft = (double)actual.BudgetLeft / total * 100;
            var percentageUsed = 100 - percentageLeft;

            reportTo.Record((int)percentageUsed);
        }

        private CommittedBlockState? CommitImpl(uint blockNumber, bool raw)
        {
            if (_prefetcher != null)
            {
                _prefetcher.BlockFurtherPrefetching();
                _blockchain._prefetchCount.Record(_prefetcher.PrefetchCount);
            }

            EnsureHash();

            var hash = _hash!.Value;

            if (hash == ParentHash)
            {
                if (hash == Keccak.EmptyTreeHash)
                {
                    return null;
                }

                ThrowSameState();
            }

            BlockNumber = blockNumber;

            var filter = _blockchain.CreateBitFilter();

            // clean no longer used fields
            var data = new PooledSpanDictionary(Pool, false);

            // use append for faster copies as state and storage won't overwrite each other
            _state.CopyTo(data, OmitUseOnce, filter, true);
            _storage.CopyTo(data, OmitUseOnce, filter, true);

            // TODO: apply InspectBeforeApply here to reduce memory usage?
            _preCommit.CopyTo(data, OmitUseOnce, filter);

            // Creation acquires the lease
            return new CommittedBlockState(filter, _destroyed, _blockchain, data, hash,
                ParentHash,
                blockNumber, raw);

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowSameState()
            {
                throw new Exception("The same state as the parent is not handled now");
            }
        }

        /// <summary>
        /// Filters out entries that are of type <see cref="EntryType.UseOnce"/> as they should be used once only.
        /// </summary>
        private static bool OmitUseOnce(byte metadata) => metadata != (int)EntryType.UseOnce;

        /// <summary>
        /// Applies this state directly on the <see cref="IBatch"/>
        /// without creating an in-memory representation of the committed state.
        /// </summary>
        public void ApplyRaw(IBatch batch)
        {
            _prefetcher?.BlockFurtherPrefetching();

            EnsureHash();

            var hash = _hash!.Value;

            var earlyReturn = false;

            if (hash == ParentHash)
            {
                if (hash == Keccak.EmptyTreeHash)
                {
                    earlyReturn = true;
                }
            }

            if (earlyReturn)
            {
                return;
            }

            ApplyImpl(batch, _state, _blockchain);
            ApplyImpl(batch, _storage, _blockchain);
            ApplyImpl(batch, _preCommit, _blockchain);
        }

        public void Reset()
        {
            _hash = ParentHash;
            _filter.Clear();
            _destroyed = null;

            CreateDictionaries();
        }

        IStateStats IWorldState.Stats => this;

        public IPreCommitPrefetcher? OpenPrefetcher()
        {
            if (_prefetcher != null)
            {
                throw new Exception("Prefetching already started");
            }

            if (_blockchain._preCommit.CanPrefetch)
            {
                return _prefetcher = new PreCommitPrefetcher(_preCommit, this, _blockchain._pool);
            }

            return null;
        }

        private class PreCommitPrefetcher : IDisposable, IPreCommitPrefetcher, IPrefetcherContext, IThreadPoolWorkItem
        {
            private volatile bool _prefetchPossible = true;

            private readonly ConcurrentQueue<(Keccak, Keccak)> _items = new();
            private readonly BitFilter _prefetched;
            private readonly PooledSpanDictionary _cache;
            private readonly BlockState _parent;
            private readonly BufferPool _pool;

            private const int Working = 1;
            private const int NotWorking = 0;
            private volatile int _working = NotWorking;
            private readonly Page _workspace;

            private static readonly Keccak JustAccount = Keccak.Zero;

            public PreCommitPrefetcher(PooledSpanDictionary cache, BlockState parent, BufferPool pool)
            {
                _cache = cache;
                _parent = parent;
                _pool = pool;
                _prefetched = _parent._blockchain.CreateBitFilter();
                _workspace = pool.Rent(false);
            }

            public bool CanPrefetchFurther => _prefetchPossible;

            public void PrefetchAccount(in Keccak account)
            {
                if (CanPrefetchFurther == false)
                    return;

                var accountHash = account.GetHashCodeUlong();

                if (ShouldPrefetch(accountHash) == false)
                {
                    return;
                }

                _items.Enqueue((account, JustAccount));
                EnsureRunning();
            }

            private void EnsureRunning()
            {
                if (_working == NotWorking)
                {
                    if (Interlocked.CompareExchange(ref _working, Working, NotWorking) == NotWorking)
                    {
                        ThreadPool.UnsafeQueueUserWorkItem(this, false);
                    }
                }
            }

            public void SpinTillPrefetchDone()
            {
                SpinWait.SpinUntil(() => _working == NotWorking);
            }

            private bool ShouldPrefetch(ulong hash) => _prefetched.AddAtomic(hash);

            public void PrefetchStorage(in Keccak account, in Keccak storage)
            {
                if (CanPrefetchFurther == false)
                    return;

                // Try account first
                var accountHash = account.GetHashCodeUlong();
                var prefetchAccount = ShouldPrefetch(accountHash);

                if (prefetchAccount)
                {
                    _items.Enqueue((account, JustAccount));
                }

                var storageHash = storage.GetHashCodeUlong();
                var prefetchStorage = ShouldPrefetch(accountHash ^ storageHash);

                if (prefetchStorage)
                {
                    _items.Enqueue((account, storage));
                }

                if (prefetchStorage || prefetchAccount)
                {
                    EnsureRunning();
                }
            }

            void IThreadPoolWorkItem.Execute()
            {
                while (_items.TryDequeue(out (Keccak account, Keccak storage) item))
                {
                    lock (_cache)
                    {
                        if (_prefetchPossible == false)
                        {
                            // We leave _working set to Working so that next Prefetch operations
                            // never ensure that a task is running.
                            return;
                        }

                        if (item.storage.Equals(JustAccount))
                        {
                            PreCommit.Prefetch(item.account, this);
                        }
                        else
                        {
                            PreCommit.Prefetch(item.account, item.storage, this);
                        }
                    }
                }

                _working = NotWorking;
            }

            private IPreCommitBehavior PreCommit => _parent._blockchain._preCommit;

            public int PrefetchCount { get; private set; }

            public void BlockFurtherPrefetching()
            {
                lock (_cache)
                {
                    // Just set the prefetch possible to false and return.
                    // As every operation in IThreadPoolWorkItem.Execute takes this lock, it's safe.
                    // This has one additional benefit. There's no need to worry about whether a worker runs or not atm.
                    _prefetchPossible = false;
                }
            }

            [SkipLocalsInit]
            public ReadOnlySpanOwner<byte> Get(scoped in Key key, TransformPrefetchedData transform)
            {
                if (CanPrefetchFurther == false)
                {
                    // Nothing more to do
                    return default;
                }

                var hash = GetHash(key);
                var keyWritten = key.WriteTo(stackalloc byte[key.MaxByteLength]);

                if (_cache.TryGet(keyWritten, hash, out var cached))
                {
                    return new ReadOnlySpanOwner<byte>(cached, null);
                }

                if (CanPrefetchFurther == false)
                {
                    // Cannot, return
                    return default;
                }

                // We can prefetch so scan the ancestors. No using as we'll return it
                var ancestor = _parent.TryGetAncestors(key, keyWritten, hash);

                var span = ancestor.Span;

                // Transform data before storing them in the cache. This is done so that Decompress for example is run on
                // this thread, no on the one that marks paths as dirty.
                var transformed = transform(span, _workspace.Span, out var entryType);

                // Store the transformed so that, if a buffer reuse occurs in the transform it can be done before the next one is called.
                _cache.Set(keyWritten, hash, transformed, (byte)entryType);
                _parent._filter.AddAtomic(hash);
                PrefetchCount++;

                // The data are in cache, but it's easier and faster to return the owner from ancestors.
                if (ancestor.IsEmpty)
                {
                    // An empty ancestor can be disposed fast, and return immediately.
                    ancestor.Dispose();
                    return default;
                }

                // No dispose, the owner must live.
                return ancestor.Owner;
            }

            public void Dispose()
            {
                _pool.Return(_workspace);
                _prefetched.Return(_pool);
            }
        }

        public uint BlockNumber { get; private set; }

        public Keccak Hash
        {
            get
            {
                EnsureHash();
                return _hash!.Value;
            }
        }

        private void EnsureHash()
        {
            if (_hash == null)
            {
                _hash = _blockchain._preCommit.BeforeCommit(this, _cacheBudgetPreCommit);
            }
        }

        private BufferPool Pool => _blockchain._pool;

        [SkipLocalsInit]
        public void DestroyAccount(in Keccak address)
        {
            _hash = null;

            _touchedAccounts.Add(address);

            var searched = NibblePath.FromKey(address);

            var account = Key.Account(address);

            // set account to empty first
            _state.Set(account.WriteTo(stackalloc byte[account.MaxByteLength]), GetHash(account),
                ReadOnlySpan<byte>.Empty, (byte)EntryType.Persistent);

            Destroy(searched, _storage);
            Destroy(searched, _preCommit);

            _storageSlots.Remove(address);

            _destroyed ??= new HashSet<Keccak>();
            _destroyed.Add(address);

            _blockchain._preCommit.OnAccountDestroyed(address, this);

            return;

            static void Destroy(NibblePath searched, PooledSpanDictionary dict)
            {
                foreach (var kvp in dict)
                {
                    Key.ReadFrom(kvp.Key, out var key);
                    if (key.Path.Equals(searched))
                    {
                        kvp.Destroy();
                    }
                }
            }
        }

        public Span<byte> GetStorage(in Keccak address, in Keccak storage, Span<byte> destination)
        {
            var key = Key.StorageCell(NibblePath.FromKey(address), storage);

            using var owner = Get(key);

            TryCache(key, owner, _storage);

            // check the span emptiness
            var data = owner.Span;
            if (data.IsEmpty)
                return Span<byte>.Empty;

            data.CopyTo(destination);
            return destination.Slice(0, data.Length);
        }

        /// <summary>
        /// Decides to whether put the value in a transient cache or in a persistent cache to speed
        /// up queries in next executions.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="owner"></param>
        /// <param name="dict"></param>
        /// <exception cref="NotImplementedException"></exception>
        private void TryCache(in Key key, in ReadOnlySpanOwnerWithMetadata<byte> owner, PooledSpanDictionary dict)
        {
            if (_cacheBudgetStorageAndStage.ShouldCache(owner))
            {
                SetImpl(key, owner.Span, EntryType.Cached, dict);
            }
        }

        public Account GetAccount(in Keccak address)
        {
            var key = Key.Account(NibblePath.FromKey(address));

            using var owner = Get(key);

            TryCache(key, owner, _state);

            // check the span emptiness
            if (owner.Span.IsEmpty)
                return new Account(0, 0);

            Account.ReadFrom(owner.Span, out var result);
            return result;
        }

        [SkipLocalsInit]
        public void SetAccount(in Keccak address, in Account account, bool newAccountHint = false)
        {
            var payload = account.WriteTo(stackalloc byte[Account.MaxByteCount]);
            SetAccountRaw(address, payload, newAccountHint);
        }

        private void SetAccountRaw(in Keccak address, Span<byte> payload, bool newAccountHint)
        {
            var path = NibblePath.FromKey(address);
            var key = Key.Account(path);

            SetImpl(key, payload, EntryType.Persistent, _state);

            if (newAccountHint)
            {
                _blockchain._preCommit.OnNewAccountCreated(address, this);
            }

            _touchedAccounts.Add(address);
        }

        public void SetStorage(in Keccak address, in Keccak storage, ReadOnlySpan<byte> value)
        {
            SetStorageImpl(address, storage, value, EnsureStorageStats(address));
        }

        private void SetStorageImpl(in Keccak address, in Keccak storage, ReadOnlySpan<byte> value,
            (List<Keccak> set, List<Keccak> deleted) stats)
        {
            var path = NibblePath.FromKey(address);
            var key = Key.StorageCell(path, storage);

            SetImpl(key, value, EntryType.Persistent, _storage);

            (value.IsEmpty ? stats.deleted : stats.set).Add(storage);
        }

        private (List<Keccak> set, List<Keccak> deleted) EnsureStorageStats(Keccak address)
        {
            _touchedAccounts.Add(address);
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, address, out var exists);
            if (exists == false)
            {
                slot = (new List<Keccak>(), new List<Keccak>());
            }

            return slot;
        }

        public IStorageSetter GetStorageSetter(in Keccak address) =>
            new StorageSetter(this, address, EnsureStorageStats(address));

        private sealed class StorageSetter(
            BlockState state,
            Keccak address,
            (List<Keccak> set, List<Keccak> deleted) stats) : IStorageSetter
        {
            public void SetStorage(in Keccak storage, ReadOnlySpan<byte> value) =>
                state.SetStorageImpl(address, storage, value, stats);
        }


        [SkipLocalsInit]
        private void SetImpl(in Key key, in ReadOnlySpan<byte> payload, EntryType type, PooledSpanDictionary dict)
        {
            // clean precalculated hash
            _hash = null;

            var hash = GetHash(key);
            AddToFilter(hash);

            var k = key.WriteTo(stackalloc byte[key.MaxByteLength]);
            dict.Set(k, hash, payload, (byte)type);
        }

        private void AddToFilter(ulong hash)
        {
            _filter.AddAtomic(hash);
        }

        private void SetImpl(in Key key, in ReadOnlySpan<byte> payload0, in ReadOnlySpan<byte> payload1,
            EntryType type,
            PooledSpanDictionary dict)
        {
            // clean precalculated hash
            _hash = null;

            var hash = GetHash(key);
            AddToFilter(hash);

            var k = key.WriteTo(stackalloc byte[key.MaxByteLength]);

            dict.Set(k, hash, payload0, payload1, (byte)type);
        }

        ReadOnlySpanOwnerWithMetadata<byte> ICommit.Get(scoped in Key key) => Get(key);

        void ICommit.Set(in Key key, in ReadOnlySpan<byte> payload, EntryType type) =>
            SetImpl(key, payload, type, _preCommit);

        void ICommit.Set(in Key key, in ReadOnlySpan<byte> payload0, in ReadOnlySpan<byte> payload1, EntryType type) =>
            SetImpl(key, payload0, payload1, type, _preCommit);

        public string Describe(Key.Predicate? predicate = null)
        {
            var writer = new StringWriter();
            var indented = new IndentedTextWriter(writer);
            indented.Indent = 1;

            writer.WriteLine("State:");
            _state.Describe(indented, predicate);

            writer.WriteLine("Storage:");
            _storage.Describe(indented, predicate);

            writer.WriteLine("PreCommit:");
            _preCommit.Describe(indented, predicate);

            return writer.ToString();
        }

        void ICommit.Visit(CommitAction action, TrieType type)
        {
            var dict = type == TrieType.State ? _state : _storage;

            foreach (var kvp in dict)
            {
                if (kvp.Metadata == (byte)EntryType.Persistent)
                {
                    Key.ReadFrom(kvp.Key, out var key);
                    action(key, kvp.Value);
                }
            }
        }

        IChildCommit ICommit.GetChild() => new ChildCommit(Pool, this);

        public IReadOnlySet<Keccak> TouchedAccounts => _touchedAccounts;

        public IReadOnlyDictionary<Keccak, (List<Keccak> set, List<Keccak> deleted)> TouchedStorageSlots =>
            _storageSlots;

        class ChildCommit(BufferPool pool, ICommit parent) : RefCountingDisposable, IChildCommit
        {
            private readonly PooledSpanDictionary _dict = new(pool, true);

            [SkipLocalsInit]
            public ReadOnlySpanOwnerWithMetadata<byte> Get(scoped in Key key)
            {
                var hash = GetHash(key);
                var keyWritten = key.WriteTo(stackalloc byte[key.MaxByteLength]);

                if (_dict.TryGet(keyWritten, hash, out var result))
                {
                    AcquireLease();
                    return new ReadOnlySpanOwnerWithMetadata<byte>(new ReadOnlySpanOwner<byte>(result, this), 0);
                }

                // Don't nest, as reaching to parent should be easy.
                return parent.Get(key);
            }

            [SkipLocalsInit]
            public void Set(in Key key, in ReadOnlySpan<byte> payload, EntryType type)
            {
                var hash = GetHash(key);
                var keyWritten = key.WriteTo(stackalloc byte[key.MaxByteLength]);

                _dict.Set(keyWritten, hash, payload, (byte)type);
            }

            [SkipLocalsInit]
            public void Set(in Key key, in ReadOnlySpan<byte> payload0, in ReadOnlySpan<byte> payload1, EntryType type)
            {
                var hash = GetHash(key);
                var keyWritten = key.WriteTo(stackalloc byte[key.MaxByteLength]);

                _dict.Set(keyWritten, hash, payload0, payload1, (byte)type);
            }

            public void Commit()
            {
                foreach (var kvp in _dict)
                {
                    Key.ReadFrom(kvp.Key, out var key);
                    var type = (EntryType)kvp.Metadata;

                    // flush down only volatiles
                    if (type != EntryType.UseOnce)
                    {
                        parent.Set(key, kvp.Value, type);
                    }
                }
            }

            public IChildCommit GetChild() => new ChildCommit(pool, this);

            protected override void CleanUp()
            {
                _dict.Dispose();
            }

            public override string ToString() => _dict.ToString();
        }

        [SkipLocalsInit]
        private ReadOnlySpanOwnerWithMetadata<byte> Get(scoped in Key key)
        {
            var hash = GetHash(key);
            var keyWritten = key.WriteTo(stackalloc byte[key.MaxByteLength]);

            return TryGet(key, keyWritten, hash);
        }

        /// <summary>
        /// A recursive search through the block and its parent until null is found at the end of the weekly referenced
        /// chain.
        /// </summary>
        private ReadOnlySpanOwnerWithMetadata<byte> TryGet(scoped in Key key, scoped ReadOnlySpan<byte> keyWritten,
            ulong bloom)
        {
            var owner = TryGetLocal(key, keyWritten, bloom, out var succeeded);
            if (succeeded)
                return owner.WithDepth(0);

            return TryGetAncestors(key, keyWritten, bloom);
        }

        private ReadOnlySpanOwnerWithMetadata<byte> TryGetAncestors(scoped in Key key,
            scoped ReadOnlySpan<byte> keyWritten, ulong keyHash)
        {
            var destroyedHash = CommittedBlockState.GetDestroyedHash(key);

            if (_ancestorsFilter.HasValue && _ancestorsFilter.GetValueOrDefault().MayContainAny(keyHash, destroyedHash))
            {
                ushort depth = 1;

                // Walk through the ancestors only if the filter shows that they may contain the value
                foreach (var ancestor in _ancestors)
                {
                    var owner = ancestor.TryGetLocal(key, keyWritten, keyHash, destroyedHash, out var succeeded);
                    if (succeeded)
                        return owner.WithDepth(depth);

                    depth++;
                }
            }

            return TryGetDatabase(key);
        }

        [SkipLocalsInit]
        private ReadOnlySpanOwnerWithMetadata<byte> TryGetDatabase(scoped in Key key)
        {
            // report db read
            Interlocked.Increment(ref _dbReads);

            if (_batch.TryGet(key, out var span))
            {
                // return leased batch
                _batch.AcquireLease();
                return new ReadOnlySpanOwner<byte>(span, _batch).FromDatabase();
            }

            // Return default as the value does not exist
            return default;
        }

        /// <summary>
        /// Tries to get the key only from this block, acquiring no lease as it assumes that the lease is taken.
        /// </summary>
        private ReadOnlySpanOwner<byte> TryGetLocal(scoped in Key key, scoped ReadOnlySpan<byte> keyWritten,
            ulong bloom, out bool succeeded)
        {
            var mayHave = _filter.MayContain(bloom);

            // check if the change is in the block
            if (!mayHave)
            {
                // if destroyed, return false as no previous one will contain it
                if (IsAccountDestroyed(key))
                {
                    succeeded = true;
                    return default;
                }

                succeeded = false;
                return default;
            }

            // First always try pre-commit as it may overwrite data.
            // Don't do it for the storage though! StorageCell entries are not modified by pre-commit! It can only read them!
            if (key.Type != DataType.StorageCell && _preCommit.TryGet(keyWritten, bloom, out var span))
            {
                // return with owned lease
                succeeded = true;
                AcquireLease();
                return new ReadOnlySpanOwner<byte>(span, this);
            }

            return TryGetLocalDict(key, keyWritten, bloom, out succeeded);
        }

        private ReadOnlySpanOwner<byte> TryGetLocalDict(scoped in Key key, scoped ReadOnlySpan<byte> keyWritten,
            ulong bloom, out bool succeeded)
        {
            // select the map to search for
            var dict = key.Type switch
            {
                DataType.Account => _state,
                DataType.StorageCell => _storage,
                _ => null
            };

            if (dict is not null && dict.TryGet(keyWritten, bloom, out var span))
            {
                // return with owned lease
                succeeded = true;
                AcquireLease();
                return new ReadOnlySpanOwner<byte>(span, this);
            }

            _xorMissed.Add(1);

            // if destroyed, return false as no previous one will contain it
            succeeded = IsAccountDestroyed(key);
            return default;
        }

        private bool IsAccountDestroyed(scoped in Key key)
        {
            if (_destroyed == null)
                return false;

            if (key.Path.Length != NibblePath.KeccakNibbleCount)
                return false;

            // it's either Account, Storage, or Merkle that is a storage
            return _destroyed.Contains(key.Path.UnsafeAsKeccak);
        }

        protected override void CleanUp()
        {
            _state.Dispose();
            _storage.Dispose();
            _preCommit.Dispose();
            _batch.Dispose();
            _xorMissed.Dispose();
            _prefetcher?.Dispose();
            _filter.Return(Pool);
            _ancestorsFilter?.Return(Pool);

            // release all the ancestors
            foreach (var ancestor in _ancestors)
            {
                ancestor.Dispose();
            }
        }

        public override string ToString() =>
            base.ToString() + ", " +
            $"{nameof(BlockNumber)}: {BlockNumber}, " +
            $"State: {_state}, " +
            $"Storage: {_storage}, " +
            $"PreCommit: {_preCommit}";

        public int DbReads => Volatile.Read(ref _dbReads);

        public IEnumerable<(uint blockNumber, Keccak hash)> Ancestors =>
            _ancestors.Select(ancestor => (ancestor.BlockNumber, ancestor.Hash));
    }

    public bool HasState(in Keccak keccak)
    {
        lock (_blockLock)
        {
            if (_blocksByHash.ContainsKey(keccak))
                return true;

            if (_db.HasState(keccak))
                return true;

            return false;
        }
    }

    /// <summary>
    /// Represents a block that is a result of ExecutionPayload.
    /// </summary>
    private class CommittedBlockState : RefCountingDisposable
    {
        /// <summary>
        /// A faster filter constructed on block commit.
        /// </summary>
        public readonly BitFilter Filter;

        /// <summary>
        /// Stores information about contracts that should have their previous incarnations destroyed.
        /// </summary>
        private readonly HashSet<Keccak>? _destroyed;

        private readonly Blockchain _blockchain;

        /// <summary>
        /// All the state, storage and commitment that was committed.
        /// </summary>
        private readonly PooledSpanDictionary _committed;

        private readonly bool _raw;
        private bool _discardable;
        private readonly DelayedMetrics.DelayedCounter<long, DelayedMetrics.LongIncrement> _filterMissed;

        public CommittedBlockState(BitFilter filter, HashSet<Keccak>? destroyed, Blockchain blockchain,
            PooledSpanDictionary committed, Keccak hash, Keccak parentHash,
            uint blockNumber, bool raw)
        {
            Filter = filter;
            _destroyed = destroyed;

            if (destroyed != null)
            {
                foreach (var account in destroyed)
                {
                    filter.Add(GetDestroyedHash(account));
                }
            }

            _blockchain = blockchain;
            _committed = committed;
            _raw = raw;
            Hash = hash;
            ParentHash = parentHash;
            BlockNumber = blockNumber;

            _filterMissed = _blockchain._bloomMissedReads.Delay();
        }

        public Keccak ParentHash { get; }

        public uint BlockNumber { get; private set; }

        public Keccak Hash { get; }

        private const ulong NonDestroyable = 0;

        public static ulong GetDestroyedHash(in Key key)
        {
            var path = key.Path;

            // Check if the path length qualifies.
            // The check for destruction is performed only for Account, Storage or Merkle-of-Storage that all have full paths.
            if (path.Length != NibblePath.KeccakNibbleCount)
                return NonDestroyable;

            // Return ulong hash.
            return GetDestroyedHash(path.UnsafeAsKeccak);
        }

        private static uint GetDestroyedHash(in Keccak keccak) =>
            BitOperations.Crc32C((uint)keccak.GetHashCode(), 0xDEADBEEF);

        /// <summary>
        /// Tries to get the key only from this block, acquiring no lease as it assumes that the lease is taken.
        /// </summary>
        public ReadOnlySpanOwner<byte> TryGetLocal(scoped in Key key, scoped ReadOnlySpan<byte> keyWritten,
            ulong bloom, ulong destroyedHash, out bool succeeded)
        {
            var mayHave = Filter.MayContain(bloom);

            // check if the change is in the block
            if (!mayHave)
            {
                // if destroyed, return false as no previous one will contain it
                if (IsAccountDestroyed(key, destroyedHash))
                {
                    succeeded = true;
                    return default;
                }

                succeeded = false;
                return default;
            }

            // first always try pre-commit as it may overwrite data
            if (_committed.TryGet(keyWritten, bloom, out var span))
            {
                // return with owned lease
                succeeded = true;
                AcquireLease();
                return new ReadOnlySpanOwner<byte>(span, this);
            }

            _filterMissed.Add(1);

            // if destroyed, return false as no previous one will contain it
            if (IsAccountDestroyed(key, destroyedHash))
            {
                succeeded = true;
                return default;
            }

            succeeded = false;
            return default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsAccountDestroyed(scoped in Key key, ulong destroyed)
        {
            if (destroyed == NonDestroyable || _destroyed == null)
                return false;

            return Filter.MayContain(destroyed) && _destroyed.Contains(key.Path.UnsafeAsKeccak);
        }

        protected override void CleanUp()
        {
            _filterMissed.Dispose();
            _committed.Dispose();
            Filter.Return(_blockchain._pool);

            if (_raw == false && _discardable == false)
            {
                _blockchain.Remove(this);
            }
        }

        public void Apply(IBatch batch)
        {
            if (_destroyed is { Count: > 0 })
            {
                foreach (var account in _destroyed)
                {
                    batch.Destroy(NibblePath.FromKey(account));
                }
            }

            ApplyImpl(batch, _committed, _blockchain);
        }

        public override string ToString() =>
            base.ToString() + ", " +
            $"{nameof(BlockNumber)}: {BlockNumber}, " +
            $"Committed data: {_committed}, ";

        public void MakeDiscardable()
        {
            _discardable = true;
        }
    }

    /// <summary>
    /// Represents a block that is a result of ExecutionPayload.
    /// </summary>
    private class ReadOnlyState : RefCountingDisposable, IReadOnlyWorldState
    {
        private readonly ReadOnlyBatchCountingRefs _batch;
        private readonly CommittedBlockState[] _ancestors;
        private readonly BitFilter? _ancestorsFilter;
        private readonly BufferPool? _pool;

        public ReadOnlyState(ReadOnlyBatchCountingRefs batch)
        {
            _batch = batch;
            _ancestors = [];
            Hash = batch.Metadata.StateHash;
        }

        public ReadOnlyState(Keccak stateRoot, ReadOnlyBatchCountingRefs batch, CommittedBlockState[] ancestors)
        {
            _batch = batch;
            _ancestors = ancestors;
            Hash = stateRoot;
        }

        public ReadOnlyState(Keccak stateRoot, ReadOnlyBatchCountingRefs batch, CommittedBlockState[] ancestors,
            BitFilter? ancestorsFilter, BufferPool pool)
        {
            _batch = batch;
            _ancestors = ancestors;
            _ancestorsFilter = ancestorsFilter;
            _pool = pool;
            Hash = stateRoot;
        }

        public uint BlockNumber { get; private set; }

        public Keccak Hash { get; }

        public Span<byte> GetStorage(in Keccak address, in Keccak storage, Span<byte> destination)
        {
            var key = Key.StorageCell(NibblePath.FromKey(address), storage);

            using var owner = Get(key);

            // check the span emptiness
            var data = owner.Span;
            if (data.IsEmpty)
                return Span<byte>.Empty;

            data.CopyTo(destination);
            return destination.Slice(0, data.Length);
        }

        public Account GetAccount(in Keccak address)
        {
            var key = Key.Account(NibblePath.FromKey(address));

            using var owner = Get(key);

            // check the span emptiness
            if (owner.Span.IsEmpty)
                return new Account(0, 0);

            Account.ReadFrom(owner.Span, out var result);
            return result;
        }

        [SkipLocalsInit]
        public ReadOnlySpanOwnerWithMetadata<byte> Get(scoped in Key key)
        {
            var hash = GetHash(key);
            var keyWritten = key.WriteTo(stackalloc byte[key.MaxByteLength]);

            var result = TryGet(key, keyWritten, hash, out var succeeded);

            Debug.Assert(succeeded);
            return result;
        }

        /// <summary>
        /// A recursive search through the block and its parent until null is found at the end of the weekly referenced
        /// chain.
        /// </summary>
        private ReadOnlySpanOwnerWithMetadata<byte> TryGet(scoped in Key key, scoped ReadOnlySpan<byte> keyWritten,
            ulong keyHash, out bool succeeded)
        {
            if (_ancestors.Length > 0)
            {
                var destroyedHash = CommittedBlockState.GetDestroyedHash(key);

                if (_ancestorsFilter == null ||
                    _ancestorsFilter.GetValueOrDefault().MayContainAny(keyHash, destroyedHash))
                {
                    ushort depth = 1;

                    // Walk through the ancestors only if the filter shows that they may contain the value
                    foreach (var ancestor in _ancestors)
                    {
                        var owner = ancestor.TryGetLocal(key, keyWritten, keyHash, destroyedHash, out succeeded);
                        if (succeeded)
                            return owner.WithDepth(depth);

                        depth++;
                    }
                }
            }

            if (_batch.TryGet(key, out var span))
            {
                // return leased batch
                succeeded = true;
                _batch.AcquireLease();
                return new ReadOnlySpanOwner<byte>(span, _batch).FromDatabase();
            }

            // report as succeeded operation. The value is not there but it was walked through.
            succeeded = true;
            return default;
        }

        protected override void CleanUp()
        {
            _batch.Dispose();

            // release all the ancestors
            foreach (var ancestor in _ancestors)
            {
                ancestor.Dispose();
            }

            _ancestorsFilter?.Return(_pool);
        }

        public override string ToString() =>
            base.ToString() + ", " +
            $"{nameof(BlockNumber)}: {BlockNumber}";
    }

    public static ulong GetHash(in Key key) => key.GetHashCodeULong();

    public async ValueTask DisposeAsync()
    {
        // mark writer as complete
        _finalizedChannel.Writer.Complete();

        // await the flushing task
        await _flusher;

        _accessor?.Dispose();

        // dispose all memoized blocks to please the ref-counting
        foreach (var (_, block) in _blocksByHash)
        {
            block.Dispose();
        }

        _blocksByHash.Clear();
        _blocksByNumber.Clear();

        // once the flushing is done and blocks are disposed, dispose the pool
        _pool.Dispose();

        // dispose metrics, but flush them last time before unregistering
        _beforeMetricsDisposed?.Invoke();
        _meter.Dispose();
    }

    /// <summary>
    /// The raw state implementation that provides a 1 layer of read-through caching with the last block.
    /// </summary>
    private class RawState : IRawState
    {
        private ArrayBufferWriter<byte> _prefixesToDelete = new();
        private readonly Blockchain _blockchain;
        private readonly IDb _db;
        private BlockState _current;

        private bool _finalized;

        public RawState(Blockchain blockchain, IDb db)
        {
            _blockchain = blockchain;
            _db = db;
            _current = new BlockState(Keccak.Zero, _db.BeginReadOnlyBatch(), [], _blockchain);
        }

        public void Dispose()
        {
            if (!_finalized)
            {
                ThrowNotFinalized();
                return;
            }

            _current.Dispose();

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowNotFinalized()
            {
                throw new Exception("Finalize not called. You need to call it before disposing the raw state. " +
                                    "Otherwise it won't be preserved properly");
            }
        }

        public Account GetAccount(in Keccak address) => _current.GetAccount(address);

        public Span<byte> GetStorage(in Keccak address, in Keccak storage, Span<byte> destination) =>
            _current.GetStorage(address, in storage, destination);

        public Keccak Hash { get; private set; }

        public void SetBoundary(in NibblePath account, in Keccak boundaryNodeKeccak)
        {
#if SNAP_SYNC_SUPPORT
            var path = SnapSync.CreateKey(account, stackalloc byte[NibblePath.FullKeccakByteLength]);
            var payload = SnapSync.WriteBoundaryValue(boundaryNodeKeccak, stackalloc byte[SnapSync.BoundaryValueSize]);

            _current.SetAccountRaw(path.UnsafeAsKeccak, payload);
#endif
        }

        public void SetBoundary(in Keccak account, in NibblePath storage, in Keccak boundaryNodeKeccak)
        {
#if SNAP_SYNC_SUPPORT
            var path = SnapSync.CreateKey(storage, stackalloc byte[NibblePath.FullKeccakByteLength]);
            var payload = SnapSync.WriteBoundaryValue(boundaryNodeKeccak, stackalloc byte[SnapSync.BoundaryValueSize]);
            _current.SetStorage(account, path.UnsafeAsKeccak, payload);
#endif
        }


        public void SetAccount(in Keccak address, in Account account) => _current.SetAccount(address, account);

        public void SetStorage(in Keccak address, in Keccak storage, ReadOnlySpan<byte> value) =>
            _current.SetStorage(address, storage, value);

        public void DestroyAccount(in Keccak address) => _current.DestroyAccount(address);

        public void RegisterDeleteByPrefix(in Key prefix)
        {
            var span = _prefixesToDelete.GetSpan(prefix.MaxByteLength);
            var written = prefix.WriteTo(span);
            _prefixesToDelete.Advance(written.Length);
        }

        public void Commit()
        {
            ThrowOnFinalized();

            Hash = _current.Hash;

            using var batch = _db.BeginNextBatch();

            DeleteByPrefixes(batch);

            _current.ApplyRaw(batch);
            _current.Dispose();

            batch.Commit(CommitOptions.DangerNoWrite);

            var read = _db.BeginReadOnlyBatch();

            _current = new BlockState(Keccak.Zero, read, [], _blockchain);
        }

        private void DeleteByPrefixes(IBatch batch)
        {
            var prefixes = _prefixesToDelete.WrittenSpan;
            while (prefixes.IsEmpty == false)
            {
                prefixes = Key.ReadFrom(prefixes, out var prefixToDelete);
                batch.DeleteByPrefix(prefixToDelete);
            }

            _prefixesToDelete.ResetWrittenCount();
        }

        public void Finalize(uint blockNumber)
        {
            ThrowOnFinalized();

            using var batch = _db.BeginNextBatch();
            batch.SetMetadata(blockNumber, Hash);
            batch.Commit(CommitOptions.DangerNoWrite);

            _finalized = true;
        }

        private void ThrowOnFinalized()
        {
            if (_finalized)
            {
                ThrowAlreadyFinalized();
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowAlreadyFinalized()
            {
                throw new Exception("This ras state has already been finalized!");
            }
        }

        public ReadOnlySpanOwnerWithMetadata<byte> Get(scoped in Key key) => ((IReadOnlyWorldState)_current).Get(key);
    }

    public IReadOnlyWorldStateAccessor BuildReadOnlyAccessor()
    {
        return _accessor = new ReadOnlyWorldStateAccessor(this);
    }

    private class ReadOnlyWorldStateAccessor : IReadOnlyWorldStateAccessor
    {
        private readonly ReaderWriterLockSlim _lock = new();
        private Dictionary<Keccak, ReadOnlyState> _readers = new();
        private readonly Queue<ReadOnlyState> _queue = new();
        private readonly Blockchain _blockchain;

        public ReadOnlyWorldStateAccessor(Blockchain blockchain)
        {
            _blockchain = blockchain;

            var snapshot = _blockchain._db.SnapshotAll()
                .Select(batch => new ReadOnlyState(new ReadOnlyBatchCountingRefs(batch)))
                .ToArray();

            // enqueue all to make them properly disposable
            foreach (ReadOnlyState state in snapshot)
            {
                _queue.Enqueue(state);
                _readers.Add(state.Hash, state);
            }
        }

        public void OnCommitToBlockchain(in Keccak stateHash)
        {
            Debug.Assert(Monitor.IsEntered(_blockchain._blockLock), "Should be called only under the lock");

            var state = _blockchain.StartReadOnly(stateHash);

            _lock.EnterWriteLock();
            try
            {
                _readers.Add(state.Hash, (ReadOnlyState)state);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public bool HasState(in Keccak keccak)
        {
            _lock.EnterReadLock();
            try
            {
                return _readers.ContainsKey(keccak);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public Account GetAccount(in Keccak rootHash, in Keccak address)
        {
            if (!TryGetLeasedState(rootHash, out var state))
            {
                return default;
            }

            try
            {
                return state.GetAccount(address);
            }
            finally
            {
                // Release
                state.Dispose();
            }
        }

        public Span<byte> GetStorage(in Keccak rootHash, in Keccak address, in Keccak storage, Span<byte> destination)
        {
            if (!TryGetLeasedState(rootHash, out var state))
            {
                return default;
            }

            try
            {
                return state.GetStorage(address, storage, destination);
            }
            finally
            {
                // Release
                state.Dispose();
            }
        }

        /// <summary>
        /// Finds the state in the dictionary under the read lock, acquires the lease on it and returns as soon as possible the leased state.
        /// </summary>
        private bool TryGetLeasedState(in Keccak rootHash, out ReadOnlyState state)
        {
            _lock.EnterReadLock();
            try
            {
                if (_readers.TryGetValue(rootHash, out state))
                {
                    state.AcquireLease();
                    return true;
                }

                return false;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void OnCommitToDatabase(CommittedBlockState committed, CommittedBlockState[] blocksWithSameNumber)
        {
            // Capture the readonly tx first
            var batch = new ReadOnlyBatchCountingRefs(_blockchain._db.BeginReadOnlyBatch());
            var readOnly = new ReadOnlyState(batch);

            Debug.Assert(committed.Hash == batch.Metadata.StateHash, "Should be equal to the last written");
            Debug.Assert(blocksWithSameNumber.Contains(committed));

            ref ReadOnlyState reader = ref Unsafe.NullRef<ReadOnlyState>();
            var toDispose = new List<ReadOnlyState>(blocksWithSameNumber.Length);

            _lock.EnterWriteLock();
            try
            {
                reader = ref CollectionsMarshal.GetValueRefOrNullRef(_readers, committed.Hash);
                Debug.Assert(Unsafe.IsNullRef(ref reader) == false);

                // add reader to dispose
                toDispose.Add(reader);

                // update to the batch
                reader = readOnly;

                // enqueue the batch for cleanup later
                _queue.Enqueue(readOnly);

                // dequeue the oldest batch if the history is beyond depth
                if (_queue.Count > _blockchain._db.HistoryDepth)
                {
                    ReadOnlyState oldestBatch = _queue.Dequeue();
                    toDispose.Add(oldestBatch);

                    var removed = _readers.Remove(oldestBatch.Hash);
                    Debug.Assert(removed);
                }

                foreach (CommittedBlockState b in blocksWithSameNumber)
                {
                    if (b.Hash != committed.Hash)
                    {
                        var removed = _readers.Remove(b.Hash, out ReadOnlyState? state);
                        Debug.Assert(removed);
                        toDispose.Add(state);
                    }
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            foreach (ReadOnlyState state in toDispose)
            {
                state.Dispose();
            }
        }

        public void Dispose()
        {
            _lock.Dispose();
            foreach (var (key, state) in _readers)
            {
                state.Dispose();
            }

            _readers.Clear();
        }
    }

    /// <summary>
    /// Creates the combined <see cref="BitFilter"/> by or-ing all <paramref name="ancestors"/>
    /// </summary>
    /// <param name="ancestors"></param>
    /// <returns></returns>
    private BitFilter? CreateAncestorsFilter(CommittedBlockState[] ancestors)
    {
        if (ancestors.Length == 0)
            return null;

        var filter = CreateBitFilter();
        filter.OrWith(ancestors.Select(a => a.Filter).ToArray());
        return filter;
    }

    private static void ApplyImpl(IBatch batch, PooledSpanDictionary dict, Blockchain blockchain)
    {
        var preCommit = blockchain._preCommit;

        var page = blockchain._pool.Rent(false);
        try
        {
            var span = page.Span;

            foreach (var kvp in dict)
            {
                if (kvp.Metadata == (byte)EntryType.Persistent)
                {
                    Key.ReadFrom(kvp.Key, out var key);
                    var data = preCommit == null ? kvp.Value : preCommit.InspectBeforeApply(key, kvp.Value, span);
                    batch.SetRaw(key, data);
                }
            }
        }
        finally
        {
            blockchain._pool.Return(page);
        }
    }
}
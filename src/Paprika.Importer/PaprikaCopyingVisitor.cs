﻿using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Paprika.Chain;
using Paprika.Utils;
using Keccak = Paprika.Crypto.Keccak;

namespace Paprika.Importer;

public class PaprikaCopyingVisitor : ITreeLeafVisitor, IDisposable
{
    struct Item
    {
        private readonly ValueKeccak _account;

        // account
        private readonly Nethermind.Core.Account? _accountValue;

        // storage
        private readonly ValueKeccak _storage;
        private readonly byte[]? _data;

        public Item(ValueKeccak account, Nethermind.Core.Account accountValue)
        {
            _account = account;
            _accountValue = accountValue;
        }

        public Item(ValueKeccak account, ValueKeccak storage, byte[] data)
        {
            _account = account;
            _storage = storage;
            _data = data;
        }

        public void Apply(IWorldState block)
        {
            var addr = AsPaprika(_account);

            if (_accountValue != null)
            {
                var v = _accountValue;
                var codeHash = AsPaprika(v.CodeHash);

                // storage root will be recalculated by Paprika, drop it
                block.SetAccount(addr, new Account(v.Balance, v.Nonce, codeHash, Keccak.EmptyTreeHash));
            }
            else
            {
                block.SetStorage(addr, AsPaprika(_storage), _data);
            }
        }
    }


    private readonly Blockchain _blockchain;
    private readonly int _batchSize;
    private readonly int? _expectedAccountCount;
    private readonly Channel<Item> _channel;

    private readonly Meter _meter;
    private readonly MetricsExtensions.IAtomicIntGauge _accountsGauge;

    private int _accounts;

    public PaprikaCopyingVisitor(Blockchain blockchain, int batchSize, int? expectedAccountCount)
    {
        _meter = new Meter("Paprika.Importer");

        var accountsUnit = expectedAccountCount.HasValue ? "%" : "count";
        _accountsGauge = _meter.CreateAtomicObservableGauge("Accounts imported", accountsUnit);

        _blockchain = blockchain;

        var options = new UnboundedChannelOptions
        { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false };
        _channel = Channel.CreateUnbounded<Item>(options);

        _batchSize = batchSize;
        _expectedAccountCount = expectedAccountCount;
    }

    public void VisitLeafAccount(in ValueKeccak account, Nethermind.Core.Account value)
    {
        var incremented = Interlocked.Increment(ref _accounts);

        // update occasionally
        if (incremented % 100 == 0)
        {
            if (_expectedAccountCount != null)
            {
                _accountsGauge.Set(incremented / (_expectedAccountCount.Value / 100));
            }
            else
            {
                _accountsGauge.Set(incremented);
            }
        }

        _channel.Writer.TryWrite(new(account, value));
    }

    public void VisitLeafStorage(in ValueKeccak account, in ValueKeccak storage, ReadOnlySpan<byte> value)
    {
        _channel.Writer.TryWrite(new(account, storage, value.ToArray()));
    }

    public void Finish() => _channel.Writer.Complete();

    public async Task Copy()
    {
        var parent = Keccak.Zero;
        uint number = 1;

        var reader = _channel.Reader;

        var finalization = new Queue<Keccak>();
        const int finalizationDepth = 32;

        while (await reader.WaitToReadAsync())
        {
            var i = 0;

            using var block = _blockchain.StartNew(parent);

            while (i < _batchSize && reader.TryRead(out var item))
            {
                i++;
                item.Apply(block);
            }

            // commit & finalize
            var hash = block.Commit(number);

            finalization.Enqueue(hash);

            if (finalization.Count == finalizationDepth)
            {
                _blockchain.Finalize(finalization.Dequeue());
            }

            // update
            number++;
            parent = hash;
        }

        while (finalization.TryDequeue(out var keccak))
        {
            _blockchain.Finalize(keccak);
        }
    }

    private static Keccak AsPaprika(Nethermind.Core.Crypto.Keccak keccak)
    {
        Unsafe.SkipInit(out Keccak k);
        keccak.Bytes.CopyTo(k.BytesAsSpan);
        return k;
    }

    private static Keccak AsPaprika(ValueKeccak keccak)
    {
        Unsafe.SkipInit(out Keccak k);
        keccak.Bytes.CopyTo(k.BytesAsSpan);
        return k;
    }

    public void Dispose() => _meter.Dispose();
}
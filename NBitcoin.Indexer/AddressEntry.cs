﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace NBitcoin.Indexer
{
    public class AddressEntry
    {
        ChainedBlock _ConfirmedBlock;
        bool _ConfirmedSet;
        public ChainedBlock ConfirmedBlock
        {
            get
            {
                if (!_ConfirmedSet)
                    throw new InvalidOperationException("You need to call FetchConfirmedBlock(Chain chain) to attach the confirmed block to this entry");
                return _ConfirmedBlock;
            }
            private set
            {
                _ConfirmedSet = true;
                _ConfirmedBlock = value;
            }
        }
        /// <summary>
        /// Fetch ConfirmationInfo if not already set about this entry from local chain
        /// </summary>
        /// <param name="chain">Local chain</param>
        /// <returns>Returns this</returns>
        public AddressEntry FetchConfirmedBlock(Chain chain)
        {
            if (_ConfirmedBlock != null)
                return this;
            if (BlockIds == null || BlockIds.Length == 0)
                return this;
            ConfirmedBlock = BlockIds.Select(id => chain.GetBlock(id)).FirstOrDefault(b => b != null);
            return this;
        }
        public AddressEntry(params Entity[] entities)
        {
            if (entities == null)
                throw new ArgumentNullException("entities");
            if (entities.Length == 0)
                throw new ArgumentException("At least one entity should be provided", "entities");

            var loadedEntity = entities.FirstOrDefault(e => e.IsLoaded);
            if (loadedEntity == null)
                loadedEntity = entities[0];

            Address = loadedEntity.Address;
            TransactionId = new uint256(loadedEntity.TransactionId);
            BlockIds = entities
                                    .Where(s => s.BlockId != null)
                                    .Select(s => new uint256(s.BlockId))
                                    .ToArray();
            SpentOutpoints = loadedEntity.SpentOutpoints;
            ReceivedTxOutIndices = loadedEntity.ReceivedTxOutIndices;
            if (loadedEntity.IsLoaded)
            {
                ReceivedCoins = new List<Spendable>();
                for (int i = 0 ; i < loadedEntity.ReceivedTxOutIndices.Count ; i++)
                {
                    ReceivedCoins.Add(new Spendable(new OutPoint(TransactionId, loadedEntity.ReceivedTxOutIndices[i]), loadedEntity.ReceivedTxOuts[i]));
                }
                SpentCoins = new List<Spendable>();
                for (int i = 0 ; i < SpentOutpoints.Count ; i++)
                {
                    SpentCoins.Add(new Spendable(SpentOutpoints[i], loadedEntity.SpentTxOuts[i]));
                }
                BalanceChange = ReceivedCoins.Select(t => t.TxOut.Value).Sum() - SpentCoins.Select(t => t.TxOut.Value).Sum();
            }
            MempoolDate = entities.Where(e => e.BlockId == null).Select(e => e.Timestamp).FirstOrDefault();
        }
        public class Entity
        {
            class IntCompactVarInt : CompactVarInt
            {
                public IntCompactVarInt(uint value)
                    : base(value, 4)
                {
                }
                public IntCompactVarInt()
                    : base(4)
                {

                }
            }
            public static Dictionary<string, Entity> ExtractFromTransaction(Transaction tx, uint256 txId)
            {
                return ExtractFromTransaction(null, tx, txId);
            }
            public static Dictionary<string, Entity> ExtractFromTransaction(uint256 blockId, Transaction tx, uint256 txId)
            {
                if (txId == null)
                    txId = tx.GetHash();
                Dictionary<string, AddressEntry.Entity> entryByAddress = new Dictionary<string, AddressEntry.Entity>();
                foreach (var input in tx.Inputs)
                {
                    if (tx.IsCoinBase)
                        break;
                    var signer = input.ScriptSig.GetSignerAddress(AzureIndexer.InternalNetwork);
                    if (signer != null)
                    {
                        AddressEntry.Entity entry = null;
                        if (!entryByAddress.TryGetValue(signer.ToString(), out entry))
                        {
                            entry = new AddressEntry.Entity(txId, signer, blockId);
                            entryByAddress.Add(signer.ToString(), entry);
                        }
                        entry.SpentOutpoints.Add(input.PrevOut);
                    }
                }

                uint i = 0;
                foreach (var output in tx.Outputs)
                {
                    var receiver = output.ScriptPubKey.GetDestinationAddress(AzureIndexer.InternalNetwork);
                    if (receiver != null)
                    {
                        AddressEntry.Entity entry = null;
                        if (!entryByAddress.TryGetValue(receiver.ToString(), out entry))
                        {
                            entry = new AddressEntry.Entity(txId, receiver, blockId);
                            entryByAddress.Add(receiver.ToString(), entry);
                        }
                        entry.ReceivedTxOutIndices.Add(i);
                    }
                    i++;
                }
                return entryByAddress;
            }

            public Entity()
            {

            }

            public Entity(DynamicTableEntity entity)
            {
                var splitted = entity.RowKey.Split('-');
                Address = BitcoinAddress.Create(splitted[0], AzureIndexer.InternalNetwork);
                TransactionId = new uint256(splitted[1]);
                if (splitted.Length >= 3)
                    BlockId = new uint256(splitted[2]);
                Timestamp = entity.Timestamp;
                //_PartitionKey = entity.PartitionKey;

                _SpentOutpoints = Helper.DeserializeList<OutPoint>(Helper.GetEntityProperty(entity, "a"));
                _SpentTxOuts = Helper.DeserializeList<TxOut>(Helper.GetEntityProperty(entity, "b"));
                _ReceivedTxOutIndices = Helper.DeserializeList<IntCompactVarInt>(Helper.GetEntityProperty(entity, "c"))
                                        .Select(o => (uint)o.ToLong())
                                        .ToList();
                _ReceivedTxOuts = Helper.DeserializeList<TxOut>(Helper.GetEntityProperty(entity, "d"));
            }

            public DynamicTableEntity CreateTableEntity()
            {
                DynamicTableEntity entity = new DynamicTableEntity();
                entity.ETag = "*";
                entity.PartitionKey = PartitionKey;
                entity.RowKey = Address.ToString() + "-" + TransactionId.ToString() + "-" + BlockId.ToString();
                Helper.SetEntityProperty(entity, "a", Helper.SerializeList(SpentOutpoints));
                Helper.SetEntityProperty(entity, "b", Helper.SerializeList(SpentTxOuts));
                Helper.SetEntityProperty(entity, "c", Helper.SerializeList(ReceivedTxOutIndices.Select(e => new IntCompactVarInt(e))));
                Helper.SetEntityProperty(entity, "d", Helper.SerializeList(ReceivedTxOuts));
                return entity;
            }

            string _PartitionKey;
            public string PartitionKey
            {
                get
                {
                    if (_PartitionKey == null && Address != null)
                    {
                        var wif = Address.ToString();
                        _PartitionKey = GetPartitionKey(wif);
                    }
                    return _PartitionKey;
                }
            }

            public Entity(uint256 txid, BitcoinAddress address, uint256 blockId)
            {
                Address = address;
                TransactionId = txid;
                BlockId = blockId;
            }

            public uint256 TransactionId
            {
                get;
                set;
            }
            public uint256 BlockId
            {
                get;
                set;
            }
            public BitcoinAddress Address
            {
                get;
                set;
            }



            public static string GetPartitionKey(string wif)
            {
                char[] c = new char[3];
                c[0] = (int)(wif[wif.Length - 3]) % 2 == 0 ? 'a' : 'b';
                c[1] = wif[wif.Length - 2];
                c[2] = wif[wif.Length - 1];
                return new string(c);
            }

            private readonly List<uint> _ReceivedTxOutIndices = new List<uint>();
            public List<uint> ReceivedTxOutIndices
            {
                get
                {
                    return _ReceivedTxOutIndices;
                }
            }

            private readonly List<TxOut> _SpentTxOuts = new List<TxOut>();
            public List<TxOut> SpentTxOuts
            {
                get
                {
                    return _SpentTxOuts;
                }
            }

            private readonly List<OutPoint> _SpentOutpoints = new List<OutPoint>();
            public List<OutPoint> SpentOutpoints
            {
                get
                {
                    return _SpentOutpoints;
                }
            }
            private readonly List<TxOut> _ReceivedTxOuts = new List<TxOut>();
            public List<TxOut> ReceivedTxOuts
            {
                get
                {
                    return _ReceivedTxOuts;
                }
            }
            public override string ToString()
            {
                return "RowKey : " + Address;
            }

            public bool IsLoaded
            {
                get
                {
                    return SpentOutpoints.Count == SpentTxOuts.Count && ReceivedTxOuts.Count == ReceivedTxOutIndices.Count;
                }
            }

            public DateTimeOffset? Timestamp
            {
                get;
                set;
            }
        }
        public uint256 TransactionId
        {
            get;
            set;
        }

        public BitcoinAddress Address
        {
            get;
            set;
        }

        List<Spendable> _ReceivedCoins;
        public List<Spendable> ReceivedCoins
        {
            get
            {
                return _ReceivedCoins;
            }
            set
            {
                _ReceivedCoins = value;
            }
        }

        List<OutPoint> _SpentOutpoints = new List<OutPoint>();

        /// <summary>
        /// List of spent outpoints
        /// </summary>
        public List<OutPoint> SpentOutpoints
        {
            get
            {
                return _SpentOutpoints;
            }
            set
            {
                _SpentOutpoints = value;
            }
        }


        List<Spendable> _SpentCoins;

        /// <summary>
        /// List of spent coins
        /// Can be null if the indexer have not yet indexed parent transactions
        /// Use SpentOutpoints if you only need outpoints
        /// </summary>
        public List<Spendable> SpentCoins
        {
            get
            {
                return _SpentCoins;
            }
            set
            {
                _SpentCoins = value;
            }
        }
        public List<int> TxOutIndices
        {
            get;
            set;
        }



        public uint256[] BlockIds
        {
            get;
            set;
        }

        public Money BalanceChange
        {
            get;
            set;
        }

        public override string ToString()
        {
            return Address + " - " + (BalanceChange == null ? "??" : BalanceChange.ToString());
        }

        public DateTimeOffset? MempoolDate
        {
            get;
            set;
        }

        public List<uint> ReceivedTxOutIndices
        {
            get;
            set;
        }
    }
}

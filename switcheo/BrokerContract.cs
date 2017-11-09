using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System.Numerics;

namespace switcheo
{
    public class BrokerContract : SmartContract
    {
        [Appcall("ecd24a4b2b31ee3144a71f7ac22dec6a3128190f")] // TODO: Add RPX ScriptHash - or find workaround to call arbitrary contract
        public static extern object CallRPXContract(string method, params object[] args);

        //[DisplayName("created")]
        //public static event Action<byte[]> Created; // (offerHash)

        //[DisplayName("filled")]
        //public static event Action<byte[], BigInteger> Filled; // (offerHash, amount)

        //[DisplayName("cancelled")]
        //public static event Action<byte[]> Cancelled; // (offerHash)

        //[DisplayName("transferred")]
        //public static event Action<byte[], byte[], byte, BigInteger> Transferred; // (address, assetID, assetCategory, amount)

        //[DisplayName("withdrawn")]
        //public static event Action<byte[], byte[], byte, BigInteger> Withdrawn; // (address, assetID, assetCategory, amount)

        private static readonly byte[] Owner = { 2, 86, 121, 88, 238, 62, 78, 230, 177, 3, 68, 142, 10, 254, 31, 223, 139, 87, 150, 110, 30, 135, 156, 120, 59, 17, 101, 55, 236, 191, 90, 249, 113 };
        private const ulong feeFactor = 100000; // 1 => 0.001%
        private const int maxFee = 3000; // 3000/10000 = 0.3%

        // Contract States
        private static readonly byte[] Pending = { };         // only can initialize
        private static readonly byte[] Active = { 0x01 };     // all operations active
        private static readonly byte[] Inactive = { 0x02 };   // trading halted - only can do cancel, withdrawl & owner actions

        // Storage Key Prefixes
        private static readonly byte[] OfferDetailsPrefix = { 0x10 };
        private static readonly byte[] OfferAmountPrefix = { 0x20 };
        private static readonly byte[] WantAmountPrefix = { 0x30 };
        private static readonly byte[] AvailableAmountPrefix = { 0x40 };

        // Asset Categories
        private static readonly byte[] SystemAsset = { 0x99 };
        private static readonly byte[] NEP5 = { 0x98 };
        
        // Flags
        private static readonly byte[] Empty = { };
        private static readonly byte[] Yes = { 0x01 };

        private struct Offer
        {
            public byte[] MakerAddress;
            public byte[] OfferAssetID;
            public byte[] OfferAssetCategory;
            public BigInteger OfferAmount;
            public byte[] WantAssetID;
            public byte[] WantAssetCategory;
            public BigInteger WantAmount;
            public BigInteger AvailableAmount;
            public byte[] Nonce;
            public byte[] PreviousOfferHash; // in same trading pair
        }

        private static Offer NewOffer(
            byte[] makerAddress,
            byte[] offerAssetID, byte[] offerAmount,
            byte[] wantAssetID,  byte[] wantAmount,
            byte[] availableAmount, byte[] nonce
        )
        {
            var offerAssetCategory = NEP5;
            var wantAssetCategory = NEP5;
            if (offerAssetID.Length == 32) offerAssetCategory = SystemAsset;
            if (wantAssetID.Length == 32) wantAssetCategory = SystemAsset;

            return new Offer
            {
                MakerAddress = makerAddress.Take(20),
                OfferAssetID = offerAssetID,
                OfferAssetCategory = offerAssetCategory,
                OfferAmount = offerAmount.AsBigInteger(),
                WantAssetID = wantAssetID,
                WantAssetCategory = wantAssetCategory,
                WantAmount = wantAmount.AsBigInteger(),
                AvailableAmount = availableAmount.AsBigInteger(),
                Nonce = nonce.Take(32)
            };
        }

        /// <summary>
        ///   This is the Switcheo smart contract entrypoint.
        /// 
        ///   Parameter List: 0710
        ///   Return List: 05
        /// </summary>
        /// <param name="operation">
        ///   The method to be invoked.
        /// </param>
        /// <param name="args">
        ///   Input parameters for the delegated method.
        /// </param>
        public static object Main(string operation, params object[] args)
        {
            if (Runtime.Trigger == TriggerType.Verification)
            {
                // == Withdrawal of SystemAsset ==
                // Check that the TransactionAttribute has been set to signify deduction during Application phase
                if (!WithdrawingSystemAsset())
                {
                    Runtime.Log("TransactionAttribute flag not set!");
                    return false;
                }

                // Verify that each output is allowed
                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                var outputs = currentTxn.GetOutputs();
                foreach (var o in outputs)
                {
                    if (!VerifyWithdrawal(o.ScriptHash, o.AssetId, o.Value))
                    {
                        Runtime.Log("Found an unauthorized output!");
                        return false;
                    }
                }
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
                // == Withdrawal of SystemAsset ==
                if (WithdrawingSystemAsset())
                {
                    var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                    var outputs = currentTxn.GetOutputs();
                    foreach (var o in outputs)
                    {
                        ReduceBalance(o.ScriptHash, o.AssetId, o.Value);
                    }
                    return true;
                }

                // == Init ==
                if (operation == "initialize")
                {
                    if (!Runtime.CheckWitness(Owner))
                    {
                        Runtime.Log("Owner signature verification failed!");
                        return false;
                    }
                    if (args.Length != 3)
                    {
                        Runtime.Log("Wrong number of arguments!");
                        return false;
                    }
                    return Initialize((BigInteger)args[0], (BigInteger)args[1], (byte[])args[2]);
                }

                // == Query ==
                // TODO: do we need all these helper methods? client can query contract storage directly!
                // Check that contract has been initialized
                if (Storage.Get(Storage.CurrentContext, "state").Length == 0)
                {
                    Runtime.Log("Contract has not been initialized!");
                    return false;
                }
                if (operation == "getOffers")
                {
                    if (args.Length != 2)
                    {
                        Runtime.Log("Wrong number of arguments!");
                        return false;
                    }
                    var key = ((byte[])args[0]).Concat((byte[])args[1]);
                    return Storage.Get(Storage.CurrentContext, key);
                }
                if (operation == "getOffer")
                {
                    if (args.Length != 1)
                    {
                        Runtime.Log("Wrong number of arguments!");
                        return false;
                    }
                    return Storage.Get(Storage.CurrentContext, (byte[])args[0]);
                }
                if (operation == "tradingStatus")
                    return Storage.Get(Storage.CurrentContext, "state");
                if (operation == "getMakerFee")
                    return Storage.Get(Storage.CurrentContext, "makerFee");
                if (operation == "getTakerFee")
                    return Storage.Get(Storage.CurrentContext, "takerFee");

                // == Execute ==
                if (operation == "makeOffer")
                {
                    if (Storage.Get(Storage.CurrentContext, "state") == Inactive)
                    {
                        Runtime.Log("Contract is inactive!");
                        return false;
                    }
                    if (args.Length != 6)
                    {
                        Runtime.Log("Wrong number of arguments!");
                        return false;
                    }

                    var offer = NewOffer((byte[])args[0], (byte[])args[1], (byte[])args[2], (byte[])args[3], (byte[])args[4], (byte[])args[4], (byte[])args[5]);

                    if (VerifyOffer(offer))
                    {
                        return MakeOffer(offer);
                    }
                    else
                    {
                        Runtime.Log("Offer is invalid!");
                        // TODO: RefundAllInputs()
                    }
                }
                if (operation == "fillOffer")
                {
                    if (Storage.Get(Storage.CurrentContext, "state") == Inactive)
                    {
                        Runtime.Log("Contract is inactive!");
                        return false;
                    }
                    if (args.Length != 3)
                    {
                        Runtime.Log("Wrong number of arguments!");
                        return false;
                    }

                    if (VerifyFill((byte[])args[0], (byte[])args[1], (BigInteger)args[2]))
                    {
                        return FillOffer((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                    }
                    else
                    {
                        Runtime.Log("Fill is invalid!");
                        // TODO: RefundAllInputs()
                    }
                }
                if (operation == "cancelOffer")
                {
                    if (args.Length != 1)
                    {
                        Runtime.Log("Wrong number of arguments!");
                        return false;
                    }

                    return CancelOffer((byte[])args[0]);
                }
                if (operation == "withdrawAssets") // NEP-5 only
                {
                    if (args.Length != 3)
                    {
                        Runtime.Log("Wrong number of arguments!");
                        return false;
                    }

                    if (VerifyWithdrawal((byte[])args[0], (byte[])args[1], (BigInteger)args[2]))
                    {
                        return WithdrawAssets((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                    }
                    else
                    {
                        Runtime.Log("Withdrawal is invalid!");
                    }
                }


                // == Owner ==
                if (!Runtime.CheckWitness(Owner))
                {
                    Runtime.Log("Owner signature verification failed");
                    return false;
                }
                if (operation == "freezeTrading")
                {
                    Storage.Put(Storage.CurrentContext, "state", Inactive);
                    return true;
                }
                if (operation == "unfreezeTrading")
                {
                    Storage.Put(Storage.CurrentContext, "state", Active);
                    return true;
                }
                if (operation == "setFees")
                {
                    if (args.Length != 2) return false;
                    return SetFees((BigInteger)args[0], (BigInteger)args[1]);
                }
                if (operation == "setFeeAddress")
                {
                    if (args.Length != 1) return false;
                    return SetFeeAddress((byte[])args[0]);
                }
            }

            return false;
        }

        private static bool Initialize(BigInteger takerFee, BigInteger makerFee, byte[] feeAddress)
        {
            Runtime.Log("Checking state..");
            if (Storage.Get(Storage.CurrentContext, "state") != Pending) return false;
            if (!SetFees(takerFee, makerFee)) return false;
            if (!SetFeeAddress(feeAddress)) return false;

            Runtime.Log("Initialized!");
            Storage.Put(Storage.CurrentContext, "state", Active);
            return true;
        }

        private static byte[] GetOffers(byte[] offerAssetID, byte[] wantAssetID)
        {
            return Storage.Get(Storage.CurrentContext, offerAssetID.Concat(wantAssetID));
        }

        private static bool VerifyOffer(Offer offer)
        {
            // Check that transaction is signed by the maker
            Runtime.Log("Checking witness..");
            if (!Runtime.CheckWitness(offer.MakerAddress)) return false;

            // Check that nonce is not repeated
            Runtime.Log("Calculating hash..");
            var hash = Hash(offer);
            Runtime.Log("Checking nonce..");
            if (Storage.Get(Storage.CurrentContext, hash).Length != 0) return false;

            // Check that the amounts > 0
            Runtime.Log("Checking offer amount min..");
            if (!(offer.OfferAmount > 0 && offer.WantAmount > 0)) return false;
            
            // Check the trade is across different assets
            Runtime.Log("Checking offer asset type..");
            if (offer.OfferAssetID == offer.WantAssetID) return false;

            // Check that asset IDs are valid
            Runtime.Log("Checking offer asset ID..");
            if ((offer.OfferAssetID.Length != 20 && offer.OfferAssetID.Length != 32) ||
                (offer.WantAssetID.Length != 20 && offer.WantAssetID.Length != 32)) return false;

            // Verify that the offer txn has really has the indicated assets available
            Runtime.Log("Checking sent amount..");
            return VerifySentAmount(offer.MakerAddress, offer.OfferAssetID, offer.OfferAssetCategory, offer.OfferAmount);
        }

        private static bool MakeOffer(Offer offer)
        {
            // Transfer NEP-5 token if required
            if (offer.OfferAssetCategory == NEP5)
            {
                // TODO: Do we need to prevent re-entrancy due to external call?
                Runtime.Log("Transferring NEP-5 token..");
                bool transferSuccessful = (bool)CallRPXContract("transferFrom", ExecutionEngine.ExecutingScriptHash, offer.MakerAddress, ExecutionEngine.ExecutingScriptHash, offer.OfferAmount);
                if (!transferSuccessful) return false; // XXX: Getting here would be very bad.
            }

            AddOffer(offer);

            // Notify runtime
            //Created(offerHash);
            return true;
        }

        private static bool VerifyFill(byte[] fillerAddress, byte[] offerHash, BigInteger amountToFill)
        {
            // Check that transaction is signed by the filler
            Runtime.Log("Checking witness..");
            if (!Runtime.CheckWitness(fillerAddress)) return false;

            // Check that the offer exists 
            Runtime.Log("Checking offer..");
            Offer offer = GetOffer(offerHash);
            if (offer.MakerAddress == Empty) return false;

            // Check that the filler is different from the maker
            // TODO: can we omit this?
            Runtime.Log("Checking addresses..");
            if (fillerAddress == offer.MakerAddress) return false;

            // Check that amount to offer <= available amount
            Runtime.Log("Checking amounts..");
            BigInteger amountToOffer = AmountToOffer(offer, amountToFill);
            if (amountToOffer > offer.AvailableAmount) return false;

            // Verify that the filling txn really has the required assets available
            Runtime.Log("Checking sent amount..");
            return VerifySentAmount(offer.MakerAddress, offer.OfferAssetID, offer.OfferAssetCategory, offer.OfferAmount);
        }

        private static bool FillOffer(byte[] fillerAddress, byte[] offerHash, BigInteger amountToFill)
        {
            // Get offer
            Runtime.Log("Getting offer..");
            Offer offer = GetOffer(offerHash);

            // Calculate offered amount and fees
            Runtime.Log("Calculating fill amounts..");
            BigInteger amountToOffer = AmountToOffer(offer, amountToFill);
            BigInteger makerFeeRate = Storage.Get(Storage.CurrentContext, "makerFee").AsBigInteger();
            BigInteger takerFeeRate = Storage.Get(Storage.CurrentContext, "takerFee").AsBigInteger();
            BigInteger makerFee = (amountToOffer * makerFeeRate) / feeFactor;
            BigInteger takerFee = (amountToOffer * takerFeeRate) / feeFactor;

            // Move fees
            Runtime.Log("Moving fees..");
            TransferAssetTo(Owner, offer.WantAssetID, makerFee);
            TransferAssetTo(Owner, offer.OfferAssetID, takerFee);

            // Move asset to the maker balance
            Runtime.Log("Moving assets to maker..");
            TransferAssetTo(offer.MakerAddress, offer.WantAssetID, amountToFill - makerFee);
            //Transferred(offer.MakerAddress, offer.WantAssetID, (byte) offer.WantAssetCategory, amountToFill - makerFee);

            // Move asset to the taker balance
            Runtime.Log("Moving assets to taker..");
            TransferAssetTo(fillerAddress, offer.OfferAssetID, amountToOffer - takerFee);
            //Transferred(fillerAddress, offer.OfferAssetID, (byte)offer.OfferAssetCategory, amountToOffer - takerFee);

            // Update available amount
            Runtime.Log("Updating available amount..");
            offer.AvailableAmount = offer.AvailableAmount - amountToFill;

            StoreOffer(offer);

            // Notify runtime
            //Filled(offerHash, amountToFill);
            return true;
        }

        private static bool CancelOffer(byte[] offerHash)
        {
            // Check that the offer exists
            Runtime.Log("Finding offer..");
            Offer offer = GetOffer(offerHash);
            if (offer.MakerAddress == Empty) return false;

            // Check that transaction is signed by the canceller
            Runtime.Log("Validating canceller..");
            if (!Runtime.CheckWitness(offer.MakerAddress)) return false;

            // Move funds to withdrawal address
            Runtime.Log("Moving assets..");
            var storeKey = StoreKey(offer.MakerAddress, offer.OfferAssetID);
            BigInteger balance = Storage.Get(Storage.CurrentContext, storeKey).AsBigInteger();
            Storage.Put(Storage.CurrentContext, storeKey, balance + offer.AvailableAmount);

            // Remove offer
            Runtime.Log("Removing cancelled offer..");
            RemoveOffer(offer);

            // Notify runtime
            //Cancelled(offerHash);
            return true;
        }
        
        private static bool VerifyWithdrawal(byte[] holderAddress, byte[] assetID, BigInteger amount)
        {
            // Check that transaction is signed by the holder
            Runtime.Log("Checking witness..");
            if (!Runtime.CheckWitness(holderAddress)) return false;

            // Check that there are asset value > 0 in balance
            Runtime.Log("Checking asset value..");
            var key = StoreKey(holderAddress, assetID);
            var balance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            if (balance < amount) return false;

            return true;
        }

        private static bool WithdrawAssets(byte[] holderAddress, byte[] assetID, BigInteger amount)
        {
            // Transfer token
            // TODO: how do we pass Runtime.CheckWitness(ourScriptHash) on NEP5 contract?
            Runtime.Log("Transferring NEP-5 token..");
            bool transferSuccessful = (bool)CallRPXContract("transfer", ExecutionEngine.ExecutingScriptHash, holderAddress, amount);
            if (!transferSuccessful) return false;

            Runtime.Log("Reducing balance..");
            ReduceBalance(holderAddress, assetID, amount);

            return true;
        }

        private static bool SetFees(BigInteger takerFee, BigInteger makerFee)
        {
            Runtime.Log("Setting fees..");
            if (takerFee > maxFee || makerFee > maxFee) return false;
            if (takerFee < 0 || makerFee < 0) return false;

            Storage.Put(Storage.CurrentContext, "takerFee", takerFee);
            Storage.Put(Storage.CurrentContext, "makerFee", makerFee);

            return true;
        }

        private static bool SetFeeAddress(byte[] feeAddress)
        {
            Runtime.Log("Setting fee address..");
            if (feeAddress.Length != 20) return false;
            Storage.Put(Storage.CurrentContext, "feeAddress", feeAddress);

            return true;
        }

        private static bool VerifySentAmount(byte[] originator, byte[] assetID, byte[] assetCategory, BigInteger amount)
        {
            // Verify that the offer really has the indicated assets available
            if (assetCategory == SystemAsset)
            {
                // Check the current transaction for the system assets
                Runtime.Log("Verifying SystemAsset..");
                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                var outputs = currentTxn.GetOutputs();
                ulong sentAmount = 0;
                foreach (var o in outputs)
                {
                    Runtime.Log("Checking output..");
                    if (o.AssetId == assetID && o.ScriptHash == ExecutionEngine.ExecutingScriptHash)
                    {
                        Runtime.Log("Found a valid output!");
                        sentAmount += (ulong)o.Value;
                    }
                }
                if (sentAmount != amount) return false;
                return true;
            }
            else if (assetCategory == NEP5)
            {
                // Check allowance on smart contract
                Runtime.Log("Verifying NEP-5 token..");
                BigInteger allowedAmount = (BigInteger)CallRPXContract("allowance", originator, ExecutionEngine.ExecutingScriptHash);
                Runtime.Log("Checking allowance..");
                if (allowedAmount < amount) return false;
                return true;
            }

            // Unknown asset category
            return false;
        }

        private static Offer GetOffer(byte[] hash)
        {
            var offerData = Storage.Get(Storage.CurrentContext, OfferDetailsPrefix.Concat(hash));
            var offerAmount = Storage.Get(Storage.CurrentContext, OfferAmountPrefix.Concat(hash));
            var wantAmount = Storage.Get(Storage.CurrentContext, WantAmountPrefix.Concat(hash));
            var availableAmount = Storage.Get(Storage.CurrentContext, AvailableAmountPrefix.Concat(hash));

            if (offerData.Length == 0 || offerAmount.Length == 0 || wantAmount.Length == 0 || availableAmount.Length == 0) return new Offer(); // invalid offer hash

            var offerAssetIDLength = 20;
            var wantAssetIDLength = 20;
            if (offerData.Range(20, 1) == SystemAsset) offerAssetIDLength = 32;
            if (offerData.Range(21, 1) == SystemAsset) wantAssetIDLength = 32;

            var makerAddress = offerData.Range(0, 20);
            var offerAssetID = offerData.Range(22, offerAssetIDLength);
            var wantAssetID = offerData.Range(22 + offerAssetIDLength, wantAssetIDLength);
            var nonce = offerData.Range(22 + offerAssetIDLength + wantAssetIDLength, 32);

            return NewOffer(makerAddress, offerAssetID, offerAmount, wantAssetID, wantAmount, availableAmount, nonce);
        }

        private static void StoreOffer(Offer offer)
        {
            Runtime.Log("Storing offer..");
            var offerHash = Hash(offer); // TODO: we seem to be hashing too many times unneccessarily 

            // Remove offer if completely filled
            if (offer.AvailableAmount == 0)
            {
                Runtime.Log("Removing depleted offer..");
                RemoveOffer(offer);
            }
            // Store offer otherwise
            else
            {
                Runtime.Log("Serializing offer..");
                var offerData = offer.MakerAddress.Concat(offer.OfferAssetCategory).Concat(offer.WantAssetCategory).Concat(offer.OfferAssetID).Concat(offer.WantAssetID).Concat(offer.Nonce);
                Storage.Put(Storage.CurrentContext, OfferDetailsPrefix.Concat(offerHash), offerData);
                Storage.Put(Storage.CurrentContext, OfferAmountPrefix.Concat(offerHash), ToBytes(offer.OfferAmount));
                Storage.Put(Storage.CurrentContext, WantAmountPrefix.Concat(offerHash), ToBytes(offer.WantAmount));
                Storage.Put(Storage.CurrentContext, AvailableAmountPrefix.Concat(offerHash), ToBytes(offer.AvailableAmount));
            }
        }

        private static void AddOffer(Offer offer)
        {
            var tradingPair = TradingPair(offer);
            var previousOfferHash = Storage.Get(Storage.CurrentContext, tradingPair);

            // Add an edge to the previous HEAD of the linked list for this trading pair
            if (previousOfferHash.Length > 0)
            {
                Runtime.Log("Setting previous offer hash..");
                offer.PreviousOfferHash = previousOfferHash;
            }

            // Set the HEAD of the linked list for this trading pair as this offer
            Storage.Put(Storage.CurrentContext, tradingPair, Hash(offer));

            // Store the offer
            StoreOffer(offer);            
        }

        private static void RemoveOffer(Offer offer)
        {
            var tradingPair = TradingPair(offer);
            var offerHash = Hash(offer); // TODO: we seem to be hashing too many times unneccessarily 

            Runtime.Log("Finding offer in list..");
            var head = Storage.Get(Storage.CurrentContext, tradingPair);
            if (head == offerHash) // This offer is the HEAD - simple case!
            {                
                // Set the new HEAD of the linked list to the previous offer if there is one
                if (offer.PreviousOfferHash.Length > 0)
                {
                    Runtime.Log("Updating new trading pair list head..");
                    Storage.Put(Storage.CurrentContext, tradingPair, offer.PreviousOfferHash);
                }
                // Remove the list HEAD since this was the only offer left
                else
                {
                    Runtime.Log("Clearing trading pair list..");
                    Storage.Delete(Storage.CurrentContext, tradingPair);
                }
            }
            else if (offer.PreviousOfferHash.Length > 0) // Find the preceding offer 
            {
                Runtime.Log("Searching for preceding offer..");
                do
                {
                    var search = GetOffer(head);
                    Runtime.Log("Comparing offer hash..");
                    if (search.PreviousOfferHash == offerHash)
                    {
                        // Move the incoming edge from the preceding offer to the offer before this one
                        Runtime.Log("Found offer, moving edges..");
                        search.PreviousOfferHash = offer.PreviousOfferHash;
                        StoreOffer(search);
                        break;
                    }
                } while (true);
            }
            
            // Delete offer data
            Storage.Delete(Storage.CurrentContext, OfferDetailsPrefix.Concat(offerHash));
            Storage.Delete(Storage.CurrentContext, OfferAmountPrefix.Concat(offerHash));
            Storage.Delete(Storage.CurrentContext, WantAmountPrefix.Concat(offerHash));
            Storage.Delete(Storage.CurrentContext, AvailableAmountPrefix.Concat(offerHash));
        }

        private static bool WithdrawingSystemAsset()
        {
            var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
            var txnAttributes = currentTxn.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == 0xa1 && attr.Data == Yes) return true;
            }
            return false;
        }

        private static void TransferAssetTo(byte[] address, byte[] assetID, BigInteger amount)
        {
            if (!(amount > 0)) return;

            byte[] key = StoreKey(address, assetID);
            BigInteger currentBalance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            Storage.Put(Storage.CurrentContext, key, currentBalance + amount);
        }

        private static void ReduceBalance(byte[] address, byte[] assetID, BigInteger amount)
        {
            if (!(amount > 0)) return;

            var key = StoreKey(address, assetID);
            var currentBalance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            if (currentBalance - amount > 0) Storage.Put(Storage.CurrentContext, key, currentBalance - amount);
            else Storage.Delete(Storage.CurrentContext, key);

            // Notify runtime
            //Withdrawn(holderAddress, assetID, (byte)assetCategory, amount);
        }

        private static byte[] ToBytes(BigInteger value)
        {
            byte[] buffer = value.ToByteArray();
            return buffer;
        }

        private static BigInteger AmountToOffer(Offer o, BigInteger amount)
        {
            return (o.OfferAmount * amount) / o.WantAmount;
        }

        private static byte[] StoreKey(byte[] owner, byte[] assetID)
        {
            return owner.Concat(assetID);
        }

        private static byte[] TradingPair(Offer o)
        {
            Runtime.Log("Deriving trading pair..");
            return o.OfferAssetID.Concat(o.WantAssetID);
        }

        private static byte[] Hash(Offer o)
        {
            Runtime.Log("Serializing amounts..");
            var offerAmountBuffer = ToBytes(o.OfferAmount);
            var wantAmountBuffer = ToBytes(o.WantAmount);

            Runtime.Log("Joining buffers..");
            var bytes = o.MakerAddress
                .Concat(TradingPair(o))
                .Concat(offerAmountBuffer)
                .Concat(wantAmountBuffer)
                .Concat(o.Nonce);

            Runtime.Log("Calculating offer hash..");
            return Hash256(bytes);
        }
    }
}

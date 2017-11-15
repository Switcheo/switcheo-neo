using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System.Numerics;

namespace switcheo
{
    public class BrokerContract : SmartContract
    {
        [Appcall("3a5ae8c529a96007831e1fdcae1bff3af35548dc")] // TODO: Hardcode RPX ScriptHash - pending [DynamicCall] support
        public static extern object CallExternalContract(string method, params object[] args);

        //[DisplayName("created")]
        //public static event Action<byte[]> Created; // (offerHash)

        //[DisplayName("filled")]
        //public static event Action<byte[], BigInteger> Filled; // (offerHash, amount)

        //[DisplayName("cancelled")]
        //public static event Action<byte[]> Cancelled; // (offerHash)

        //[DisplayName("transferred")]
        //public static event Action<byte[], byte[], BigInteger> Transferred; // (address, assetID, amount)

        //[DisplayName("withdrawn")]
        //public static event Action<byte[], byte[], BigInteger> Withdrawn; // (address, assetID, amount)

        private static readonly byte[] Owner = { 2, 86, 121, 88, 238, 62, 78, 230, 177, 3, 68, 142, 10, 254, 31, 223, 139, 87, 150, 110, 30, 135, 156, 120, 59, 17, 101, 55, 236, 191, 90, 249, 113 };
        //private const ulong assetFactor = 100000000;
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
            byte[] availableAmount, byte[] previousOfferHash
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
                PreviousOfferHash = previousOfferHash,
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
                // XXX: There is no currently way to check that this contract is invoked from the verification phase
                if (!WithdrawingSystemAsset()) return false;

                // Verify that each output is allowed
                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                var outputs = currentTxn.GetOutputs();
                foreach (var o in outputs)
                {
                    if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash && 
                        !VerifyWithdrawal(o.ScriptHash, o.AssetId, o.Value)) return false;
                }

                // TODO: ensure that gas isn't burnt?

                return true;
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
                Runtime.Log("Application trigger");

                // == Withdrawal of SystemAsset ==
                // XXX: can the vm be crashed after verification by manipulating the invoke AppCall args?
                if (WithdrawingSystemAsset())
                {
                    var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                    var outputs = currentTxn.GetOutputs();
                    foreach (var o in outputs)
                    {
                        if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash)
                        {
                            Runtime.Log("Found a withdrawal..");
                            ReduceBalance(o.ScriptHash, o.AssetId, o.Value);
                        }
                    }
                    // return true;
                }

                // == Init ==
                if (operation == "initialize")
                {
                    if (!Runtime.CheckWitness(Owner))
                    {
                        Runtime.Log("Owner signature verification failed!");
                        return false;
                    }
                    if (args.Length != 3) return false;
                    return Initialize((BigInteger)args[0], (BigInteger)args[1], (byte[])args[2]);
                }

                // == Execute ==
                if (operation == "makeOffer")
                {
                    if (Storage.Get(Storage.CurrentContext, "state") == Inactive)
                    {
                        Runtime.Log("Contract is inactive!");
                        return false;
                    }
                    if (args.Length != 6) return false;
                    var offer = NewOffer((byte[])args[0], (byte[])args[1], (byte[])args[2], (byte[])args[3], (byte[])args[4], (byte[])args[2], null);
                    var offerHash = Hash(offer, (byte[])args[5]);

                    if (VerifyOffer(offerHash, offer))
                    {
                        return MakeOffer(offerHash, offer);
                    }
                    else
                    {
                        Runtime.Log("Offer is invalid!");
                        // TODO: RefundAllInputs()
                        return false;
                    }
                }
                if (operation == "fillOffer")
                {
                    if (Storage.Get(Storage.CurrentContext, "state") == Inactive)
                    {
                        Runtime.Log("Contract is inactive!");
                        return false;
                    }
                    if (args.Length != 3) return false;
                    if (VerifyFill((byte[])args[0], (byte[])args[1], (BigInteger)args[2]))
                    {
                        return FillOffer((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                    }
                    else
                    {
                        Runtime.Log("Fill is invalid!");
                        // TODO: RefundAllInputs()
                        return false;
                    }
                }
                if (operation == "cancelOffer")
                {
                    if (args.Length != 1) return false;
                    return CancelOffer((byte[])args[0]);
                }
                if (operation == "withdrawAssets") // NEP-5 only
                {
                    if (args.Length != 3) return false;
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

            return true;
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

        private static bool VerifyOffer(byte[] offerHash, Offer offer)
        {
            // Check that transaction is signed by the maker
            if (!Runtime.CheckWitness(offer.MakerAddress)) return false;

            // Check that nonce is not repeated
            if (Storage.Get(Storage.CurrentContext, OfferDetailsPrefix.Concat(offerHash)).Length != 0) return false;

            // Check that the amounts > 0
            if (!(offer.OfferAmount > 0 && offer.WantAmount > 0)) return false;
            
            // Check the trade is across different assets
            if (offer.OfferAssetID == offer.WantAssetID) return false;

            // Check that asset IDs are valid
            if ((offer.OfferAssetID.Length != 20 && offer.OfferAssetID.Length != 32) ||
                (offer.WantAssetID.Length != 20 && offer.WantAssetID.Length != 32)) return false;

            // Verify that the offer txn has really has the indicated assets available
            return VerifySentAmount(offer.OfferAssetID, offer.OfferAssetCategory, offer.OfferAmount);
        }

        private static bool MakeOffer(byte[] offerHash, Offer offer)
        {
            // Transfer NEP-5 token if required
            if (offer.OfferAssetCategory == NEP5)
            {
                Runtime.Log("Transferring NEP-5 token..");
                bool transferSuccessful = (bool)CallExternalContract("transfer", offer.MakerAddress, ExecutionEngine.ExecutingScriptHash, offer.OfferAmount);
                if (!transferSuccessful) {
                    Runtime.Log("Failed to transfer NEP-5 tokens!");
                    return false;
                }
            }

            AddOffer(offerHash, offer);

            // Notify runtime
            Runtime.Log("Offer made successfully!");
            return true;
        }

        private static bool VerifyFill(byte[] fillerAddress, byte[] offerHash, BigInteger amountToFill)
        {
            // Check that transaction is signed by the filler
            if (!Runtime.CheckWitness(fillerAddress)) return false;

            // Check that the offer exists 
            Offer offer = GetOffer(offerHash);
            if (offer.MakerAddress == Empty) return false;

            // Check that the filler is different from the maker
            if (fillerAddress == offer.MakerAddress) return false;

            // Check that amount to offer <= available amount
            BigInteger amountToOffer = AmountToOffer(offer, amountToFill);
            if (amountToOffer > offer.AvailableAmount || amountToOffer < 1) return false;

            // Verify that the filling txn really has the required assets available
            return VerifySentAmount(offer.WantAssetID, offer.WantAssetCategory, amountToFill);
        }

        private static bool FillOffer(byte[] fillerAddress, byte[] offerHash, BigInteger amountToFill)
        {
            // Get offer
            Offer offer = GetOffer(offerHash);

            // Calculate offered amount and fees
            BigInteger amountToOffer = AmountToOffer(offer, amountToFill);
            BigInteger makerFeeRate = Storage.Get(Storage.CurrentContext, "makerFee").AsBigInteger();
            BigInteger takerFeeRate = Storage.Get(Storage.CurrentContext, "takerFee").AsBigInteger();
            BigInteger makerFee = (amountToFill * makerFeeRate) / feeFactor;
            BigInteger takerFee = (amountToOffer * takerFeeRate) / feeFactor;

            // Move fees
            TransferAssetTo(Owner, offer.WantAssetID, makerFee);
            TransferAssetTo(Owner, offer.OfferAssetID, takerFee);

            // Move asset to the maker balance
            TransferAssetTo(offer.MakerAddress, offer.WantAssetID, amountToFill - makerFee);
            //Transferred(offer.MakerAddress, offer.WantAssetID, (byte) offer.WantAssetCategory, amountToFill - makerFee);

            // Move asset to the taker balance
            TransferAssetTo(fillerAddress, offer.OfferAssetID, amountToOffer - takerFee);
            //Transferred(fillerAddress, offer.OfferAssetID, (byte)offer.OfferAssetCategory, amountToOffer - takerFee);

            // Update available amount
            offer.AvailableAmount = offer.AvailableAmount - amountToOffer;

            // Store updated offer
            StoreOffer(offerHash, offer);

            // Notify runtime
            Runtime.Log("Offer filled successfully!");
            return true;
        }

        private static bool CancelOffer(byte[] offerHash)
        {
            // Check that the offer exists
            Offer offer = GetOffer(offerHash);
            if (offer.MakerAddress == Empty) return false;

            // Check that transaction is signed by the canceller
            if (!Runtime.CheckWitness(offer.MakerAddress)) return false;

            // Move funds to withdrawal address
            var storeKey = StoreKey(offer.MakerAddress, offer.OfferAssetID);
            BigInteger balance = Storage.Get(Storage.CurrentContext, storeKey).AsBigInteger();
            Storage.Put(Storage.CurrentContext, storeKey, balance + offer.AvailableAmount);

            // Remove offer
            RemoveOffer(offerHash, offer);

            // Notify runtime
            Runtime.Log("Offer cancelled successfully!");
            return true;
        }
        
        private static bool VerifyWithdrawal(byte[] holderAddress, byte[] assetID, BigInteger amount)
        {
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
            Runtime.Log("Transferring NEP-5 token..");
            bool transferSuccessful = (bool)CallExternalContract("transfer", ExecutionEngine.ExecutingScriptHash, holderAddress, amount);
            if (!transferSuccessful) return false;

            Runtime.Log("Reducing balance..");
            ReduceBalance(holderAddress, assetID, amount);

            Runtime.Log("Assets withdrawn successfully!");
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

        private static bool VerifySentAmount(byte[] assetID, byte[] assetCategory, BigInteger amount)
        {
            // Verify that the offer really has the indicated assets available
            if (assetCategory == SystemAsset)
            {
                // Check the current transaction for the system assets
                Runtime.Log("Verifying SystemAsset amount..");
                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                var outputs = currentTxn.GetOutputs();
                ulong sentAmount = 0;
                foreach (var o in outputs)
                {
                    if (o.AssetId == assetID && o.ScriptHash == ExecutionEngine.ExecutingScriptHash)
                    {
                        sentAmount += (ulong)o.Value;
                    }
                }

                // Get previously consumed amount wihtin same transaction
                var consumedAmount = Storage.Get(Storage.CurrentContext, currentTxn.Hash.Concat(assetID)).AsBigInteger();

                // Check that the sent amount is still sufficient
                if (sentAmount - consumedAmount < amount) {
                    Runtime.Log("Not enough of asset sent");
                    return false;   
                }

                // Update the consuemd amount for this txn
                Storage.Put(Storage.CurrentContext, currentTxn.Hash.Concat(assetID), consumedAmount + amount);

                // TODO: how to cleanup?
                return true;
            }
            else if (assetCategory == NEP5)
            {
                // Just skip this check and fail later if the transfer fails - saves gas cost and supports old NEP-5 standard

                // Check allowance on smart contract
                //Runtime.Log("Verifying NEP-5 token..");
                //BigInteger allowedAmount = (BigInteger)CallExternalContract("allowance", originator, ExecutionEngine.ExecutingScriptHash);
                //Runtime.Log("Checking allowance..");
                //if (allowedAmount < amount) return false;
                return true;
            }

            // Unknown asset category
            return false;
        }

        private static Offer GetOffer(byte[] hash)
        {
            Runtime.Log("Getting offer..");
            var offerData = Storage.Get(Storage.CurrentContext, OfferDetailsPrefix.Concat(hash));
            var offerAmount = Storage.Get(Storage.CurrentContext, OfferAmountPrefix.Concat(hash));
            var wantAmount = Storage.Get(Storage.CurrentContext, WantAmountPrefix.Concat(hash));
            var availableAmount = Storage.Get(Storage.CurrentContext, AvailableAmountPrefix.Concat(hash));

            Runtime.Log("Checking if retrieved data is a valid offer..");
            if (offerData.Length == 0 || offerAmount.Length == 0 || wantAmount.Length == 0 || availableAmount.Length == 0) return new Offer(); // invalid offer hash

            Runtime.Log("Building offer..");
            var offerAssetIDLength = 20;
            var wantAssetIDLength = 20;
            if (offerData.Range(20, 2) == SystemAsset) offerAssetIDLength = 32;
            if (offerData.Range(22, 2) == SystemAsset) wantAssetIDLength = 32;

            var makerAddress = offerData.Range(0, 20);
            var offerAssetID = offerData.Range(24, offerAssetIDLength);
            var wantAssetID = offerData.Range(24 + offerAssetIDLength, wantAssetIDLength);
            var previousOfferHash = offerData.Range(24 + offerAssetIDLength + wantAssetIDLength, 32);

            Runtime.Log("Initializing offer..");
            return NewOffer(makerAddress, offerAssetID, offerAmount, wantAssetID, wantAmount, availableAmount, previousOfferHash);
        }

        private static void StoreOffer(byte[] offerHash, Offer offer)
        {
            Runtime.Log("Storing offer..");

            // Remove offer if completely filled
            if (offer.AvailableAmount == 0)
            {
                Runtime.Log("Removing depleted offer..");
                RemoveOffer(offerHash, offer);
            }
            // Store offer otherwise
            else
            {
                Runtime.Log("Serializing offer..");
                // TODO: we can save storage space by not storing assetCategory / IDs?
                var offerData = offer.MakerAddress.Concat(offer.OfferAssetCategory).Concat(offer.WantAssetCategory).Concat(offer.OfferAssetID).Concat(offer.WantAssetID).Concat(offer.PreviousOfferHash);
                Runtime.Log("Serializing amounts..");
                // TODO: serialize these properly as a single key:
                Storage.Put(Storage.CurrentContext, OfferDetailsPrefix.Concat(offerHash), offerData);
                Storage.Put(Storage.CurrentContext, OfferAmountPrefix.Concat(offerHash), offer.OfferAmount);
                Storage.Put(Storage.CurrentContext, WantAmountPrefix.Concat(offerHash), offer.WantAmount);
                Storage.Put(Storage.CurrentContext, AvailableAmountPrefix.Concat(offerHash), offer.AvailableAmount);
            }
        }

        private static void AddOffer(byte[] offerHash, Offer offer)
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
            Storage.Put(Storage.CurrentContext, tradingPair, offerHash);

            // Store the offer
            StoreOffer(offerHash, offer);            
        }

        private static void RemoveOffer(byte[] offerHash, Offer offer)
        {
            Runtime.Log("Removing offer..");

            var tradingPair = TradingPair(offer);

            byte[] head = Storage.Get(Storage.CurrentContext, tradingPair);
            if (head == offerHash) // This offer is the HEAD - simple case!
            {                
                // Set the new HEAD of the linked list to the previous offer if there is one
                if (offer.PreviousOfferHash.Length > 0)
                {
                    Storage.Put(Storage.CurrentContext, tradingPair, offer.PreviousOfferHash);
                }
                // Remove the list HEAD since this was the only offer left
                else
                {
                    Storage.Delete(Storage.CurrentContext, tradingPair);
                }
            }
            else // Find the later offer 
            {
                Runtime.Log("Searching for later offer..");
                Offer search = GetOffer(head);
                do // XXX: this may break stack limits (1024) - use a doubly linked list?
                {
                    Runtime.Log("Comparing offer hash..");
                    if (search.PreviousOfferHash == offerHash)
                    {
                        // Move the incoming edge from the later offer to the previous offer
                        Runtime.Log("Found offer");
                        search.PreviousOfferHash = offer.PreviousOfferHash;
                        StoreOffer(head, search);
                        break;
                    }
                    else if (search.PreviousOfferHash.Length > 0)
                    {
                        Runtime.Log("Moving search ptr");
                        search = GetOffer(search.PreviousOfferHash);
                    }
                    else
                    {
                        Runtime.Log("Could not find offer in list any longer!");
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
            if (currentTxn.Type != 0xd1) return false;

            var txnAttributes = currentTxn.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == 0xa1 && attr.Data == Yes) return true;
            }

            return false;
        }

        private static void TransferAssetTo(byte[] address, byte[] assetID, BigInteger amount)
        {
            if (amount < 1) 
            {
                Runtime.Log("Amount to transfer is less than 1!");
                return;
            }

            byte[] key = StoreKey(address, assetID);
            BigInteger currentBalance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            Storage.Put(Storage.CurrentContext, key, currentBalance + amount);
        }

        private static void ReduceBalance(byte[] address, byte[] assetID, BigInteger amount)
        {
            if (amount < 1)
            {
                Runtime.Log("Amount to reduce is less than 1!");
                return;
            }

            var key = StoreKey(address, assetID);
            var currentBalance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            if (currentBalance - amount > 0) Storage.Put(Storage.CurrentContext, key, currentBalance - amount);
            else Storage.Delete(Storage.CurrentContext, key);
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
            return o.OfferAssetID.Concat(o.WantAssetID);
        }

        private static byte[] Hash(Offer o, byte[] nonce)
        {
            var offerAmountBuffer = ToBytes(o.OfferAmount);
            var wantAmountBuffer = ToBytes(o.WantAmount);

            var bytes = o.MakerAddress
                .Concat(TradingPair(o))
                .Concat(offerAmountBuffer)
                .Concat(wantAmountBuffer)
                .Concat(nonce);

            return Hash256(bytes);
        }
    }
}

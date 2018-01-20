using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System;
using System.ComponentModel;
using System.Numerics;

namespace switcheo
{
    public class BrokerContract : SmartContract
    {

        public delegate object NEP5Contract(string method, object[] args);

        [DisplayName("created")]
        public static event Action<byte[]> Created; // (offerHash)

        [DisplayName("filled")]
        public static event Action<byte[], byte[], BigInteger> Filled; // (address, offerHash, amount)

        [DisplayName("failed")]
        public static event Action<byte[], byte[]> Failed; // (address, offerHash)

        [DisplayName("cancelled")]
        public static event Action<byte[]> Cancelled; // (offerHash)

        [DisplayName("transferred")]
        public static event Action<byte[], byte[], BigInteger> Transferred; // (address, assetID, amount)

        [DisplayName("withdrawn")]
        public static event Action<byte[], byte[], BigInteger> Withdrawn; // (address, assetID, amount)

        private static readonly byte[] Owner = "AHDfSLZANnJ4N9Rj3FCokP14jceu3u7Bvw".ToScriptHash();
        private static readonly byte[] StakingToken = "ATqJk6ZBC36W6fSn6BetgwvJxq8JuKYCdk".ToScriptHash();
        private const ulong feeFactor = 1000000; // 1 => 0.0001%
        private const int maxFee = 5000; // 5000/1000000 = 0.5%
        private const int stakeDuration = 4000; // 4000 blocks => ~27 hrs @ 25s/block

        // Contract States
        private static readonly byte[] Pending = { };         // only can initialize
        private static readonly byte[] Active = { 0x01 };     // all operations active
        private static readonly byte[] Inactive = { 0x02 };   // trading halted - only can do cancel, withdrawl & owner actions

        // Asset Categories
        private static readonly byte[] SystemAsset = { 0x99 };
        private static readonly byte[] NEP5 = { 0x98 };
        
        // Flags / Byte Constants
        private static readonly byte[] Empty = { };
        private static readonly byte[] Withdrawing = { 0x50 };
        private static readonly byte[] FeesAccumulated = { 0x60 };
        private static readonly byte[] StakedAmount = { 0x61 };
        private static readonly byte[] StakedTime = { 0x62 };
        private static readonly byte[] StakedTotal = { 0x63 };
        private static readonly byte[] Zeroes = { 0, 0, 0, 0, 0, 0, 0, 0 }; // for fixed8 (8 bytes)
        private static readonly byte[] Null = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; // for fixed width list ptr (32bytes)        

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
            public byte[] PreviousOfferHash; // in same trading pair
            public byte[] NextOfferHash;     // in same trading pair
        }

        private static Offer NewOffer(
            byte[] makerAddress,
            byte[] offerAssetID, byte[] offerAmount,
            byte[] wantAssetID,  byte[] wantAmount,
            byte[] availableAmount, 
            byte[] previousOfferHash, byte[] nextOfferHash
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
                NextOfferHash = nextOfferHash
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
                // Check that the TransactionAttribute has been set to signify deduction for double withdrawal checks
                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                if (!IsWithdrawingSystemAsset(currentTxn)) return false;

                // Get the withdrawing address
                var withdrawingAddr = GetWithdrawalAddress(currentTxn);

                // Verify that each output is allowed
                var outputs = currentTxn.GetOutputs();
                ulong totalOut = 0;
                foreach (var o in outputs)
                {
                    // Get amount for each asset
                    var amount = GetAmountForAssetInOutputs(o.AssetId, outputs);
                    // Verify that the output address owns the balance 
                    if (!VerifyWithdrawal(withdrawingAddr, o.AssetId, amount)) return false;
                    // Accumulate total for checking against inputs later
                    totalOut += (ulong)o.Value;
                }

                // Check that all previous withdrawals has been cleared (SC amounts have been updated through invoke)
                var startOfWithdrawal = (uint)Storage.Get(Storage.CurrentContext, WithdrawalKey(withdrawingAddr)).AsBigInteger();
                var currentHeight = Blockchain.GetHeight();

                // Check that start of withdrawal has been initiated previously
                if (startOfWithdrawal == 0) return false;

                // Check that withdrawal was not already done
                for (var i = startOfWithdrawal; i < currentHeight; i++)
                {
                    var block = Blockchain.GetBlock(i);
                    var txns = block.GetTransactions();
                    foreach (var transaction in txns)
                    {
                        // Since this is flagged as a withdrawal from this contract,
                        // and it is signed by the withdrawing user,
                        // we know that an withdrawal has already been executed without 
                        // a corresponding application invocation to reduce balance,
                        // therefore we should reject further withdrawals.
                        if (IsWithdrawingSystemAsset(transaction) &&
                            GetWithdrawalAddress(transaction) == withdrawingAddr) return false;
                    }
                }

                // Ensure that nothing is burnt
                ulong totalIn = 0;
                foreach (var i in currentTxn.GetReferences()) totalIn += (ulong)i.Value;
                if (totalIn != totalOut) return false;

                return true;
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
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
                if (operation == "deposit")
                {
                    if (Storage.Get(Storage.CurrentContext, "state") != Active) return false;
                    if (IsWithdrawingSystemAsset((Transaction)ExecutionEngine.ScriptContainer)) return false;
                    if (args.Length != 3) return false;
                    if (!VerifySentAmount((byte[])args[0], (byte[])args[1], (BigInteger)args[2])) return false;
                    TransferAssetTo((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                    return true;
                }
                if (operation == "makeOffer")
                {
                    if (Storage.Get(Storage.CurrentContext, "state") != Active) return false;
                    if (args.Length != 6) return false;
                    var offer = NewOffer((byte[])args[0], (byte[])args[1], (byte[])args[2], (byte[])args[3], (byte[])args[4], (byte[])args[2], Null, Null);
                    var offerHash = Hash(offer, (byte[])args[5]);
                    return MakeOffer(offerHash, offer);
                }
                if (operation == "fillOffer")
                {
                    if (Storage.Get(Storage.CurrentContext, "state") != Active) return false;
                    if (args.Length != 3) return false;
                    return FillOffer((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                }
                if (operation == "stakeTokens")
                {
                    if (Storage.Get(Storage.CurrentContext, "state") != Active) return false;
                    if (args.Length != 2) return false;
                    return StakeTokens((byte[])args[0], (BigInteger)args[1]);
                }
                if (operation == "claimFees")
                {
                    if (Storage.Get(Storage.CurrentContext, "state") != Active) return false;
                    if (args.Length != 2) return false;
                    return ClaimFees((byte[])args[0], (byte[][])args[1], (BigInteger)args[2]);
                }
                if (operation == "cancelStake")
                {
                    if (Storage.Get(Storage.CurrentContext, "state") != Active) return false;
                    if (args.Length != 1) return false;
                    return CancelStake((byte[])args[0]);
                }

                // == Cancel / Withdraw ==
                if (Storage.Get(Storage.CurrentContext, "state") == Pending)
                {
                    Runtime.Log("Contract not initialized!");
                    return false;
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
                        return false;
                    }
                }
                if (operation == "prepareAssetWithdrawal")
                {
                    if (args.Length != 1) return false;
                    return PrepareAssetWithdrawal((byte[])args[0]);
                }
                if (operation == "completeAssetWithdrawal") // SystemAsset only
                {
                    var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                    if (!IsWithdrawingSystemAsset(currentTxn)) return false;

                    var outputs = currentTxn.GetOutputs();
                    foreach (var o in outputs)
                    {
                        if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash)
                        {
                            Runtime.Log("Found a withdrawal..");
                            if(!ReduceBalance(o.ScriptHash, o.AssetId, o.Value)) return false;
                        }
                    }

                    var withdrawingAddr = GetWithdrawalAddress(currentTxn);
                    Storage.Delete(Storage.CurrentContext, WithdrawalKey(withdrawingAddr));

                    return true;
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
            if (Storage.Get(Storage.CurrentContext, "state") != Pending) return false;
            if (!SetFees(takerFee, makerFee)) return false;
            if (!SetFeeAddress(feeAddress)) return false;
            
            Storage.Put(Storage.CurrentContext, "state", Active);

            Runtime.Log("Contract initialized");
            return true;
        }

        private static byte[] GetOffers(byte[] offerAssetID, byte[] wantAssetID)
        {
            return Storage.Get(Storage.CurrentContext, offerAssetID.Concat(wantAssetID));
        }

        private static bool MakeOffer(byte[] offerHash, Offer offer)
        {
            // Check that transaction is signed by the maker
            if (!Runtime.CheckWitness(offer.MakerAddress)) return false;

            // Check that nonce is not repeated
            if (Storage.Get(Storage.CurrentContext, offerHash).Length != 0) return false;

            // Check that the amounts > 0
            if (!(offer.OfferAmount > 0 && offer.WantAmount > 0)) return false;

            // Check the trade is across different assets
            if (offer.OfferAssetID == offer.WantAssetID) return false;

            // Check that asset IDs are valid
            if ((offer.OfferAssetID.Length != 20 && offer.OfferAssetID.Length != 32) ||
                (offer.WantAssetID.Length != 20 && offer.WantAssetID.Length != 32)) return false;

            // Reduce available balance for the offered asset and amount
            if (!ReduceBalance(offer.MakerAddress, offer.OfferAssetID, offer.OfferAmount)) return false;

            // Add the offer to storage
            AddOffer(offerHash, offer);

            // Notify clients
            Created(offerHash);
            return true;
        }
        
        private static bool FillOffer(byte[] fillerAddress, byte[] offerHash, BigInteger amountToFill)
        {
            // Check that transaction is signed by the filler
            if (!Runtime.CheckWitness(fillerAddress)) return false;

            // Check that the offer still exists 
            Offer offer = GetOffer(offerHash);
            if (offer.MakerAddress == Empty)
            {
                // Notify clients of failure
                Failed(fillerAddress, offerHash);
                return true;
            }

            // Check that the filler is different from the maker
            if (fillerAddress == offer.MakerAddress) return false;

            // Calculate max amount that can be offered & filled
            BigInteger amountToOffer = (offer.OfferAmount * amountToFill) / offer.WantAmount;
            if (amountToOffer > offer.AvailableAmount)
            {
                amountToOffer = offer.AvailableAmount;
                amountToFill = (amountToOffer * offer.WantAmount) / offer.OfferAmount;
            }

            // Check that the amount available is sufficient
            if (amountToOffer < 1)
            {
                // Notify clients of failure
                Failed(fillerAddress, offerHash);
                return true; // TODO: can we return false?
            }

            // Reduce available balance for the filled asset and amount
            if (!ReduceBalance(fillerAddress, offer.WantAssetID, amountToFill)) return false;

            // Calculate offered amount and fees
            BigInteger makerFeeRate = Storage.Get(Storage.CurrentContext, "makerFee").AsBigInteger();
            BigInteger takerFeeRate = Storage.Get(Storage.CurrentContext, "takerFee").AsBigInteger();
            BigInteger makerFee = (amountToFill * makerFeeRate) / feeFactor;
            BigInteger takerFee = (amountToOffer * takerFeeRate) / feeFactor;

            // Move fees
            BigInteger bucketNumber = Blockchain.GetHeight() / stakeDuration;
            var feeAddress = FeeAddressFor(bucketNumber);
            TransferAssetTo(feeAddress, offer.WantAssetID, makerFee);
            TransferAssetTo(feeAddress, offer.OfferAssetID, takerFee);

            // Move asset to the maker balance and notify clients
            TransferAssetTo(offer.MakerAddress, offer.WantAssetID, amountToFill - makerFee);
            Transferred(offer.MakerAddress, offer.WantAssetID, amountToFill - makerFee);

            // Move asset to the taker balance and notify clients
            TransferAssetTo(fillerAddress, offer.OfferAssetID, amountToOffer - takerFee);
            Transferred(fillerAddress, offer.OfferAssetID, amountToOffer - takerFee);

            // Update available amount
            offer.AvailableAmount = offer.AvailableAmount - amountToOffer;

            // Store updated offer
            StoreOffer(offerHash, offer);

            // Notify clients
            Filled(fillerAddress, offerHash, amountToFill);
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
            Cancelled(offerHash);
            return true;
        }
        
        private static bool VerifyWithdrawal(byte[] holderAddress, byte[] assetID, BigInteger amount)
        {
            // Check that there are asset value > 0 in balance
            var key = StoreKey(holderAddress, assetID);
            var balance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            if (balance < amount) return false;

            return true;
        }

        private static bool WithdrawAssets(byte[] holderAddress, byte[] assetID, BigInteger amount)
        {
            // Transfer token
            var contract = (NEP5Contract)assetID.ToDelegate();
            bool transferSuccessful = (bool)contract("transfer", new object[] { ExecutionEngine.ExecutingScriptHash, holderAddress, amount });
            if (!transferSuccessful)
            {
                Runtime.Log("Failed to transfer NEP-5 tokens!");
                return false;
            }
            
            // Reduce balance in storage
            if (!ReduceBalance(holderAddress, assetID, amount)) return false;

            // Notify clients
            Withdrawn(holderAddress, assetID, amount);

            return true;
        }

        private static bool PrepareAssetWithdrawal(byte[] holderAddress)
        {
            // Check that transaction is signed by the asset holder
            if (!Runtime.CheckWitness(holderAddress)) return false;

            // Get the key which marks start of withdrawal process
            var withdrawalKey = WithdrawalKey(holderAddress);
            
            // Check if already withdrawing
            if (Storage.Get(Storage.CurrentContext, withdrawalKey).Length != 0) return false;

            // Set blockheight from which to check for double withdrawals later on
            Storage.Put(Storage.CurrentContext, withdrawalKey, Blockchain.GetHeight());

            Runtime.Log("Prepared for asset withdrawal");

            return true;
        }

        private static bool StakeTokens(byte[] stakerAddress, BigInteger amount)
        {
            // Stake tokens from contract balance
            if (!ReduceBalance(stakerAddress, StakingToken, amount)) return false;

            // Get the next available bucket for staking
            BigInteger bucketNumber = Blockchain.GetHeight() / stakeDuration;

            // Get staking keys
            var stakedAmountKey = StakedAmount.Concat(stakerAddress);
            var stakedTimeKey = StakedTime.Concat(stakerAddress);
            var stakedTotalKey = StakedTotal.Concat(bucketNumber.AsByteArray());

            // Get staking values
            var stakedAmount = Storage.Get(Storage.CurrentContext, stakedAmountKey).AsBigInteger();
            var stakedTime = Storage.Get(Storage.CurrentContext, stakedTimeKey).AsBigInteger();
            var stakedTotal = Storage.Get(Storage.CurrentContext, stakedTotalKey).AsBigInteger();

            // Don't allow re-staking - must claim and cancel first
            if (stakedAmount > 0 || stakedTime > 0) return false; 

            // Update individual staked amount
            Storage.Put(Storage.CurrentContext, stakedAmountKey, amount);

            // Update start time of staking
            Storage.Put(Storage.CurrentContext, stakedTimeKey, bucketNumber);

            // Update total amount staked in the next bucket
            Storage.Put(Storage.CurrentContext, stakedTotalKey, stakedTotal + amount);

            return true;
        }

        private static bool ClaimFees(byte[] claimerAddress, byte[][] assetIDs, BigInteger bucketNumber)
        {
            // Get staking keys
            var stakedAmountKey = StakedAmount.Concat(claimerAddress);
            var stakedTimeKey = StakedTime.Concat(claimerAddress);
            var stakedTotalKey = StakedTotal.Concat(bucketNumber.AsByteArray());

            // Get staking values
            var stakedAmount = Storage.Get(Storage.CurrentContext, stakedAmountKey).AsBigInteger();
            var stakedTime = Storage.Get(Storage.CurrentContext, stakedTimeKey).AsBigInteger();
            var stakedTotal = Storage.Get(Storage.CurrentContext, stakedTotalKey).AsBigInteger();

            // Check that the claim is valid
            BigInteger currentBucketNumber = Blockchain.GetHeight() / stakeDuration;
            if (stakedAmount <= 0 || stakedTime < bucketNumber || bucketNumber >= currentBucketNumber) return false;
            
            // Move fees from fee addr to claimer addr
            foreach (byte[] assetID in assetIDs)
            {
                var feeAddress = FeeAddressFor(bucketNumber);
                var feesKey = StoreKey(feeAddress, assetID);
                var totalFees = Storage.Get(Storage.CurrentContext, feesKey).AsBigInteger();
                var claimableAmount = totalFees * stakedAmount / stakedTotal;
                if (claimableAmount > 0)
                {
                    TransferAssetTo(claimerAddress, assetID, claimableAmount);
                    if (!ReduceBalance(feeAddress, assetID, claimableAmount)) return false;
                }                
            }

            // Update staked time
            Storage.Put(Storage.CurrentContext, stakedTimeKey, bucketNumber);

            return true;
        }

        private static bool CancelStake(byte[] stakerAddress)
        {
            // Get the next available bucket for staking
            BigInteger bucketNumber = Blockchain.GetHeight() / stakeDuration;

            // Save staked amount and then remove it
            var stakedAmountKey = StakedAmount.Concat(stakerAddress);
            var stakedAmount = Storage.Get(Storage.CurrentContext, stakedAmountKey).AsBigInteger();            
            Storage.Delete(Storage.CurrentContext, stakedAmountKey);

            // Clean up associated timing
            var stakedTimeKey = StakedTime.Concat(stakerAddress);
            Storage.Delete(Storage.CurrentContext, stakedTimeKey);

            // Reduce total staked
            var stakedTotalKey = StakedTotal.Concat(bucketNumber.AsByteArray());
            var stakedTotal = Storage.Get(Storage.CurrentContext, stakedTotalKey).AsBigInteger();
            Storage.Put(Storage.CurrentContext, stakedTotalKey, stakedTotal - stakedAmount);

            // Allow withdrawing of previously staked asset
            TransferAssetTo(stakerAddress, StakingToken, stakedAmount);

            return true;
        }

        private static bool SetFees(BigInteger takerFee, BigInteger makerFee)
        {
            if (takerFee > maxFee || makerFee > maxFee) return false;
            if (takerFee < 0 || makerFee < 0) return false;

            Storage.Put(Storage.CurrentContext, "takerFee", takerFee);
            Storage.Put(Storage.CurrentContext, "makerFee", makerFee);

            return true;
        }

        private static bool SetFeeAddress(byte[] feeAddress)
        {
            if (feeAddress.Length != 20) return false;
            Storage.Put(Storage.CurrentContext, "feeAddress", feeAddress);

            return true;
        }

        private static bool VerifySentAmount(byte[] originator, byte[] assetID, BigInteger amount)
        {
            // Verify that the offer really has the indicated assets available
            if (assetID.Length == 32)
            {
                // Check the current transaction for the system assets
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

                // Update the consumed amount for this txn
                Storage.Put(Storage.CurrentContext, currentTxn.Hash.Concat(assetID), consumedAmount + amount);

                // TODO: how to cleanup?
                return true;
            }
            else if (assetID.Length == 20)
            {
                // Just transfer immediately or fail as this is the last step in verification
                var contract = (NEP5Contract)assetID.ToDelegate();
                var transferSuccessful = (bool)contract("transfer", new object[] { originator, ExecutionEngine.ExecutingScriptHash, amount });
                if (!transferSuccessful)
                {
                    Runtime.Log("Failed to transfer NEP-5 tokens!");
                    return false;
                }
                return true;
            }

            // Unknown asset category
            return false;
        }

        private static Offer GetOffer(byte[] hash)
        {
            // Check that offer exists
            var offerData = Storage.Get(Storage.CurrentContext, hash);
            if (offerData.Length == 0) return new Offer(); // invalid offer hash

            // Deserialize offer
            var index = 0;

            var makerAddress = offerData.Range(index, 20);
            index += 20;

            var offerAssetIDLength = 20;
            var wantAssetIDLength = 20;
            var typeLength = 2;
            var intLength = 8;
            var orderHashLength = 32;

            if (offerData.Range(index, typeLength) == SystemAsset) offerAssetIDLength = 32;
            index += typeLength;

            if (offerData.Range(index, typeLength) == SystemAsset) wantAssetIDLength = 32;
            index += typeLength;

            var offerAssetID = offerData.Range(index, offerAssetIDLength);
            index += offerAssetIDLength;

            var wantAssetID = offerData.Range(index, wantAssetIDLength);
            index += wantAssetIDLength;

            var offerAmount = offerData.Range(index, intLength);
            index += intLength;

            var wantAmount = offerData.Range(index, intLength);
            index += intLength;

            var availableAmount = offerData.Range(index, intLength);
            index += intLength;

            var previousOfferHash = offerData.Range(index, orderHashLength);
            index += orderHashLength;

            var nextOfferHash = offerData.Range(index, orderHashLength);
            index += orderHashLength;

            return NewOffer(makerAddress, offerAssetID, offerAmount, wantAssetID, wantAmount, availableAmount, previousOfferHash, nextOfferHash);
        }

        private static void StoreOffer(byte[] offerHash, Offer offer)
        {
            // Remove offer if completely filled
            if (offer.AvailableAmount == 0)
            {
                RemoveOffer(offerHash, offer);
            }
            // Store offer otherwise
            else
            {
                // Serialize offer
                // TODO: we can save storage space by not storing assetCategory / IDs and force clients to walk the list
                var offerData = offer.MakerAddress
                                     .Concat(offer.OfferAssetCategory)
                                     .Concat(offer.WantAssetCategory)
                                     .Concat(offer.OfferAssetID)
                                     .Concat(offer.WantAssetID)
                                     .Concat(offer.OfferAmount.AsByteArray().Concat(Zeroes).Take(8))
                                     .Concat(offer.WantAmount.AsByteArray().Concat(Zeroes).Take(8))
                                     .Concat(offer.AvailableAmount.AsByteArray().Concat(Zeroes).Take(8))
                                     .Concat(offer.PreviousOfferHash)
                                     .Concat(offer.NextOfferHash);
                
                Storage.Put(Storage.CurrentContext, offerHash, offerData);
            }
        }

        private static void AddOffer(byte[] offerHash, Offer offer)
        {
            var tradingPair = TradingPair(offer);
            var previousOfferHash = Storage.Get(Storage.CurrentContext, tradingPair);

            // Add edges to the previous HEAD of the linked list for this trading pair
            if (previousOfferHash != Null)
            {
                offer.PreviousOfferHash = previousOfferHash;
                var previousOffer = GetOffer(previousOfferHash);
                previousOffer.NextOfferHash = offerHash;
                StoreOffer(previousOfferHash, previousOffer); 
            }

            // Set the HEAD of the linked list for this trading pair as this offer
            Storage.Put(Storage.CurrentContext, tradingPair, offerHash);

            // Store the new offer
            StoreOffer(offerHash, offer);
        }

        private static void RemoveOffer(byte[] offerHash, Offer offer)
        {
            // Get the first item (head) in order book
            var tradingPair = TradingPair(offer);
            byte[] head = Storage.Get(Storage.CurrentContext, tradingPair);

            // Check if the offer is at the HEAD of the linked list
            if (head == offerHash) 
            {             
                // There are more offers in this list so set the new HEAD of the linked list to the previous offer
                if (offer.PreviousOfferHash != Null)
                {
                    Storage.Put(Storage.CurrentContext, tradingPair, offer.PreviousOfferHash);
                }
                // Otherwise, just remove the whole list since this is the only offer left
                else
                {
                    Storage.Delete(Storage.CurrentContext, tradingPair);
                }
            }

            // Combine nodes with an bi-directional edge
            if (offer.NextOfferHash != Null)
            {
                var nextOffer = GetOffer(offer.NextOfferHash);
                nextOffer.PreviousOfferHash = offer.PreviousOfferHash;
                StoreOffer(offer.NextOfferHash, nextOffer);
            }
            if (offer.PreviousOfferHash != Null)
            {
                var previousOffer = GetOffer(offer.PreviousOfferHash);
                previousOffer.NextOfferHash = offer.NextOfferHash;
                StoreOffer(offer.PreviousOfferHash, previousOffer);
            }

            // Delete offer data
            Storage.Delete(Storage.CurrentContext, offerHash);
        }

        private static ulong GetAmountForAssetInOutputs(byte[] assetID, TransactionOutput[] outputs)
        {
            ulong amount = 0;
            foreach (var o in outputs)
            {
                if (o.AssetId == assetID && o.ScriptHash != ExecutionEngine.ExecutingScriptHash) amount += (ulong)o.Value;
            }

            return amount;
        }

        private static byte[] GetWithdrawalAddress(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                // This is the additional verification script which can be used
                // to ensure any withdrawal txns are intended by the owner.
                if (attr.Usage == 0x20) return attr.Data.Take(20);
            }
            return Empty;
        }

        private static bool IsWithdrawingSystemAsset(Transaction transaction)
        {
            // Check that transaction is an Invocation transaction
            if (transaction.Type != 0xd1) return false;

            // Check that the transaction is marked as a SystemAsset withdrawal
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == 0xa1 && attr.Data.Take(20) == ExecutionEngine.ExecutingScriptHash) return true;
            }

            return false;
        }

        private static void TransferAssetTo(byte[] originator, byte[] assetID, BigInteger amount)
        {
            if (amount < 1) 
            {
                Runtime.Log("Amount to transfer is less than 1!");
                return;
            }

            byte[] key = StoreKey(originator, assetID);
            BigInteger currentBalance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            Storage.Put(Storage.CurrentContext, key, currentBalance + amount);
        }

        private static bool ReduceBalance(byte[] address, byte[] assetID, BigInteger amount)
        {
            if (amount < 1)
            {
                Runtime.Log("Amount to reduce is less than 1!");
                return false;
            }

            var key = StoreKey(address, assetID);
            var currentBalance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            var newBalance = currentBalance - amount;

            if (newBalance < 0)
            {
                Runtime.Log("Not enough balance!");
                return false;
            }

            if (newBalance > 0) Storage.Put(Storage.CurrentContext, key, newBalance);
            else Storage.Delete(Storage.CurrentContext, key);

            return true;
        }

        private static BigInteger AmountToOffer(Offer o, BigInteger amount)
        {
            return (o.OfferAmount * amount) / o.WantAmount;
        }

        private static byte[] WithdrawalKey(byte[] owner)
        {
            return owner.Concat(Withdrawing);
        }

        private static byte[] StoreKey(byte[] owner, byte[] assetID)
        {
            return owner.Concat(assetID);
        }

        private static byte[] FeeAddressFor(BigInteger bucketNumber)
        {
            return FeesAccumulated.Concat(bucketNumber.AsByteArray());
        }

        private static byte[] TradingPair(Offer o)
        {
            return o.OfferAssetID.Concat(o.WantAssetID);
        }

        private static byte[] Hash(Offer o, byte[] nonce)
        {
            var bytes = o.MakerAddress
                .Concat(TradingPair(o))
                .Concat(o.OfferAmount.AsByteArray())
                .Concat(o.WantAmount.AsByteArray())
                .Concat(nonce);

            return Hash256(bytes);
        }
    }
}

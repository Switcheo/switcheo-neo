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

        // Events
        [DisplayName("created")]
        public static event Action<byte[], byte[], byte[], BigInteger, byte[], BigInteger> Created; // (address, offerHash, offerAssetID, offerAmount, wantAssetID, wantAmount)

        [DisplayName("filled")]
        public static event Action<byte[], byte[], BigInteger, byte[], BigInteger, byte[], BigInteger> Filled; // (address, offerHash, fillAmount, offerAssetID, offerAmount, wantAssetID, wantAmount)

        [DisplayName("failed")]
        public static event Action<byte[], byte[]> Failed; // (address, offerHash)

        [DisplayName("cancelled")]
        public static event Action<byte[], byte[]> Cancelled; // (address, offerHash)

        [DisplayName("transferred")]
        public static event Action<byte[], byte[], BigInteger> Transferred; // (address, assetID, amount)

        [DisplayName("withdrawing")]
        public static event Action<byte[], byte[], BigInteger> Withdrawing; // (address, assetID, amount)

        [DisplayName("withdrawn")]
        public static event Action<byte[], byte[], BigInteger> Withdrawn; // (address, assetID, amount)

        // Broker Settings & Hardcaps
        private static readonly byte[] Owner = "AHDfSLZANnJ4N9Rj3FCokP14jceu3u7Bvw".ToScriptHash();
        private static readonly byte[] NativeToken = "AYdPyCbHS3MZoJDeZSntgdnDbpa5ScXade".ToScriptHash();
        private const ulong feeFactor = 1000000; // 1 => 0.0001%
        private const int maxFee = 5000; // 5000/1000000 = 0.5%
        private const int bucketDuration = 82800; // 82800secs = 23hrs
        private const int nativeTokenDiscount = 2; // 1/2 => 50%

        // Contract States
        private static readonly byte[] Pending = { };         // only can initialize
        private static readonly byte[] Active = { 0x01 };     // all operations active
        private static readonly byte[] Inactive = { 0x02 };   // trading halted - only can do cancel, withdrawl & owner actions

        // Asset Categories
        private static readonly byte[] SystemAsset = { 0x99 };
        private static readonly byte[] NEP5 = { 0x98 };

        // Native Token Flags
        private static readonly byte[] Native = { 0x70 };
        private static readonly byte[] Foreign = { 0x71 };

        // Withdrawal Flags
        private static readonly byte[] Mark = { 0x50 };
        private static readonly byte[] Withdraw = { 0x51 };
        private static readonly byte[] OpCode_TailCall = { 0x69 };
        private static readonly byte Type_InvocationTransaction = 0xd1;
        private static readonly byte TAUsage_WithdrawalStage = 0xa1;
        private static readonly byte TAUsage_NEP5AssetID = 0xa2;
        private static readonly byte TAUsage_SystemAssetID = 0xa3;
        private static readonly byte TAUsage_WithdrawalAddress = 0xa4;
        private static readonly byte TAUsage_AdditionalWitness = 0x20; // additional verification script which can be used to ensure any withdrawal txns are intended by the owner

        // Byte Constants
        private static readonly byte[] Empty = { };
        private static readonly byte[] Zeroes = { 0, 0, 0, 0, 0, 0, 0, 0 }; // for fixed8 (8 bytes)
        private static readonly byte[] Null = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; // for fixed width list ptr (32bytes)        
        private static readonly byte[] GasAssetID = { 231, 45, 40, 105, 121, 238, 108, 177, 183, 230, 93, 253, 223, 178, 227, 132, 16, 11, 141, 20, 142, 119, 88, 222, 66, 228, 22, 139, 113, 121, 44, 96 };
        private static readonly byte[] WithdrawArgs = new byte[] { 0x00, 0xc1, 0x08 }.Concat("withdraw".AsByteArray()); // PUSH0, PACK, PUSHBYTES8, "withdraw" as bytes

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
        }

        private static Offer NewOffer(
            byte[] makerAddress,
            byte[] offerAssetID, byte[] offerAmount,
            byte[] wantAssetID, byte[] wantAmount,
            byte[] availableAmount,
            byte[] nonce
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
                Nonce = nonce,
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
                if (GetState() == Pending) return false;

                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                var withdrawalStage = WithdrawalStage(currentTxn);
                var withdrawingAddr = GetWithdrawalAddress(currentTxn, withdrawalStage);
                var assetID = GetWithdrawalAsset(currentTxn);
                var isWithdrawingNEP5 = assetID.Length == 20;
                var inputs = currentTxn.GetInputs();
                var outputs = currentTxn.GetOutputs();

                ulong totalOut = 0;
                if (withdrawalStage == Mark)
                {
                    // Check that txn is signed
                    if (!Runtime.CheckWitness(withdrawingAddr)) return false;

                    // Check that withdrawal is possible
                    if (!VerifyWithdrawal(withdrawingAddr, assetID)) return false;

                    // Check that inputs are not already reserved
                    foreach (var i in inputs)
                    {
                        if (Storage.Get(Context(), i.PrevHash.Concat(IndexAsByteArray(i.PrevIndex))).Length > 0) return false;
                    }

                    // Check that outputs are a valid self-send
                    var authorizedAssetID = isWithdrawingNEP5 ? GasAssetID : assetID;
                    foreach (var o in outputs)
                    {
                        totalOut += (ulong)o.Value;
                        if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash) return false;
                        if (o.AssetId != authorizedAssetID) return false;
                    }
                    // TODO: should also check outputs.Length for SystemAsset (at most +1 of required)

                    // Check that NEP5 withdrawals don't reserve more utxos than required
                    if (isWithdrawingNEP5)
                    {
                        if (inputs.Length > 1) return false;
                        if (outputs.Length > 2) return false;
                        if (outputs[0].Value > 1) return false;
                    }
                }
                else if (withdrawalStage == Withdraw)
                {
                    // Check that utxo has been reserved
                    foreach (var i in inputs)
                    {
                        if (Storage.Get(Context(), i.PrevHash.Concat(IndexAsByteArray(i.PrevIndex))) != withdrawingAddr) return false;
                    }

                    // Check withdrawal destinations
                    var authorizedAssetID = isWithdrawingNEP5 ? GasAssetID : assetID;
                    var authorizedAddress = isWithdrawingNEP5 ? ExecutionEngine.ExecutingScriptHash : withdrawingAddr;
                    foreach (var o in outputs)
                    {
                        totalOut += (ulong)o.Value;
                        if (o.AssetId != authorizedAssetID) return false;
                        if (o.ScriptHash != authorizedAddress) return false;
                    }

                    // Check withdrawal amount
                    var authorizedAmount = isWithdrawingNEP5 ? 1 : GetWithdrawAmount(withdrawingAddr, assetID);
                    if (totalOut != authorizedAmount) return false;
                }
                else
                {
                    return false;
                }

                // Ensure that nothing is burnt
                ulong totalIn = 0;
                foreach (var i in currentTxn.GetReferences()) totalIn += (ulong)i.Value;
                if (totalIn != totalOut) return false;

                // Check that Application trigger will be tail called
                if (currentTxn.Type != Type_InvocationTransaction) return false;
                var invocationTransaction = (InvocationTransaction)currentTxn;
                if (invocationTransaction.Script != WithdrawArgs.Concat(OpCode_TailCall).Concat(ExecutionEngine.ExecutingScriptHash)) return false; 

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

                // == Getters ==
                if (operation == "getState") return GetState();
                if (operation == "getMakerFee") return GetMakerFee();
                if (operation == "getTakerFee") return GetTakerFee();
                if (operation == "getExchangeRate") return GetExchangeRate((byte[])args[0]);
                if (operation == "getOffers") return GetOffers((byte[])args[0], (BigInteger)args[1]);
                if (operation == "getBalance") return GetBalance((byte[])args[0], (byte[])args[1]);

                // == Execute ==
                if (operation == "deposit")
                {
                    if (GetState() != Active) return false;
                    if (args.Length != 3) return false;
                    if (!VerifySentAmount((byte[])args[0], (byte[])args[1], (BigInteger)args[2])) return false;
                    TransferAssetTo((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                    return true;
                }
                if (operation == "makeOffer")
                {
                    if (GetState() != Active) return false;
                    if (args.Length != 6) return false;
                    var offer = NewOffer((byte[])args[0], (byte[])args[1], (byte[])args[2], (byte[])args[3], (byte[])args[4], (byte[])args[2], (byte[])args[5]);
                    return MakeOffer(offer);
                }
                if (operation == "fillOffer")
                {
                    if (GetState() != Active) return false;
                    if (args.Length != 5) return false;
                    return FillOffer((byte[])args[0], (byte[])args[1], (byte[])args[2], (BigInteger)args[3], (bool)args[4]);
                }
                if (operation == "cancelOffer")
                {
                    if (GetState() == Pending) return false;
                    if (args.Length != 2) return false;
                    return CancelOffer((byte[])args[0], (byte[])args[1]);
                }

                // == Withdrawal ==
                if (operation == "withdraw")
                {
                    return ProcessWithdrawal();
                }

                // == Owner ==
                if (!Runtime.CheckWitness(Owner))
                {
                    Runtime.Log("Owner signature verification failed");
                    return false;
                }
                if (operation == "freezeTrading")
                {
                    Storage.Put(Context(), "state", Inactive);
                    return true;
                }
                if (operation == "unfreezeTrading")
                {
                    Storage.Put(Context(), "state", Active);
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
                if (operation == "addToWhitelist")
                {
                    if (args.Length != 1) return false;
                    if (Storage.Get(Context(), "stateContractWhitelist") == Inactive) return false;
                    Storage.Put(Context(), WhitelistKey((byte[])args[0]), "1");
                }
                if (operation == "removeFromWhitelist")
                {
                    if (args.Length != 1) return false;
                    Storage.Delete(Context(), WhitelistKey((byte[])args[0]));
                }
                if (operation == "destroyWhitelist")
                {
                    Storage.Put(Context(), "stateContractWhitelist", Inactive);
                }
            }

            return true;
        }

        private static bool Initialize(BigInteger takerFee, BigInteger makerFee, byte[] feeAddress)
        {
            if (GetState() != Pending) return false;
            if (!SetFees(takerFee, makerFee)) return false;
            if (!SetFeeAddress(feeAddress)) return false;

            Storage.Put(Context(), "state", Active);

            Runtime.Log("Contract initialized");
            return true;
        }

        private static byte[] GetState()
        {
            return Storage.Get(Context(), "state");
        }

        private static BigInteger GetMakerFee()
        {
            return Storage.Get(Context(), "makerFee").AsBigInteger();
        }

        private static BigInteger GetTakerFee()
        {
            return Storage.Get(Context(), "takerFee").AsBigInteger();
        }

        private static BigInteger GetBalance(byte[] originator, byte[] assetID)
        {
            return Storage.Get(Context(), BalanceKey(originator, assetID)).AsBigInteger();
        }

        private static BigInteger GetWithdrawAmount(byte[] originator, byte[] assetID)
        {
            return Storage.Get(Context(), BalanceKey(originator, assetID)).AsBigInteger();
        }

        private static BigInteger[] GetExchangeRate(byte[] assetID) // against native token
        {
            var bucketNumber = CurrentBucket() - 1;
            var nativeVolume = Storage.Get(Context(), NativeVolumeKey(assetID, bucketNumber)).AsBigInteger();
            var otherVolume = Storage.Get(Context(), ForeignVolumeKey(assetID, bucketNumber)).AsBigInteger();

            return new BigInteger[] { otherVolume, nativeVolume };
        }

        private static Offer[] GetOffers(byte[] tradingPair, BigInteger count) // offerAssetID.Concat(wantAssetID)
        {
            var result = new Offer[50]; // TODO: dynamic initialization doesn't work?

            byte[] keySize = Empty;
            var prefixLength = tradingPair.Length;
            if (prefixLength == 40) keySize = new byte[] { 0x48 }; // tradingPair size (prefix 40/52/64) + offerHash size (32)
            else if (prefixLength == 52) keySize = new byte[] { 0x54 };
            else if (prefixLength == 64) keySize = new byte[] { 0x60 };

            // TODO: allow iteration until start offerHash or offset?
            var i = 0;
            var it = Storage.Find(Context(), keySize.Concat(tradingPair));
            while (it.Next() && i < count && i < 50)
            {
                var value = it.Value;
                var bytes = Runtime.Deserialize(value);
                var offer = (Offer)bytes;
                result[i] = offer;
                i++;
            }

            return result;
        }

        private static bool MakeOffer(Offer offer)
        {
            // Check that transaction is signed by the maker
            if (!Runtime.CheckWitness(offer.MakerAddress)) return false;

            // Check that nonce is not repeated
            var tradingPair = TradingPair(offer);
            var offerHash = Hash(offer);
            if (Storage.Get(Context(), tradingPair.Concat(offerHash)) != Empty) return false;

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
            StoreOffer(tradingPair, offerHash, offer);

            // Notify clients
            Created(offer.MakerAddress, offerHash, offer.OfferAssetID, offer.OfferAmount, offer.WantAssetID, offer.WantAmount);
            return true;
        }

        private static bool FillOffer(byte[] fillerAddress, byte[] tradingPair, byte[] offerHash, BigInteger amountToFill, bool useNativeTokens)
        {
            // Check that transaction is signed by the filler
            if (!Runtime.CheckWitness(fillerAddress)) return false;

            // Check that the offer still exists 
            Offer offer = GetOffer(tradingPair, offerHash);
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
            if (amountToOffer < 1 || amountToFill < 1)
            {
                // Notify clients of failure
                Failed(fillerAddress, offerHash);
                return true;
            }

            // Reduce available balance for the filled asset and amount
            if (!ReduceBalance(fillerAddress, offer.WantAssetID, amountToFill)) return false;

            // Calculate offered amount and fees
            byte[] feeAddress = Storage.Get(Context(), "feeAddress");
            BigInteger makerFeeRate = GetMakerFee();
            BigInteger takerFeeRate = GetTakerFee();
            BigInteger makerFee = (amountToFill * makerFeeRate) / feeFactor;
            BigInteger takerFee = (amountToOffer * takerFeeRate) / feeFactor;
            BigInteger nativeFee = 0;

            // Move asset to the taker balance and notify clients
            if (useNativeTokens)
            {
                // Use previous trading period's exchange rate
                var bucketNumber = CurrentBucket() - 1;

                // Derive rate from volumes traded
                var nativeVolume = Storage.Get(Context(), NativeVolumeKey(offer.OfferAssetID, bucketNumber)).AsBigInteger();
                var otherVolume = Storage.Get(Context(), ForeignVolumeKey(offer.OfferAssetID, bucketNumber)).AsBigInteger();

                // Use native fee, if we can get an exchange rate
                if (otherVolume > 0)
                {
                    nativeFee = (takerFee * nativeVolume) / (otherVolume * nativeTokenDiscount);
                }

                // Reduce balance immediately from taker
                if (!ReduceBalance(fillerAddress, NativeToken, nativeFee))
                {
                    // Reset to 0 if balance is insufficient
                    nativeFee = 0;
                }
            }

            // Move asset to the taker balance and notify clients
            var takerAmount = amountToOffer - (nativeFee > 0 ? 0 : takerFee);
            TransferAssetTo(fillerAddress, offer.OfferAssetID, takerAmount);
            Transferred(fillerAddress, offer.OfferAssetID, takerAmount);

            // Move asset to the maker balance and notify clients
            var makerAmount = amountToFill - makerFee;
            TransferAssetTo(offer.MakerAddress, offer.WantAssetID, makerAmount);
            Transferred(offer.MakerAddress, offer.WantAssetID, makerAmount);

            // Move fees
            TransferAssetTo(feeAddress, offer.WantAssetID, makerFee);
            if (nativeFee == 0) TransferAssetTo(feeAddress, offer.OfferAssetID, takerFee);

            // Update native token exchange rate
            if (offer.OfferAssetID == NativeToken)
            {
                // Adding volume to the current trading period
                var bucketNumber = CurrentBucket();

                // Increase native token total by amountToOffer
                var nativeKey = NativeVolumeKey(offer.WantAssetID, bucketNumber);
                var nativeVolume = Storage.Get(Context(), nativeKey).AsBigInteger();
                Storage.Put(Context(), nativeKey, nativeVolume + amountToOffer);

                // Increase other token total by amountToFill                
                var otherKey = ForeignVolumeKey(offer.WantAssetID, bucketNumber);
                var otherVolume = Storage.Get(Context(), otherKey).AsBigInteger();
                Storage.Put(Context(), nativeKey, otherVolume + amountToFill);
            }
            if (offer.WantAssetID == NativeToken)
            {
                // Adding volume to the current trading period
                var bucketNumber = CurrentBucket();

                // Increase native token total by amountToOffer
                var nativeKey = NativeVolumeKey(offer.OfferAssetID, bucketNumber);
                var nativeVolume = Storage.Get(Context(), nativeKey).AsBigInteger();
                Storage.Put(Context(), nativeKey, nativeVolume + amountToFill);

                // Increase other token total by amountToFill                
                var otherKey = ForeignVolumeKey(offer.OfferAssetID, bucketNumber);
                var otherVolume = Storage.Get(Context(), otherKey).AsBigInteger();
                Storage.Put(Context(), nativeKey, otherVolume + amountToOffer);
            }

            // Update available amount
            offer.AvailableAmount = offer.AvailableAmount - amountToOffer;

            // Store updated offer
            StoreOffer(tradingPair, offerHash, offer);

            // Notify clients
            Filled(fillerAddress, offerHash, amountToFill, offer.OfferAssetID, offer.OfferAmount, offer.WantAssetID, offer.WantAmount);
            return true;
        }

        private static bool CancelOffer(byte[] tradingPair, byte[] offerHash)
        {
            // Check that the offer exists
            Offer offer = GetOffer(tradingPair, offerHash);
            if (offer.MakerAddress == Empty) return false;

            // Check that transaction is signed by the canceller
            if (!Runtime.CheckWitness(offer.MakerAddress)) return false;

            // Move funds to withdrawal address
            TransferAssetTo(offer.MakerAddress, offer.OfferAssetID, offer.AvailableAmount);

            // Remove offer
            RemoveOffer(tradingPair, offerHash);

            // Notify runtime
            Cancelled(offer.MakerAddress, offerHash);
            return true;
        }
                        
        private static bool SetFees(BigInteger takerFee, BigInteger makerFee)
        {
            if (takerFee > maxFee || makerFee > maxFee) return false;
            if (takerFee < 0 || makerFee < 0) return false;

            Storage.Put(Context(), "takerFee", takerFee);
            Storage.Put(Context(), "makerFee", makerFee);

            return true;
        }

        private static bool SetFeeAddress(byte[] feeAddress)
        {
            if (feeAddress.Length != 20) return false;
            Storage.Put(Context(), "feeAddress", feeAddress);

            return true;
        }

        private static object ProcessWithdrawal()
        {
            Runtime.Log("withdrawing");
            var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
            var withdrawalStage = WithdrawalStage(currentTxn);
            if (withdrawalStage == Empty) return false;

            var withdrawingAddr = GetWithdrawalAddress(currentTxn, withdrawalStage);
            var assetID = GetWithdrawalAsset(currentTxn);
            var isWithdrawingNEP5 = assetID.Length == 20;
            var inputs = currentTxn.GetInputs();
            var outputs = currentTxn.GetOutputs();

            if (withdrawalStage == Mark)
            {
                var amount = GetBalance(withdrawingAddr, assetID);
                MarkWithdrawal(withdrawingAddr, assetID, amount);
                if (isWithdrawingNEP5)
                {
                    Storage.Put(Context(), currentTxn.Hash.Concat(IndexAsByteArray(0)), withdrawingAddr);
                }
                else
                {
                    ulong sum = 0;
                    for (ushort index = 0; index < outputs.Length; index++)
                    {
                        sum += (ulong)outputs[index].Value;
                        Runtime.Log("Output check..");
                        if (sum <= amount)
                        {
                            Runtime.Log("Reserving...");
                            Storage.Put(Context(), currentTxn.Hash.Concat(IndexAsByteArray(index)), withdrawingAddr);
                        }
                    }
                }
                Withdrawing(withdrawingAddr, assetID, amount);
                return true;
            }
            else if (withdrawalStage == Withdraw)
            {
                foreach (var i in inputs)
                {
                    Storage.Delete(Context(), i.PrevHash.Concat(IndexAsByteArray(i.PrevIndex)));
                }
                var amount = GetWithdrawAmount(withdrawingAddr, assetID);
                if (isWithdrawingNEP5 && !WithdrawNEP5(withdrawingAddr, assetID, amount)) return false; // TODO: if nep-5 withdraw fails for some reason funds can get stuck?
                Withdrawn(withdrawingAddr, assetID, amount);
                return true;
            }

            return false;
        }

        private static bool VerifyWithdrawal(byte[] holderAddress, byte[] assetID)
        {
            var balance = GetBalance(holderAddress, assetID);
            if (balance <= 0) return false;

            var withdrawingAmount = GetWithdrawAmount(holderAddress, assetID);
            if (withdrawingAmount > 0) return false;

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
                var consumedAmount = Storage.Get(Context(), currentTxn.Hash.Concat(assetID)).AsBigInteger();

                // Check that the sent amount is still sufficient
                if (sentAmount - consumedAmount < amount) {
                    Runtime.Log("Not enough of asset sent");
                    return false;
                }

                // Update the consumed amount for this txn
                Storage.Put(Context(), currentTxn.Hash.Concat(assetID), consumedAmount + amount);

                // TODO: how to cleanup?
                return true;
            }
            else if (assetID.Length == 20)
            {
                // Just transfer immediately or fail as this is the last step in verification
                if (!VerifyContract(assetID)) return false;
                var args = new object[] { originator, ExecutionEngine.ExecutingScriptHash, amount };
                var Contract = (NEP5Contract)assetID.ToDelegate();
                var transferSuccessful = (bool)Contract("transfer", args);
                return transferSuccessful;
            }

            // Unknown asset category
            return false;
        }

        private static bool VerifyContract(byte[] assetID)
        {
            if (Storage.Get(Context(), "stateContractWhitelist") == Inactive) return true;
            return Storage.Get(Context(), WhitelistKey(assetID)).Length > 0;
        }

        private static Offer GetOffer(byte[] tradingPair, byte[] hash)
        {
            byte[] offerData = Storage.Get(Context(), tradingPair.Concat(hash));
            if (offerData.Length == 0) return new Offer();

            Runtime.Log("Deserializing offer");
            return (Offer)Runtime.Deserialize(offerData);
        }

        private static void StoreOffer(byte[] tradingPair, byte[] offerHash, Offer offer)
        {
            // Remove offer if completely filled
            if (offer.AvailableAmount == 0)
            {
                RemoveOffer(tradingPair, offerHash);
            }
            // Store offer otherwise
            else
            {
                // Serialize offer
                Runtime.Log("Serializing offer");
                var offerData = Runtime.Serialize(offer);
                Runtime.Log("Done serializing offer");
                Runtime.Notify("offerData", offerData);
                Storage.Put(Context(), tradingPair.Concat(offerHash), offerData);
            }
        }

        private static void RemoveOffer(byte[] tradingPair, byte[] offerHash)
        {
            // Delete offer data
            Storage.Delete(Context(), tradingPair.Concat(offerHash));
        }

        private static void TransferAssetTo(byte[] originator, byte[] assetID, BigInteger amount)
        {
            if (amount < 1)
            {
                Runtime.Log("Amount to transfer is less than 1!");
                return;
            }

            byte[] key = BalanceKey(originator, assetID);
            BigInteger currentBalance = Storage.Get(Context(), key).AsBigInteger();
            Storage.Put(Context(), key, currentBalance + amount);
        }

        private static bool ReduceBalance(byte[] address, byte[] assetID, BigInteger amount)
        {
            if (amount < 1)
            {
                Runtime.Log("Amount to reduce is less than 1!");
                return false;
            }

            var key = BalanceKey(address, assetID);
            var currentBalance = Storage.Get(Context(), key).AsBigInteger();
            var newBalance = currentBalance - amount;

            if (newBalance < 0)
            {
                Runtime.Log("Not enough balance!");
                return false;
            }

            if (newBalance > 0) Storage.Put(Context(), key, newBalance);
            else Storage.Delete(Context(), key);

            return true;
        }

        private static bool MarkWithdrawal(byte[] address, byte[] assetID, BigInteger amount)
        {
            Runtime.Log("Checking Last Mark..");
            if (!VerifyWithdrawal(address, assetID)) return false;

            Runtime.Log("Marking Withdrawal..");
            var balance = GetBalance(address, assetID);            
            Storage.Delete(Context(), BalanceKey(address, assetID));
            Storage.Put(Context(), WithdrawKey(address, assetID), balance);

            return true;
        }

        private static bool WithdrawNEP5(byte[] address, byte[] assetID, BigInteger amount)
        {
            // Transfer token
            if (!VerifyContract(assetID)) return false;
            var args = new object[] { ExecutionEngine.ExecutingScriptHash, address, amount };
            var contract = (NEP5Contract)assetID.ToDelegate();
            bool transferSuccessful = (bool)contract("transfer", args);
            if (!transferSuccessful)
            {
                Runtime.Log("Failed to transfer NEP-5 tokens!");
                return false;
            }

            return true;
        }

        private static byte[] GetWithdrawalAddress(Transaction transaction, byte[] withdrawalStage)
        {
            var usage = withdrawalStage == Mark ? TAUsage_AdditionalWitness : TAUsage_WithdrawalAddress;
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == usage) return attr.Data.Take(20);
            }
            return Empty;
        }

        private static byte[] GetWithdrawalAsset(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == TAUsage_NEP5AssetID) return attr.Data.Take(20);
                if (attr.Usage == TAUsage_SystemAssetID) return attr.Data;
            }
            return Empty;
        }

        private static byte[] WithdrawalStage(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == TAUsage_WithdrawalStage) return attr.Data.Take(1);
            }
            return Empty;
        }

        private static BigInteger AmountToOffer(Offer o, BigInteger amount)
        {
            return (o.OfferAmount * amount) / o.WantAmount;
        }

        private static byte[] Hash(Offer o)
        {
            var bytes = o.MakerAddress
                .Concat(TradingPair(o))
                .Concat(o.OfferAmount.AsByteArray())
                .Concat(o.WantAmount.AsByteArray())
                .Concat(o.Nonce);

            return Hash256(bytes);
        }

        // Helpers
        private static StorageContext Context() => Storage.CurrentContext;
        private static BigInteger CurrentBucket() => Runtime.Time / bucketDuration;
        private static byte[] IndexAsByteArray(ushort index) => index > 0 ? ((BigInteger)index).AsByteArray() : Empty;
        private static byte[] TradingPair(Offer o) => o.OfferAssetID.Concat(o.WantAssetID);

        // Keys
        private static byte[] WhitelistKey(byte[] assetID) => "contractWhitelist".AsByteArray().Concat(assetID);
        private static byte[] BalanceKey(byte[] originator, byte[] assetID) => originator.Concat(assetID);
        private static byte[] WithdrawKey(byte[] originator, byte[] assetID) => originator.Concat(assetID).Concat(Withdraw);
        private static byte[] ForeignVolumeKey(byte[] assetID, BigInteger bucketNumber) => assetID.Concat(Foreign).Concat(bucketNumber.AsByteArray());
        private static byte[] NativeVolumeKey(byte[] assetID, BigInteger bucketNumber) => assetID.Concat(Native).Concat(bucketNumber.AsByteArray());
    }
}

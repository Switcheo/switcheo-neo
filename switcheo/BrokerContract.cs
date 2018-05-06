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
        public static event Action<byte[], byte[], byte[], BigInteger, byte[], BigInteger> EmitCreated; // (address, offerHash, offerAssetID, offerAmount, wantAssetID, wantAmount)

        [DisplayName("filled")]
        public static event Action<byte[], byte[], BigInteger, byte[], BigInteger, byte[], BigInteger, BigInteger> EmitFilled; // (address, offerHash, fillAmount, offerAssetID, offerAmount, wantAssetID, wantAmount, amountToTake)

        [DisplayName("fees")]
        public static event Action<byte[], byte[], byte[], BigInteger, byte[], BigInteger> EmitFeesSent; // (feeAddress, offerHash, offerAssetID, makerFee, wantAssetID, takerFee);

        [DisplayName("failed")]
        public static event Action<byte[], byte[], BigInteger, byte[], byte[]> EmitFailed; // (address, offerHash, amountToFill, useNativeTokens, reason)

        [DisplayName("cancelled")]
        public static event Action<byte[], byte[]> EmitCancelled; // (address, offerHash)

        [DisplayName("transferred")]
        public static event Action<byte[], byte[], BigInteger, byte[]> EmitTransferred; // (address, assetID, amount, reason)

        [DisplayName("withdrawing")]
        public static event Action<byte[], byte[], BigInteger> EmitWithdrawing; // (address, assetID, amount)

        [DisplayName("withdrawn")]
        public static event Action<byte[], byte[], BigInteger> EmitWithdrawn; // (address, assetID, amount)

        [DisplayName("volumeAdded")]
        public static event Action<byte[], BigInteger, BigInteger> EmitVolumeAdded; // (assetID, nativeAmount, foreignAmount)

        [DisplayName("tradingFrozen")]
        public static event Action EmitTradingFrozen;

        [DisplayName("tradingResumed")]
        public static event Action EmitTradingResumed;

        [DisplayName("addedToWhitelist")]
        public static event Action<byte[]> EmitAddedTowWhitelist; // (scriptHash)

        [DisplayName("whitelistDestroyed")]
        public static event Action EmitWhitelistDestroyed;

        // Broker Settings & Hardcaps
        private static readonly byte[] Owner = "Ae6LkR5TLXVVAE5WSRqAEDEYBx6ChBE6Am".ToScriptHash();
        private static readonly byte[] NativeToken = "AbwJtGDCcwoH2HhDmDq12ZcqFmUpCU3XMp".ToScriptHash();
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
        private static readonly byte[] NeoAssetID = { 155, 124, 255, 218, 166, 116, 190, 174, 15, 147, 14, 190, 96, 133, 175, 144, 147, 229, 254, 86, 179, 74, 92, 34, 12, 205, 207, 110, 252, 51, 111, 197 };
        private static readonly byte[] GasAssetID = { 231, 45, 40, 105, 121, 238, 108, 177, 183, 230, 93, 253, 223, 178, 227, 132, 16, 11, 141, 20, 142, 119, 88, 222, 66, 228, 22, 139, 113, 121, 44, 96 };
        private static readonly byte[] WithdrawArgs = { 0x00, 0xc1, 0x08, 0x77, 0x69, 0x74, 0x68, 0x64, 0x72, 0x61, 0x77 }; // PUSH0, PACK, PUSHBYTES8, "withdraw" as bytes
        private static readonly byte[] DepositArgs = { 0x00, 0xc1, 0x00 }; // PUSH0, PACK, PUSHBYTES0

        // Reason Code for balance changes
        private static readonly byte[] ReasonDeposit = { 0x01 }; // Balance increased due to deposit
        private static readonly byte[] ReasonMake = { 0x02 }; // Balance reduced due to maker making
        private static readonly byte[] ReasonTakerFill = { 0x03 }; // Balance reduced due to taker filling maker's offered asset
        private static readonly byte[] ReasonTakerFee = { 0x04 }; // Balance reduced due to taker fees
        private static readonly byte[] ReasonMakerFee = { 0x05 }; // Balance reduced due to maker fees
        private static readonly byte[] ReasonTakerReceive = { 0x06 }; // Balance increased due to taker receiving his cut in the trade
        private static readonly byte[] ReasonMakerReceive = { 0x07 }; // Balance increased due to maker receiving his cut in the trade
        private static readonly byte[] ReasonContractMakerFee = { 0x08 }; // Balance increased on fee address due to contract receiving maker fee
        private static readonly byte[] ReasonContractTakerFee = { 0x09 }; // Balance increased on fee address due to contract receiving taker fee
        private static readonly byte[] ReasonCancel = { 0x0a }; // Balance increased due to cancelling offer
        private static readonly byte[] ReasonPrepareWithdrawal = { 0x0b }; // Balance reduced due to preparing for asset withdrawal

        // Reason Code for fill failures
        private static readonly byte[] ReasonEmptyOffer = { 0x21 }; // Empty Offer when trying to fill
        private static readonly byte[] ReasonTakingLessThanOne = { 0x22 }; // Taking less than 1 asset when trying to fill

        // True or false
        private static readonly byte[] True = { 0x01 };
        private static readonly byte[] False = { 0x00 };

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

        private struct Volume
        {
            public BigInteger Native;
            public BigInteger Foreign;
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
            if (Runtime.Trigger == TriggerType.VerificationR)
            {
                if (operation == "receiving") return Receiving();
                return false;
            }
            else if (Runtime.Trigger == TriggerType.ApplicationR)
            {
                return operation == "received" ? Received() : Empty;
            }
            else if (Runtime.Trigger == TriggerType.Verification)
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

                    // Check that NEP5 withdrawals don't reserve more utxos than required
                    if (isWithdrawingNEP5)
                    {
                        if (inputs.Length > 1) return false;
                        if (outputs[0].Value > 1) return false;
                    }

                    // Check that inputs are not wasted (prevent DOS on withdrawals)
                    if (outputs.Length - inputs.Length > 1) return false;
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

                // Check that Application trigger will be tail called with the correct params
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
                if (operation == "getMakerFee") return GetMakerFee(args.Length == 1 ? (byte[])args[0] : Empty);
                if (operation == "getTakerFee") return GetTakerFee(args.Length == 1 ? (byte[])args[0] : Empty);
                if (operation == "getExchangeRate") return GetExchangeRate((byte[])args[0]);
                if (operation == "getOffers") return GetOffers((byte[])args[0], (byte[])args[1]);
                if (operation == "getOffer") return GetOffer((byte[])args[0], (byte[])args[1]);
                if (operation == "getBalance") return GetBalance((byte[])args[0], (byte[])args[1]);

                // == Execute == 
                if (operation == "deposit") // NEP-5 ONLY
                {
                    if (GetState() != Active) return false;
                    if (args.Length != 3) return false;
                    if (!Deposit((byte[])args[0], (byte[])args[1], (BigInteger)args[2])) return false;
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
                    EmitTradingFrozen();
                    return true;
                }
                if (operation == "unfreezeTrading")
                {
                    Storage.Put(Context(), "state", Active);
                    EmitTradingResumed();
                    return true;
                }
                if (operation == "setMakerFee")
                {
                    if (args.Length != 2) return false;
                    return SetMakerFee((BigInteger)args[0], (byte[])args[1]);
                }
                if (operation == "setTakerFee")
                {
                    if (args.Length != 2) return false;
                    return SetTakerFee((BigInteger)args[0], (byte[])args[1]);
                }
                if (operation == "resetTakerFee")
                {
                    if (args.Length != 2) return false;
                    return ResetTakerFee((byte[])args[0]);
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
                    byte[] scriptHash = (byte[])args[0];
                    Storage.Put(Context(), WhitelistKey(scriptHash), "1");
                    EmitAddedTowWhitelist(scriptHash);
                }
                if (operation == "destroyWhitelist")
                {
                    Storage.Put(Context(), "stateContractWhitelist", Inactive);
                    EmitWhitelistDestroyed();
                }
            }

            return true;
        }

        // Called by VerificationR
        public static bool Receiving() {
            var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;

            // Always pass if receiving any system assets from this contract (Currently this is assumed to only happen in system asset withdrawals)
            if (IsReceivingFromSelf(currentTxn)) return true;

            // If there are no withdrawals: Do additional checks if depositing: Check that Application trigger will be tail called with the correct params
            var invocationTransaction = (InvocationTransaction)currentTxn;
            // Make sure it is an invocation
            if (currentTxn.Type != Type_InvocationTransaction) return false;
            // Make sure there is a deposit call with no arguments
            if (invocationTransaction.Script != DepositArgs.Concat(OpCode_TailCall).Concat(ExecutionEngine.ExecutingScriptHash)) return false;
            return true;
        }

        // Called by ApplicationR
        public static byte[] Received()
        {
            // Check the current transaction for the system assets
            var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
            var outputs = currentTxn.GetOutputs();

            // if there is input from the contract it is a Withdrawal and we won't deposit anything
            if (IsReceivingFromSelf(currentTxn)) return Empty;

            // Only deposit those assets not from contract
            ulong sentGasAmount = 0;
            ulong sentNeoAmount = 0;

            foreach (var o in outputs)
            {
                if (o.ScriptHash == ExecutionEngine.ExecutingScriptHash)
                {
                    if (o.AssetId == GasAssetID)
                    {
                        sentGasAmount += (ulong)o.Value;
                    }
                    else if (o.AssetId == NeoAssetID)
                    {
                        sentNeoAmount += (ulong)o.Value;
                    }
                }
            }
            byte[] firstAvailableAddress = currentTxn.GetReferences()[0].ScriptHash;
            if (sentGasAmount > 0) IncreaseBalance(firstAvailableAddress, GasAssetID, sentGasAmount, ReasonDeposit);
            if (sentNeoAmount > 0) IncreaseBalance(firstAvailableAddress, NeoAssetID, sentNeoAmount, ReasonDeposit);

            return Empty;
        }

        private static bool Initialize(BigInteger takerFee, BigInteger makerFee, byte[] feeAddress)
        {
            if (GetState() != Pending) return false;
            if (!SetMakerFee(makerFee, Empty)) return false;
            if (!SetTakerFee(takerFee, Empty)) return false;
            if (!SetFeeAddress(feeAddress)) return false;

            Storage.Put(Context(), "state", Active);

            Runtime.Log("Contract initialized");
            return true;
        }

        private static byte[] GetState()
        {
            return Storage.Get(Context(), "state");
        }

        private static BigInteger GetMakerFee(byte[] assetID)
        {
            var fee = Storage.Get(Context(), "makerFee".AsByteArray().Concat(assetID));
            if (fee.Length != 0 || assetID.Length == 0) return fee.AsBigInteger();

            return Storage.Get(Context(), "makerFee").AsBigInteger();
        }

        private static BigInteger GetTakerFee(byte[] assetID)
        {
            var fee = Storage.Get(Context(), "takerFee".AsByteArray().Concat(assetID));
            if (fee.Length != 0 || assetID.Length == 0) return fee.AsBigInteger();

            return Storage.Get(Context(), "takerFee").AsBigInteger();
        }

        private static BigInteger GetBalance(byte[] originator, byte[] assetID)
        {
            return Storage.Get(Context(), BalanceKey(originator, assetID)).AsBigInteger();
        }

        private static BigInteger GetWithdrawAmount(byte[] originator, byte[] assetID)
        {
            return Storage.Get(Context(), WithdrawKey(originator, assetID)).AsBigInteger();
        }

        private static Volume GetExchangeRate(byte[] assetID) // against native token
        {
            var bucketNumber = CurrentBucket();
            return GetVolume(bucketNumber, assetID);
        }

        private static Offer[] GetOffers(byte[] tradingPair, byte[] offset) // offerAssetID.Concat(wantAssetID)
        {
            var result = new Offer[50];

            var it = Storage.Find(Context(), tradingPair);

            while (it.Next())
            {
                if (it.Value == offset) break;
            }

            var i = 0;
            while (it.Next() && i < 50)
            {
                var value = it.Value;
                var bytes = value.Deserialize();
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
            if (!ReduceBalance(offer.MakerAddress, offer.OfferAssetID, offer.OfferAmount, ReasonMake)) return false;

            // Add the offer to storage
            StoreOffer(tradingPair, offerHash, offer);

            // Notify clients
            EmitCreated(offer.MakerAddress, offerHash, offer.OfferAssetID, offer.OfferAmount, offer.WantAssetID, offer.WantAmount);
            return true;
        }

        // Fills an offer by filling the amount the offerer wants
        // => amountToFill's asset type = offer's wantAssetID
        private static bool FillOffer(byte[] fillerAddress, byte[] tradingPair, byte[] offerHash, BigInteger amountToFill, bool useNativeTokens)
        {
            // Check that transaction is signed by the filler
            if (!Runtime.CheckWitness(fillerAddress)) return false;

            // Check that the offer still exists 
            Offer offer = GetOffer(tradingPair, offerHash);
            if (offer.MakerAddress == Empty)
            {
                // Notify clients of failure
                EmitFailed(fillerAddress, offerHash, amountToFill, useNativeTokens ? True : False, ReasonEmptyOffer);
                return true;
            }

            // Check that the filler is different from the maker
            if (fillerAddress == offer.MakerAddress) return false;


            // Calculate the max amount that can be offered & filled based on current available amount
            BigInteger amountToTake = (offer.OfferAmount * amountToFill) / offer.WantAmount; // amountToTake's asset type = offerAssetID (taker is taking what is offered)
            if (amountToTake > offer.AvailableAmount)
            {
                amountToTake = offer.AvailableAmount;
                amountToFill = (amountToTake * offer.WantAmount) / offer.OfferAmount;
            }
            // Check that the amount that will be taken is at least 1
            if (amountToTake <= 0)
            {
                // Notify clients of failure
                EmitFailed(fillerAddress, offerHash, amountToFill, useNativeTokens ? True : False, ReasonTakingLessThanOne);
                return true;
            }

            // Reduce available balance for the filler's asset
            if (amountToFill > 0 && !ReduceBalance(fillerAddress, offer.WantAssetID, amountToFill, ReasonTakerFill)) return false;

            // Calculate offered amount and fees
            byte[] feeAddress = Storage.Get(Context(), "feeAddress");
            BigInteger makerFeeRate = GetMakerFee(offer.WantAssetID);
            BigInteger takerFeeRate = GetTakerFee(offer.OfferAssetID);
            BigInteger makerFee = (amountToFill * makerFeeRate) / feeFactor;
            BigInteger takerFee = (amountToTake * takerFeeRate) / feeFactor;
            BigInteger nativeFee = 0;

            // Calculate native fees (SWH)
            if (offer.OfferAssetID == NativeToken) {
                nativeFee = takerFee / nativeTokenDiscount;
            }
            else if (useNativeTokens)
            {
                Runtime.Log("Using Native Fees...");

                // Use current trading period's exchange rate
                var bucketNumber = CurrentBucket();
                Volume volume = GetVolume(bucketNumber, offer.OfferAssetID);

                // Derive rate from volumes traded
                var nativeVolume = volume.Native;
                var foreignVolume = volume.Foreign;

                // Use native fee, if we can get an exchange rate
                if (foreignVolume > 0)
                {
                    nativeFee = (takerFee * nativeVolume) / (foreignVolume * nativeTokenDiscount);
                }
                // Reduce balance immediately from taker
                if (!ReduceBalance(fillerAddress, NativeToken, nativeFee, ReasonTakerFee))
                {
                    // Reset to 0 if balance is insufficient
                    nativeFee = 0;
                }
            }

            // Move asset to the taker balance and notify clients
            var takerAmount = amountToTake - (nativeFee > 0 ? 0 : takerFee);
            IncreaseBalance(fillerAddress, offer.OfferAssetID, takerAmount, ReasonTakerReceive);

            // Move asset to the maker balance and notify clients
            var makerAmount = amountToFill - makerFee;
            IncreaseBalance(offer.MakerAddress, offer.WantAssetID, makerAmount, ReasonMakerReceive);

            // Move fees
            if (makerFee > 0) IncreaseBalance(feeAddress, offer.WantAssetID, makerFee, ReasonContractMakerFee);
            if (nativeFee == 0) IncreaseBalance(feeAddress, offer.OfferAssetID, takerFee, ReasonContractTakerFee);

            // Update native token volumes that will be used as exchange rate
            if (offer.OfferAssetID == NativeToken)
            {
                AddVolume(offer.WantAssetID, amountToTake, amountToFill);
            }
            if (offer.WantAssetID == NativeToken)
            {
                AddVolume(offer.OfferAssetID, amountToFill, amountToTake);
            }

            // Update available amount
            offer.AvailableAmount = offer.AvailableAmount - amountToTake;

            // Store updated offer
            StoreOffer(tradingPair, offerHash, offer);

            // Notify clients
            EmitFilled(fillerAddress, offerHash, amountToFill, offer.OfferAssetID, offer.OfferAmount, offer.WantAssetID, offer.WantAmount, amountToTake);
            EmitFeesSent(feeAddress, offerHash, offer.OfferAssetID, makerFee, offer.WantAssetID, takerFee);
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
            IncreaseBalance(offer.MakerAddress, offer.OfferAssetID, offer.AvailableAmount, ReasonCancel);

            // Remove offer
            RemoveOffer(tradingPair, offerHash);

            // Notify runtime
            EmitCancelled(offer.MakerAddress, offerHash);
            return true;
        }
                        
        private static bool SetMakerFee(BigInteger fee, byte[] assetID)
        {
            if (fee > maxFee) return false;
            if (fee < 0) return false;

            Storage.Put(Context(), "makerFee".AsByteArray().Concat(assetID), fee);

            return true;
        }

        private static bool SetTakerFee(BigInteger fee, byte[] assetID)
        {
            if (fee > maxFee) return false;
            if (fee < 0) return false;

            Storage.Put(Context(), "takerFee".AsByteArray().Concat(assetID), fee);

            return true;
        }

        private static bool ResetTakerFee(byte[] assetID)
        {
            Storage.Delete(Context(), "takerFee".AsByteArray().Concat(assetID));

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

                if (isWithdrawingNEP5)
                {
                    if (amount > 0) Storage.Put(Context(), currentTxn.Hash.Concat(IndexAsByteArray(0)), withdrawingAddr);
                }
                else
                {
                    if (assetID == NeoAssetID)
                    {
                        // neo must be rounded down
                        const ulong neoAssetFactor = 100000000;
                        amount = amount / neoAssetFactor * neoAssetFactor;
                    }
                    ulong sum = 0;
                    for (ushort index = 0; index < outputs.Length; index++)
                    {
                        var value = (ulong)outputs[index].Value;
                        if (sum + value > amount) break;

                        Storage.Put(Context(), currentTxn.Hash.Concat(IndexAsByteArray(index)), withdrawingAddr);
                        sum += value;
                    }
                    amount = sum;
                }

                return MarkWithdrawal(withdrawingAddr, assetID, amount);
            }
            else if (withdrawalStage == Withdraw)
            {
                foreach (var i in inputs)
                {
                    Storage.Delete(Context(), i.PrevHash.Concat(IndexAsByteArray(i.PrevIndex)));
                }

                var amount = GetWithdrawAmount(withdrawingAddr, assetID);
                if (isWithdrawingNEP5 && !WithdrawNEP5(withdrawingAddr, assetID, amount)) return false;

                Storage.Delete(Context(), WithdrawKey(withdrawingAddr, assetID));
                EmitWithdrawn(withdrawingAddr, assetID, amount);
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

        private static bool Deposit(byte[] originator, byte[] assetID, BigInteger amount)
        {
            // Check whitelist
            if (!VerifyContract(assetID)) return false;

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

                // Check that the sent amount is correct
                if (sentAmount != amount)
                {
                    return false;
                }

                // Check that there is no double deposit
                var alreadyVerified = Storage.Get(Context(), currentTxn.Hash.Concat(assetID)).Length > 0;
                if (alreadyVerified) return false;

                // Update the consumed amount for this txn
                Storage.Put(Context(), currentTxn.Hash.Concat(assetID), 1);

                // TODO: how to cleanup?
                return true;
            }
            else if (assetID.Length == 20)
            {
                // Just transfer immediately
                var args = new object[] { originator, ExecutionEngine.ExecutingScriptHash, amount };
                var Contract = (NEP5Contract)assetID.ToDelegate();
                var transferSuccessful = (bool)Contract("transfer", args);
                if (transferSuccessful) IncreaseBalance(originator, assetID, amount, ReasonDeposit);
                return transferSuccessful;
            }
            // Unknown asset category
            return false;
        }

        // Checks if there are any system assets received from this contract in this transaction
        private static bool IsReceivingFromSelf(Transaction transaction)
        {
            var inputs = transaction.GetReferences();
            foreach (var i in inputs)
            {
                if (i.ScriptHash == ExecutionEngine.ExecutingScriptHash) return true;
            }
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
            return (Offer)offerData.Deserialize();
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
                var offerData = offer.Serialize();
                Storage.Put(Context(), tradingPair.Concat(offerHash), offerData);
            }
        }

        private static void RemoveOffer(byte[] tradingPair, byte[] offerHash)
        {
            // Delete offer data
            Storage.Delete(Context(), tradingPair.Concat(offerHash));
        }

        private static void IncreaseBalance(byte[] originator, byte[] assetID, BigInteger amount, byte[] reason)
        {
            if (amount < 1) return;

            byte[] key = BalanceKey(originator, assetID);
            BigInteger currentBalance = Storage.Get(Context(), key).AsBigInteger();
            Storage.Put(Context(), key, currentBalance + amount);
            EmitTransferred(originator, assetID, amount, reason);
        }

        private static bool ReduceBalance(byte[] address, byte[] assetID, BigInteger amount, byte[] reason)
        {
            if (amount < 1) return false;

            var key = BalanceKey(address, assetID);
            var currentBalance = Storage.Get(Context(), key).AsBigInteger();
            var newBalance = currentBalance - amount;

            if (newBalance < 0) return false;

            if (newBalance > 0) Storage.Put(Context(), key, newBalance);
            else Storage.Delete(Context(), key);
            // TODO: emit event in v2 EmitBalanceChanged(address, assetID, amount, reason);

            return true;
        }

        private static bool MarkWithdrawal(byte[] address, byte[] assetID, BigInteger amount)
        {
            if (amount < 1) return false;
            if (!VerifyWithdrawal(address, assetID)) return false;

            if (!ReduceBalance(address, assetID, amount, ReasonPrepareWithdrawal)) return false;
            Storage.Put(Context(), WithdrawKey(address, assetID), amount);

            EmitWithdrawing(address, assetID, amount);

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

        // Add volume to the current reference assetID e.g. NEO/SWH: Add nativeAmount to SWH volume and foreignAmount to NEO volume
        private static bool AddVolume(byte[] assetID, BigInteger nativeAmount, BigInteger foreignAmount) 
        {
            // Retrieve all volumes from current 24 hr bucket
            var bucketNumber = CurrentBucket();
            var volumeKey = VolumeKey(bucketNumber, assetID);
            byte[] volumeData = Storage.Get(Context(), volumeKey);

            Volume volume;

            // Either create a new record or add to existing volume
            if (volumeData.Length == 0)
            {
                volume = new Volume
                {
                    Native = nativeAmount,
                    Foreign = foreignAmount
                };
            }
            else
            {
                volume = (Volume)volumeData.Deserialize();
                volume.Native = volume.Native + nativeAmount;
                volume.Foreign = volume.Foreign + foreignAmount;
            }

            // Save to blockchain
            Storage.Put(Context(), volumeKey, volume.Serialize());
            EmitVolumeAdded(assetID, nativeAmount, foreignAmount);
            Runtime.Log("Done serializing and storing");

            return true;
        }

        // Retrieves the native and foreign volume of a reference assetID in the current 24 hr bucket
        private static Volume GetVolume(BigInteger bucketNumber, byte[] assetID)
        {
            byte[] volumeData = Storage.Get(Context(), VolumeKey(bucketNumber, assetID));
            if (volumeData.Length == 0)
            {
                return new Volume();
            }
            else {
                return (Volume)volumeData.Deserialize();
            }
        }

        // Helpers
        private static StorageContext Context() => Storage.CurrentContext;
        private static BigInteger CurrentBucket() => Runtime.Time / bucketDuration;
        private static byte[] IndexAsByteArray(ushort index) => index > 0 ? ((BigInteger)index).AsByteArray() : Empty;
        private static byte[] TradingPair(Offer o) => o.OfferAssetID.Concat(o.WantAssetID);

        // Keys
        private static byte[] BalanceKey(byte[] originator, byte[] assetID) => originator.Concat(assetID);
        private static byte[] WithdrawKey(byte[] originator, byte[] assetID) => originator.Concat(assetID).Concat(Withdraw);
        private static byte[] WhitelistKey(byte[] assetID) => "contractWhitelist".AsByteArray().Concat(assetID);
        private static byte[] VolumeKey(BigInteger bucketNumber, byte[] assetID) => "tradeVolume".AsByteArray().Concat(bucketNumber.AsByteArray()).Concat(assetID);
    }
}

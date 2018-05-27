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

        [DisplayName("failed")]
        public static event Action<byte[], byte[], BigInteger, byte[], BigInteger, byte[]> EmitFailed; // (address, offerHash, amountToTake, takerFeeAsssetID, takerFee, reason)

        [DisplayName("cancelAnnounced")]
        public static event Action<byte[], byte[]> EmitCancelAnnounced; // (address, offerHash)

        [DisplayName("cancelled")]
        public static event Action<byte[], byte[]> EmitCancelled; // (address, offerHash)

        [DisplayName("transferred")]
        public static event Action<byte[], byte[], BigInteger, byte[]> EmitTransferred; // (address, assetID, amount, reason)

        [DisplayName("deposited")]
        public static event Action<byte[], byte[], BigInteger> EmitDeposited; // (address, assetID, amount)

        [DisplayName("withdrawAnnounced")]
        public static event Action<byte[], byte[], BigInteger> EmitWithdrawAnnounced; // (address, assetID, amount)

        [DisplayName("withdrawing")]
        public static event Action<byte[], byte[], BigInteger> EmitWithdrawing; // (address, assetID, amount)

        [DisplayName("withdrawn")]
        public static event Action<byte[], byte[], BigInteger> EmitWithdrawn; // (address, assetID, amount)

        [DisplayName("tradingFrozen")]
        public static event Action EmitTradingFrozen;

        [DisplayName("tradingResumed")]
        public static event Action EmitTradingResumed;

        [DisplayName("addedToWhitelist")]
        public static event Action<byte[]> EmitAddedTowWhitelist; // (scriptHash)

        [DisplayName("removedFromWhitelist")]
        public static event Action<byte[]> EmitRemovedFromWhitelist; // (scriptHash)

        [DisplayName("nep8ActiveSet")]
        public static event Action EmitNEP8ActiveSet;

        [DisplayName("coordinatorSet")]
        public static event Action<byte[]> EmitCoordinatorSet; // (address)

        [DisplayName("initialized")]
        public static event Action<byte[], byte[], BigInteger> Initialized;

        // Broker Settings & Hardcaps
        private static readonly byte[] Owner = "Ae6LkR5TLXVVAE5WSRqAEDEYBx6ChBE6Am".ToScriptHash();
        private static readonly ulong maxAnnounceDelay = 60 * 60 * 24 * 7; // 7 days

        // Contract States
        private static readonly byte[] Pending = { };         // only can initialize
        private static readonly byte[] Active = { 0x01 };     // all operations active
        private static readonly byte[] Inactive = { 0x02 };   // trading halted - only can do cancel, withdrawal & owner actions

        // Withdrawal Flags
        private static readonly byte[] Mark = { 0x50 };
        private static readonly byte[] Withdraw = { 0x51 };
        private static readonly byte[] OpCode_TailCall = { 0x69 };
        private static readonly byte Type_InvocationTransaction = 0xd1;
        private static readonly byte TAUsage_WithdrawalStage = 0xa1;
        private static readonly byte TAUsage_NEP5AssetID = 0xa2;
        private static readonly byte TAUsage_SystemAssetID = 0xa3;
        private static readonly byte TAUsage_WithdrawalAddress = 0xa4;
        private static readonly byte TAUsage_WithdrawalAmount = 0xa5;

        // Byte Constants
        private static readonly byte[] Empty = { };
        private static readonly byte[] NeoAssetID = { 155, 124, 255, 218, 166, 116, 190, 174, 15, 147, 14, 190, 96, 133, 175, 144, 147, 229, 254, 86, 179, 74, 92, 34, 12, 205, 207, 110, 252, 51, 111, 197 };
        private static readonly byte[] GasAssetID = { 231, 45, 40, 105, 121, 238, 108, 177, 183, 230, 93, 253, 223, 178, 227, 132, 16, 11, 141, 20, 142, 119, 88, 222, 66, 228, 22, 139, 113, 121, 44, 96 };
        private static readonly byte[] WithdrawArgs = { 0x00, 0xc1, 0x08, 0x77, 0x69, 0x74, 0x68, 0x64, 0x72, 0x61, 0x77 }; // PUSH0, PACK, PUSHBYTES8, "withdraw" as bytes

        // Reason Code for balance changes
        private static readonly byte[] ReasonDeposit = { 0x01 }; // Balance increased due to deposit
        private static readonly byte[] ReasonMake = { 0x02 }; // Balance reduced due to maker making
        private static readonly byte[] ReasonTake = { 0x03 }; // Balance reduced due to taker filling maker's offered asset
        private static readonly byte[] ReasonTakerFee = { 0x04 }; // Balance reduced due to taker fees
        private static readonly byte[] ReasonTakerReceive = { 0x05 }; // Balance increased due to taker receiving his cut in the trade
        private static readonly byte[] ReasonMakerReceive = { 0x06 }; // Balance increased due to maker receiving his cut in the trade
        private static readonly byte[] ReasonContractTakerFee = { 0x07 }; // Balance increased on fee address due to contract receiving taker fee
        private static readonly byte[] ReasonCancel = { 0x08 }; // Balance increased due to cancelling offer
        private static readonly byte[] ReasonPrepareWithdrawal = { 0x09 }; // Balance reduced due to preparing for asset withdrawal

        // Reason Code for fill failures
        private static readonly byte[] ReasonOfferNotExist = { 0x21 }; // Empty Offer when trying to fill
        private static readonly byte[] ReasonTakingLessThanOne = { 0x22 }; // Taking less than 1 asset when trying to fill
        private static readonly byte[] ReasonFillerSameAsMaker = { 0x23 }; // Filler same as maker
        private static readonly byte[] ReasonTakingMoreThanAvailable = { 0x24 }; // Taking more than available in the offer at the moment
        private static readonly byte[] ReasonFillingLessThanOne = { 0x25 }; // Filling less than 1 asset when trying to fill
        private static readonly byte[] ReasonNotEnoughBalanceOnFiller = { 0x26 }; // Not enough balance to give (wantAssetID) for what you want to take (offerAssetID)
        private static readonly byte[] ReasonNotEnoughBalanceOnNativeToken = { 0x27 }; // Not enough balance in native tokens to use
        private static readonly byte[] ReasonFeesMoreThanLimit = { 0x28 }; // Fees exceed 0.5%

        private struct Offer
        {
            public byte[] MakerAddress;
            public byte[] OfferAssetID;
            public BigInteger OfferAmount;
            public byte[] WantAssetID;
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
            return new Offer
            {
                MakerAddress = makerAddress.Take(20),
                OfferAssetID = offerAssetID,
                OfferAmount = offerAmount.AsBigInteger(),
                WantAssetID = wantAssetID,
                WantAmount = wantAmount.AsBigInteger(),
                AvailableAmount = availableAmount.AsBigInteger(),
                Nonce = nonce,
            };
        }

        private struct WithdrawInfo
        {
            public BigInteger TimeStamp;
            public BigInteger Amount;
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
                if (operation == "received") return Received();
                return false;
            }
            else if (Runtime.Trigger == TriggerType.Verification)
            {
                if (GetState() == Pending) return false;

                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                var withdrawalStage = GetWithdrawalStage(currentTxn);
                var withdrawingAddr = GetWithdrawalAddress(currentTxn);
                var assetID = GetWithdrawalAsset(currentTxn);
                var isWithdrawingNEP5 = assetID.Length == 20;
                var inputs = currentTxn.GetInputs();
                var outputs = currentTxn.GetOutputs();
                var references = currentTxn.GetReferences();

                ulong totalOut = 0;
                if (withdrawalStage == Mark)
                {
                    // Check that inputs are not already reserved (We must not re-use a utxo that is already reserved)
                    foreach (var i in inputs)
                    {
                        if (Storage.Get(Context(), WithdrawalKey(i.PrevHash)).Length > 0) return false;
                    }

                    // Check that withdrawal is possible (Enough balance and nothing reserved for withdrawing. Only 1 withdraw can happen at a time)
                    var amount = isWithdrawingNEP5 ? GetWithdrawalAmount(currentTxn) : totalOut;
                    if (!VerifyWithdrawal(withdrawingAddr, assetID, amount)) return false;

                    // Check that the transaction is signed by the coordinator or pre-announced + signed by the user
                    if (!Runtime.CheckWitness(GetCoordinatorAddress()))
                    {
                        if (!Runtime.CheckWitness(withdrawingAddr)) return false;
                        if (!IsWithdrawalAnnounced(withdrawingAddr, assetID, amount)) return false;
                    }

                    // Check inputs and outputs
                    if (isWithdrawingNEP5)
                    {
                        // Check that NEP5 withdrawals don't use contract assets
                        foreach (var i in references)
                        {
                            if (i.ScriptHash == ExecutionEngine.ExecutingScriptHash) return false;
                        }
                    }
                    else
                    {
                        // Check that outputs are a valid self-send for system asset withdrawals
                        // (In marking phase, all assets from contract should be sent to contract and nothing should go anywhere else)
                        foreach (var o in outputs)
                        {
                            totalOut += (ulong)o.Value;
                            if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash) return false;
                            if (o.AssetId != assetID) return false;
                        }
                    }

                    // Check that inputs are not wasted (prevent denial-of-service by using additional inputs)
                    if (inputs.Length > 2) return false;
                }
                else if (withdrawalStage == Withdraw)
                {
                    // Check that utxo has been reserved
                    if (inputs.Length > 1) return false;
                    if (Storage.Get(Context(), WithdrawalKey(inputs[0].PrevHash)) != withdrawingAddr) return false;

                    if (isWithdrawingNEP5)
                    {
                        // Check that NEP5 withdrawals don't use contract assets
                        foreach (var i in references)
                        {
                            if (i.ScriptHash == ExecutionEngine.ExecutingScriptHash) return false;
                        }
                        // Check for whitelist if we are doing old style NEP-5 transfers
                        // New style NEP-5 transfers SHOULD NOT require contract verification / witness
                        if (!VerifyContract(assetID)) return false;
                    }
                    else
                    {
                        // Check withdrawal destination and amount
                        if (outputs.Length > 1) return false;
                        if (outputs[0].AssetId != assetID) return false;
                        if (outputs[0].ScriptHash != withdrawingAddr) return false;
                    }
                }
                else
                {
                    return false;
                }

                // Check that Application trigger will be tail called with the correct params
                if (currentTxn.Type != Type_InvocationTransaction) return false;
                var invocationTransaction = (InvocationTransaction)currentTxn;
                if (invocationTransaction.Script != WithdrawArgs.Concat(OpCode_TailCall).Concat(ExecutionEngine.ExecutingScriptHash)) return false;

                // Ensure that nothing is burnt
                ulong totalIn = 0;
                foreach (var i in references) totalIn += (ulong)i.Value;
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
                    if (args.Length != 2) return false;
                    return Initialize((byte[])args[0], (byte[])args[1]);
                }

                // == Getters ==
                if (operation == "getState") return GetState();
                if (operation == "getOffers") return GetOffers((byte[])args[0], (byte[])args[1]);
                if (operation == "getOffer") return GetOffer((byte[])args[0], (byte[])args[1]);
                if (operation == "getBalance") return GetBalance((byte[])args[0], (byte[])args[1]);
                if (operation == "getCoordinatorAddress") return GetCoordinatorAddress();
                if (operation == "getAnnounceDelay") return GetAnnounceDelay();

                // == Execute == 
                if (operation == "deposit") // NEP-5 ONLY + backwards compatibility before nep-7
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
                    if (args.Length != 6) return false;
                    return FillOffer((byte[])args[0], (byte[])args[1], (byte[])args[2], (BigInteger)args[3], (byte[])args[4], (BigInteger)args[5]);
                }
                if (operation == "cancelOffer")
                {
                    if (GetState() == Pending) return false;
                    if (args.Length != 2) return false;
                    return CancelOffer((byte[])args[0], (byte[])args[1]);
                }
                if (operation == "withdraw")
                {
                    if (GetState() == Pending) return false;
                    return ProcessWithdrawal(args);
                }
                if (operation == "announceCancel")
                {
                    if (GetState() == Pending) return false;
                    if (args.Length != 2) return false;
                    return AnnounceCancel((byte[])args[0], (byte[])args[1]);
                }
                if (operation == "announceWithdraw")
                {
                    if (GetState() == Pending) return false;
                    if (args.Length != 3) return false;
                    return AnnounceWithdraw((byte[])args[0], (byte[])args[1], (BigInteger)args[0]);
                }

                // == Owner ==
                if (!Runtime.CheckWitness(Owner))
                {
                    Runtime.Log("Owner signature verification failed");
                    return false;
                }
                if (operation == "freezeTrading")
                {
                    if (!Runtime.CheckWitness(GetCoordinatorAddress())) return false; // ensure both coordinator and owner signed
                    Storage.Put(Context(), "state", Inactive);
                    EmitTradingFrozen();
                    return true;
                }
                if (operation == "unfreezeTrading")
                {
                    if (!Runtime.CheckWitness(GetCoordinatorAddress())) return false; // ensure both coordinator and owner signed
                    Storage.Put(Context(), "state", Active);
                    EmitTradingResumed();
                    return true;
                }
                if (operation == "setAnnounceDelay")
                {
                    if (args.Length != 1) return false;
                    return SetAnnounceDelay((BigInteger)args[0]); ;
                }
                if (operation == "setCoordinatorAddress")
                {
                    if (args.Length != 1) return false;
                    return SetCoordinatorAddress((byte[])args[0]); ;
                }
                if (operation == "setFeeAddress")
                {
                    if (args.Length != 1) return false;
                    return SetFeeAddress((byte[])args[0]);
                }
                if (operation == "addToWhitelist")
                {
                    if (args.Length != 1) return false;
                    return AddToWhitelist((byte[]) args[0]);
                }
                if (operation == "removeFromWhitelist")
                {
                    if (args.Length != 1) return false;
                    return RemoveFromWhitelist((byte[])args[0]);
                }
                if (operation == "setNEP8Active")
                {
                    Storage.Put(Context(), "nep8status", Active);
                    EmitNEP8ActiveSet();
                    return true;
                }
            }

            return true;
        }

        /***********
         * Getters *
         ***********/

        private static byte[] GetState()
        {
            return Storage.Get(Context(), "state");
        }

        private static BigInteger GetBalance(byte[] originator, byte[] assetID)
        {
            return Storage.Get(Context(), BalanceKey(originator, assetID)).AsBigInteger();
        }

        private static BigInteger GetAnnounceDelay()
        {
            return Storage.Get(Context(), "announceDelay").AsBigInteger();
        }

        private static byte[] GetCoordinatorAddress()
        {
            return Storage.Get(Context(), "coordinatorAddress");
        }

        private static Offer GetOffer(byte[] tradingPair, byte[] hash)
        {
            byte[] offerData = Storage.Get(Context(), tradingPair.Concat(hash));
            if (offerData.Length == 0) return new Offer();

            return (Offer)offerData.Deserialize();
        }

        private static Offer[] GetOffers(byte[] tradingPair, byte[] offset) // tradingPair ==> offerAssetID.Concat(wantAssetID)
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

        /***********
         * Control *
         ***********/

        private static bool Initialize(byte[] feeAddress, byte[] coordinatorAddress)
        {
            if (GetState() != Pending) return false;

            if (!SetFeeAddress(feeAddress)) throw new Exception("Failed to set fee address");
            if (!SetCoordinatorAddress(coordinatorAddress)) throw new Exception("Failed to set coordinator");
            if (!SetAnnounceDelay(maxAnnounceDelay)) throw new Exception("Failed to announcement delay");

            Storage.Put(Context(), "state", Active);
            Initialized(feeAddress, coordinatorAddress, maxAnnounceDelay);

            return true;
        }

        private static bool SetFeeAddress(byte[] feeAddress)
        {
            if (feeAddress.Length != 20) return false;
            Storage.Put(Context(), "feeAddress", feeAddress);

            return true;
        }

        private static bool SetAnnounceDelay(BigInteger delay)
        {
            if (delay < 0 || delay > maxAnnounceDelay) return false;
            Storage.Put(Context(), "announceDelay", delay);

            return true;
        }

        private static bool SetCoordinatorAddress(byte[] coordinatorAddress)
        {
            if (coordinatorAddress.Length != 20) return false;
            Storage.Put(Context(), "coordinatorAddress", coordinatorAddress);
            EmitCoordinatorSet(coordinatorAddress);
            return true;
        }

        private static bool AddToWhitelist(byte[] scriptHash)
        {
            if (scriptHash.Length != 20) return false;
            Storage.Put(Context(), WhitelistKey(scriptHash), "1");
            EmitAddedTowWhitelist(scriptHash);
            return true;
        }

        private static bool RemoveFromWhitelist(byte[] scriptHash)
        {
            if (scriptHash.Length != 20) return false;
            Storage.Delete(Context(), WhitelistKey(scriptHash));
            EmitRemovedFromWhitelist(scriptHash);
            return true;
        }

        /***********
         * Trading *
         ***********/

        private static bool MakeOffer(Offer offer)
        {
            // Check that transaction is signed by the maker
            if (!Runtime.CheckWitness(offer.MakerAddress)) return false;

            // Check that transaction is signed by the coordinator
            if (!Runtime.CheckWitness(GetCoordinatorAddress())) return false;

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

        // Fills an offer by taking the amount you want
        // => amountToFill's asset type = offer's wantAssetID
        // amountToTake's asset type = offerAssetID (taker is taking what is offered)
        private static bool FillOffer(byte[] fillerAddress, byte[] tradingPair, byte[] offerHash, BigInteger amountToTake, byte[] takerFeeAssetID, BigInteger takerFeeAmount)
        {
            // Note: We do all checks first then execute state changes

            // Check that transaction is signed by the filler
            if (!Runtime.CheckWitness(fillerAddress)) return true;

            // Check that transaction is signed by the coordinator
            if (!Runtime.CheckWitness(GetCoordinatorAddress())) return false;

            // Check that the offer still exists 
            Offer offer = GetOffer(tradingPair, offerHash);
            if (offer.MakerAddress == Empty)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonOfferNotExist);
                return false;
            }

            // Check that the filler is different from the maker
            if (fillerAddress == offer.MakerAddress)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonFillerSameAsMaker);
                return false;
            }

            // Check that the amount that will be taken is at least 1
            if (amountToTake < 1)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonTakingLessThanOne);
                return false;
            }

            // Check that you cannot take more than available
            if (amountToTake > offer.AvailableAmount)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonTakingMoreThanAvailable);
                return false;
            }

            // Calculate amount we have to give the offerer (what the offerer wants)
            BigInteger amountToFill = (amountToTake * offer.WantAmount) / offer.OfferAmount;

            // Check that amount to fill(give) is not less than 1
            if (amountToFill < 1)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonFillingLessThanOne);
                return false;
            }

            // Check that there is enough balance to reduce for filler
            var fillerBalance = GetBalance(fillerAddress, offer.WantAssetID);
            if (fillerBalance < amountToFill)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonNotEnoughBalanceOnFiller);
                return false;
            }

            // Check the fee type
            bool useNativeTokens = takerFeeAssetID != offer.OfferAssetID;

            // Check that there is enough balance in native fees if using native fees
            if (useNativeTokens && GetBalance(fillerAddress, takerFeeAssetID) < takerFeeAmount)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonNotEnoughBalanceOnNativeToken);
                return false;
            }

            // Check that the amountToTake is not more than 0.5% if not using native fees
            if (!useNativeTokens && ((takerFeeAmount * 1000) / amountToTake > 5))
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonFeesMoreThanLimit);
                return false;
            }

            // Reduce balance from filler
            ReduceBalance(fillerAddress, offer.WantAssetID, amountToFill, ReasonTake);

            // Move filled asset to the maker balance
            IncreaseBalance(offer.MakerAddress, offer.WantAssetID, amountToFill, ReasonMakerReceive);

            // Move taken asset to the taker balance
            var amountToTakeAfterFees = useNativeTokens ? amountToTake : amountToTake - takerFeeAmount;
            IncreaseBalance(fillerAddress, offer.OfferAssetID, amountToTakeAfterFees, ReasonTakerReceive);

            // Move fees
            byte[] feeAddress = Storage.Get(Context(), "feeAddress");
            if (takerFeeAmount > 0)
            {
                if (useNativeTokens)
                {
                    ReduceBalance(fillerAddress, takerFeeAssetID, takerFeeAmount, ReasonTakerFee);
                } else
                {
                    IncreaseBalance(feeAddress, takerFeeAssetID, takerFeeAmount, ReasonContractTakerFee);
                }
            }

            // Update available amount
            offer.AvailableAmount = offer.AvailableAmount - amountToTake;

            // Store updated offer
            StoreOffer(tradingPair, offerHash, offer);

            // Notify clients
            EmitFilled(fillerAddress, offerHash, amountToFill, offer.OfferAssetID, offer.OfferAmount, offer.WantAssetID, offer.WantAmount, amountToTake);

            return true;
        }

        private static bool CancelOffer(byte[] tradingPair, byte[] offerHash)
        {
            // Check that the offer exists
            Offer offer = GetOffer(tradingPair, offerHash);
            if (offer.MakerAddress == Empty) return false;

            // Check that the transaction is signed by the coordinator or pre-announced
            var cancellationAnnounced = IsCancellationAnnounced(offerHash);
            if (!Runtime.CheckWitness(GetCoordinatorAddress()) && !cancellationAnnounced) return false;

            // Check that transaction is signed by the canceller or trading is frozen 
            if (!Runtime.CheckWitness(offer.MakerAddress) && !IsTradingFrozen()) return false;

            // Move funds to maker address
            IncreaseBalance(offer.MakerAddress, offer.OfferAssetID, offer.AvailableAmount, ReasonCancel);

            // Remove offer
            RemoveOffer(tradingPair, offerHash);

            // Clean up announcement
            if (cancellationAnnounced)
            {
                Storage.Delete(Context(), CancelAnnounceKey(offerHash));
            }

            // Notify runtime
            EmitCancelled(offer.MakerAddress, offerHash);
            return true;
        }

        private static bool AnnounceCancel(byte[] tradingPair, byte[] offerHash)
        {
            // Check that the offer exists
            Offer offer = GetOffer(tradingPair, offerHash);
            if (offer.MakerAddress == Empty) return false;

            // Check that transaction is signed by the canceller or trading is frozen 
            if (!Runtime.CheckWitness(offer.MakerAddress)) return false;

            Storage.Put(Context(), CancelAnnounceKey(offerHash), Runtime.Time);
            
            // Announce cancel intent to coordinator
            EmitCancelAnnounced(offer.MakerAddress, offerHash);

            return true;
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
            EmitTransferred(address, assetID, 0 - amount, reason);

            return true;
        }

        /***********
         * Deposit *
         ***********/

        private static bool Deposit(byte[] originator, byte[] assetID, BigInteger amount)
        {
            // Verify that the offer really has the indicated assets available
            if (assetID.Length == 32)
            {
                // Accept all system assets
                var received = Received();

                // Mark deposit
                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                Storage.Put(Context(), DepositKey(currentTxn), 1);
                if (received) EmitDeposited(originator, assetID, amount);
                return received;
            }
            else if (assetID.Length == 20)
            {
                // Check whitelist
                if (!VerifyContract(assetID) && !IsNEP8Active()) return false;

                // Just transfer immediately
                var args = new object[] { originator, ExecutionEngine.ExecutingScriptHash, amount };
                var Contract = (NEP5Contract)assetID.ToDelegate();
                var transferSuccessful = (bool)Contract("transfer", args);
                if (transferSuccessful)
                {
                    IncreaseBalance(originator, assetID, amount, ReasonDeposit);
                    EmitDeposited(originator, assetID, amount);
                }

                return transferSuccessful;
            }

            // Unknown asset category
            return false;
        }

        /********************
         * Receive Triggers *
         ********************/

        // Receiving system asset directly
        public static bool Receiving()
        {
            var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;

            // Always pass immediately if this is step 1 of withdrawal which requires self-sending
            if (GetWithdrawalStage(currentTxn) == Mark) return true;

            return true;
        }

        // Received system asset
        public static bool Received()
        {
            // Check the current transaction for the system assets
            var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
            var outputs = currentTxn.GetOutputs();

            // Check for double deposits
            if (Storage.Get(Context(), DepositKey(currentTxn)).Length > 1) return false;

            // Don't deposit if this is withdrawal stage 1 (self-send)
            if (GetWithdrawalStage(currentTxn) == Mark) return false;

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

            return true;
        }

        /**************
         * Withdrawal *
         **************/

        private static bool VerifyWithdrawal(byte[] holderAddress, byte[] assetID, BigInteger amount)
        {
            if (holderAddress.Length != 20) return false;
            if (assetID.Length != 20 && assetID.Length != 32) return false;
            if (amount < 1) return false;

            var balance = GetBalance(holderAddress, assetID);
            if (balance < amount) return false;

            var withdrawingAmount = GetWithdrawAmount(holderAddress, assetID);
            if (withdrawingAmount > 0) return false;

            return true;
        }

        private static bool AnnounceWithdraw(byte[] originator, byte[] assetID, BigInteger amountToWithdraw)
        {
            if (!Runtime.CheckWitness(originator)) return false;

            if (!VerifyWithdrawal(originator, assetID, amountToWithdraw)) return false;

            WithdrawInfo withdrawInfo = new WithdrawInfo
            {
                TimeStamp = Runtime.Time,
                Amount = amountToWithdraw
            };

            var key = WithdrawAnnounceKey(originator, assetID);
            Storage.Put(Context(), key, withdrawInfo.Serialize());

            // Announce withdrawal intent to clients
            EmitWithdrawAnnounced(originator, assetID, amountToWithdraw);

            return true;
        }

        private static object ProcessWithdrawal(object[] args)
        {
            var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
            var withdrawalStage = GetWithdrawalStage(currentTxn);
            if (withdrawalStage == Empty) return false;

            var withdrawingAddr = GetWithdrawalAddress(currentTxn);
            if (withdrawingAddr == Empty) return false;

            var assetID = GetWithdrawalAsset(currentTxn);
            if (assetID == Empty) return false;

            var isWithdrawingNEP5 = assetID.Length == 20;
            var inputs = currentTxn.GetInputs();
            var outputs = currentTxn.GetOutputs();
            
            if (withdrawalStage == Mark)
            {
                if (!Runtime.CheckWitness(ExecutionEngine.ExecutingScriptHash)) return false;
                BigInteger amount = isWithdrawingNEP5 ? GetWithdrawalAmount(currentTxn) : outputs[0].Value;
                return MarkWithdrawal(currentTxn.Hash, withdrawingAddr, assetID, amount);
            }
            else if (withdrawalStage == Withdraw)
            {
                var key = WithdrawalKey(inputs[0].PrevHash);
                if (Storage.Get(Context(), key) != withdrawingAddr) return false;

                Storage.Delete(Context(), key);

                var amount = GetWithdrawAmount(withdrawingAddr, assetID);

                if (isWithdrawingNEP5)
                {
                    if (!VerifyContract(assetID))
                    {
                        // New-style non-whitelisted NEP-5 transfers MUST NOT pass contract witness checks
                        if (Runtime.CheckWitness(ExecutionEngine.ExecutingScriptHash)) return false;
                        if (!IsNEP8Active()) return false;
                    }
                    if (!WithdrawNEP5(withdrawingAddr, assetID, amount))
                    {
                        Runtime.Log("Tried to withdraw NEP-5 but failed!");
                        return false;
                    }
                }

                Storage.Delete(Context(), WithdrawKey(withdrawingAddr, assetID));
                EmitWithdrawn(withdrawingAddr, assetID, amount);
                return true;
            }

            return false;
        }

        private static bool MarkWithdrawal(byte[] transactionHash, byte[] address, byte[] assetID, BigInteger amount)
        {
            bool withdrawalAnnounced = IsWithdrawalAnnounced(address, assetID, amount);

            if (!Runtime.CheckWitness(GetCoordinatorAddress()) && !withdrawalAnnounced)
            {                
                Runtime.Log("Coordinator witness missing or withdrawal unannounced");
                return false;
            }

            if (amount < 1)
            {
                Runtime.Log("Amount to mark withdrawal is less than 1");
                return false;
            }

            if (!VerifyWithdrawal(address, assetID, amount))
            {
                Runtime.Log("Verify withdrawal failed");
                return false;
            }

            if (!ReduceBalance(address, assetID, amount, ReasonPrepareWithdrawal))
            {
                Runtime.Log("Reduce balance for withdrawal failed");
                return false;
            }

            if (withdrawalAnnounced)
            {
                Storage.Delete(Context(), WithdrawAnnounceKey(address, assetID));
            }

            Storage.Put(Context(), WithdrawalKey(transactionHash), address);
            Storage.Put(Context(), WithdrawKey(address, assetID), amount);
            EmitWithdrawing(address, assetID, amount);

            return true;
        }

        private static bool WithdrawNEP5(byte[] address, byte[] assetID, BigInteger amount)
        {
            // Transfer token
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

        private static BigInteger GetWithdrawAmount(byte[] originator, byte[] assetID)
        {
            return Storage.Get(Context(), WithdrawKey(originator, assetID)).AsBigInteger();
        }

        private static byte[] GetWithdrawalAddress(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == TAUsage_WithdrawalAddress) return attr.Data.Take(20);
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

        private static BigInteger GetWithdrawalAmount(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == TAUsage_WithdrawalAmount) return attr.Data.AsBigInteger();
            }
            return 0;
        }

        private static byte[] GetWithdrawalStage(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == TAUsage_WithdrawalStage) return attr.Data.Take(1);
            }
            return Empty;
        }

        // Helpers
        private static StorageContext Context() => Storage.CurrentContext;
        private static BigInteger AmountToOffer(Offer o, BigInteger amount) => (o.OfferAmount * amount) / o.WantAmount;
        private static byte[] TradingPair(Offer o) => o.OfferAssetID.Concat(o.WantAssetID); // to be used as a prefix only
        private static bool IsTradingFrozen() => Storage.Get(Context(), "state") == Inactive;
        private static bool IsNEP8Active() => Storage.Get(Context(), "nep8status") == Active;

        private static bool IsWithdrawalAnnounced(byte[] withdrawingAddr, byte[] assetID, BigInteger amount)
        {
            var announce = Storage.Get(Context(), WithdrawAnnounceKey(withdrawingAddr, assetID));
            if (announce.Length == 0) return false; // not announced
            var announceInfo = (WithdrawInfo)announce.Deserialize();
            var announceDelay = GetAnnounceDelay();

            return announceInfo.TimeStamp + announceDelay > Runtime.Time && announceInfo.Amount == amount;
        }

        private static bool IsCancellationAnnounced(byte[] offerHash)
        {
            var announceTime = Storage.Get(Context(), CancelAnnounceKey(offerHash));
            if (announceTime.Length == 0) return false; // not announced
            var announceDelay = GetAnnounceDelay();

            return announceTime.AsBigInteger() + announceDelay > Runtime.Time;
        }

        private static bool VerifyContract(byte[] assetID)
        {
            if (assetID.AsBigInteger() == 0) return false;
            return Storage.Get(Context(), WhitelistKey(assetID)).Length > 0;
        }

        // Unique hash for an offer
        private static byte[] Hash(Offer o)
        {
            var bytes = o.MakerAddress
                .Concat(TradingPair(o))
                .Concat(o.OfferAmount.AsByteArray())
                .Concat(o.WantAmount.AsByteArray())
                .Concat(o.Nonce);

            return Hash256(bytes);
        }

        // Keys
        private static byte[] BalanceKey(byte[] originator, byte[] assetID) => "balance".AsByteArray().Concat(originator).Concat(assetID);
        private static byte[] WithdrawalKey(byte[] transactionHash) => "withdrawalUTXO".AsByteArray().Concat(transactionHash);
        private static byte[] WithdrawKey(byte[] originator, byte[] assetID) => "withdrawing".AsByteArray().Concat(originator).Concat(assetID);
        private static byte[] WhitelistKey(byte[] assetID) => "contractWhitelist".AsByteArray().Concat(assetID);
        private static byte[] DepositKey(Transaction txn) => "deposited".AsByteArray().Concat(txn.Hash);
        private static byte[] CancelAnnounceKey(byte[] offerHash) => "cancelAnnounce".AsByteArray().Concat(offerHash);
        private static byte[] WithdrawAnnounceKey(byte[] originator, byte[] assetID) => "withdrawAnnounce".AsByteArray().Concat(originator).Concat(assetID);
    }
}

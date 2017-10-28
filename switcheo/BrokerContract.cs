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
        [Appcall("1c4f43f942b56ed906dba00b7f3c7ce3da3dd11077532baed900c2cc8c7f247e")] // TODO: Add RPX ScriptHash - or find workaround to call arbitrary contract
        public static extern object CallRPXContract(string method, params object[] args);

        [DisplayName("created")]
        public static event Action<byte[]> Created; // (offerHash)

        [DisplayName("filled")]
        public static event Action<byte[], BigInteger> Filled; // (offerHash, amount)

        [DisplayName("cancelled")]
        public static event Action<byte[]> Cancelled; // (offerHash)

        [DisplayName("transferred")]
        public static event Action<byte[], byte[], byte, BigInteger> Transferred; // (address, assetID, assetCategory, amount)

        [DisplayName("withdrawn")]
        public static event Action<byte[], byte[], byte, BigInteger> Withdrawn; // (address, assetID, assetCategory, amount)

        private static readonly byte[] Owner = { 2, 86, 121, 88, 238, 62, 78, 230, 177, 3, 68, 142, 10, 254, 31, 223, 139, 87, 150, 110, 30, 135, 156, 120, 59, 17, 101, 55, 236, 191, 90, 249, 113 };
        private const ulong feeFactor = 100000; // 1 => 0.001%
        private const int maxFee = 3000; // 3000/10000 = 0.3%

        // Contract States
        private static byte[] Pending = { };         // only can initialize
        private static byte[] Active = { 0x01 };     // all operations active
        private static byte[] Inactive = { 0x02 };   // trading halted - only can do cancel, withdrawl & owner actions

        // TODO: do we need an enum? we can just do `private static byte SystemAsset = 0x00` instead?
        private enum AssetCategory : byte
        {
            SystemAsset = 0x00,
            NEP5 = 0x01
        }

        private struct Offer
        {
            public byte[] MakerAddress;
            public byte[] OfferAssetID;
            public AssetCategory OfferAssetCategory;
            public BigInteger OfferAmount;
            public byte[] WantAssetID;
            public AssetCategory WantAssetCategory;
            public BigInteger WantAmount;
            public BigInteger AvailableAmount;
            public byte[] Nonce;
        }

        private static Offer NewOffer(
            byte[] makerAddress,
            byte[] offerAssetID, byte offerAssetCategory, byte[] offerAmount,
            byte[] wantAssetID, byte wantAssetCategory, byte[] wantAmount,
            byte[] nonce
        )
        {
            return new Offer
            {
                MakerAddress = makerAddress.Take(20),
                OfferAssetID = offerAssetID.Take(20),
                OfferAssetCategory = (AssetCategory)offerAssetCategory,
                OfferAmount = offerAmount.AsBigInteger(),
                WantAssetID = wantAssetID.Take(20),
                WantAssetCategory = (AssetCategory)wantAssetCategory,
                WantAmount = wantAmount.AsBigInteger(),
                AvailableAmount = wantAmount.AsBigInteger(),
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
            // == Init ==
            if (operation == "initialize")
            {
                if (args.Length != 4) return false;
                if (!Runtime.CheckWitness((byte[])args[0])) return false;
                return Initialize((byte[])args[0], (BigInteger)args[1], (BigInteger)args[2], (byte[])args[3]);
            }


            // == Query ==

            // Check that contract has been initialized
            if (Storage.Get(Storage.CurrentContext, "state") == Pending) return false;
            
            // TODO: do we need all these helper methods? client can query contract storage directly!
            if (operation == "getOffers")
            {
                if (args.Length != 4) return false;
                var key = ((byte[])args[0]).
                    Concat(new byte[] { (byte)args[1] }).
                    Concat((byte[])args[2]).
                    Concat(new byte[] { (byte)args[3] });
                return Storage.Get(Storage.CurrentContext, key);
            }
            if (operation == "getOffer")
            {
                if (args.Length != 1) return false;
                return Storage.Get(Storage.CurrentContext, (byte[]) args[0]);
            }
            if (operation == "tradingStatus")
                return Storage.Get(Storage.CurrentContext, "state");
            if (operation == "getMakerFee")
                return Storage.Get(Storage.CurrentContext, "makerFee");
            if (operation == "getTakerFee")
                return Storage.Get(Storage.CurrentContext, "takerFee");


            // == Execute ==

            // Check that the originator is honest
            if (Runtime.Trigger == TriggerType.Verification)
            {
                // TODO: is this right?
                if (!Runtime.CheckWitness((byte[])args[0])) return false;

                // What does this sample code mean? :
                //    if (Owner.Length == 20)
                //    {
                //        return Runtime.CheckWitness(Owner);
                //    }
                //    else if (Owner.Length == 33)
                //    {
                //        byte[] signature = operation.AsByteArray();
                //        return VerifySignature(signature, Owner);
                //    }
            }

            if (operation == "makeOffer")
            {
                if (Storage.Get(Storage.CurrentContext, "state") == Inactive) return false;
                if (args.Length != 7) return false;
                var offer = NewOffer((byte[])args[0], (byte[])args[1], (byte)args[2], (byte[])args[3], (byte[])args[4], (byte)args[5], (byte[])args[6], (byte[])args[7]);

                if (Runtime.Trigger == TriggerType.Verification) return VerifyOffer(offer);
                else if (Runtime.Trigger == TriggerType.Application) return MakeOffer(offer);
                return false;
            }
            if (operation == "fillOffer")
            {
                if (Storage.Get(Storage.CurrentContext, "state") == Inactive) return false;
                if (args.Length != 3) return false;

                if (Runtime.Trigger == TriggerType.Verification) return VerifyFill((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                else if (Runtime.Trigger == TriggerType.Application) return FillOffer((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                return false;
            }
            if (operation == "cancelOffer")
            {
                if (args.Length != 2) return false;
                return CancelOffer((byte[])args[0], (byte[])args[1]);
            }
            if (operation == "withdrawAssets")
            {
                if (args.Length != 5) return false;

                if (Runtime.Trigger == TriggerType.Verification) return VerifyWithdrawal((byte[])args[0], (byte[])args[1], (AssetCategory)args[2], (BigInteger)args[3], (byte[])args[4]);
                else if (Runtime.Trigger == TriggerType.Application) return WithdrawAssets((byte[])args[0], (byte[])args[1], (AssetCategory)args[2], (BigInteger)args[3], (byte[])args[4]);
                return false;
            }


            // == Owner ==

            if (Runtime.Trigger == TriggerType.Verification)
            {
                // Check that originator is the owner
                return (byte[])args[0] == Owner;
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
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
                    return SetFees((BigInteger)args[1], (BigInteger)args[2]);
                }
                if (operation == "setFeeAddress")
                {
                    if (args.Length != 1) return false;
                    return SetFeeAddress((byte[])args[1]);
                }
            }

            return false;
        }

        private static bool Initialize(byte[] originator, BigInteger takerFee, BigInteger makerFee, byte[] feeAddress)
        {
            if (originator != Owner) return false;
            if (Storage.Get(Storage.CurrentContext, "state") != Pending) return false;
            if (!SetFees(takerFee, makerFee)) return false;
            if (!SetFeeAddress(feeAddress)) return false;

            Storage.Put(Storage.CurrentContext, "state", Active);
            return true;
        }

        private static byte[] GetOffers(byte[] offerAssetID, byte[] offerAssetCategory, byte[] wantAssetID, byte[] wantAssetCategory)
        {
            return Storage.Get(Storage.CurrentContext, offerAssetID.Concat(offerAssetCategory).Concat(wantAssetID).Concat(wantAssetCategory));
        }

        private static bool VerifyOffer(Offer offer)
        {
            // Check that nonce is not repeated
            if (Storage.Get(Storage.CurrentContext, Hash(offer)).Length != 0) return false;

            // Check that the amounts > 0
            if (offer.OfferAmount <= 0 || offer.WantAmount <= 0) return false;

            // Check that the amounts < 2^(2^32)
            // TODO: optimize this check
            if (ToBytes(offer.OfferAmount).Length > 32 || ToBytes(offer.WantAmount).Length > 32) return false;

            // Check the trade is across different assets
            // TODO: should we bother checking this?
            if (offer.OfferAssetID == offer.WantAssetID && offer.OfferAssetCategory == offer.WantAssetCategory) return false;

            // Check that asset IDs are valid
            // TODO: do we need this as Take(20) has already been invoked?
            if (offer.OfferAssetID.Length != 20 || offer.WantAssetID.Length != 20) return false;

            // Verify that the offer txn has really has the indicated assets available
            return VerifySentAmount(offer.MakerAddress, offer.OfferAssetID, offer.OfferAssetCategory, offer.OfferAmount);
        }

        private static bool MakeOffer(Offer offer)
        {
            var tradingPair = TradingPair(offer);
            var offerHash = Hash(offer);

            // Transfer NEP-5 token if required
            if (offer.OfferAssetCategory == AssetCategory.NEP5)
            {
                // TODO: Do we need to prevent re-entrancy due to external call?
                bool transferSuccessful = (bool)CallRPXContract("transferFrom", ExecutionEngine.ExecutingScriptHash, offer.MakerAddress, ExecutionEngine.ExecutingScriptHash, offer.OfferAmount);
                if (!transferSuccessful) return false; // XXX: Getting here would be very bad.
            }

            // Store a mapping on the trading pair to the offer
            byte[] offers = Storage.Get(Storage.CurrentContext, tradingPair);
            Storage.Put(Storage.CurrentContext, tradingPair, offers.Concat(offerHash));

            // Store the maker address and filled amount under the offer hash
            Storage.Put(Storage.CurrentContext, offerHash, ToBuffer(offer));

            // Notify runtime
            Created(offerHash);
            return true;
        }

        private static bool VerifyFill(byte[] fillerAddress, byte[] offerHash, BigInteger amountToFill)
        {
            // Check that the offer exists 
            byte[] offerData = Storage.Get(Storage.CurrentContext, offerHash);
            if (offerData.Length == 0) return false;
            Offer offer = FromBuffer(offerData);

            // Check that the filler is different from the maker
            // TODO: can we omit this?
            if (fillerAddress == offer.MakerAddress) return false;

            // Check that amount to offer <= available amount
            BigInteger amountToOffer = AmountToOffer(offer, amountToFill);
            if (amountToOffer > offer.AvailableAmount) return false;

            // Verify that the filling txn really has the required assets available
            return VerifySentAmount(offer.MakerAddress, offer.OfferAssetID, offer.OfferAssetCategory, offer.OfferAmount);
        }

        private static bool FillOffer(byte[] fillerAddress, byte[] offerHash, BigInteger amountToFill)
        {
            // Get offer
            Offer offer = FromBuffer(Storage.Get(Storage.CurrentContext, offerHash));

            // Calculate offered amount and fees
            BigInteger amountToOffer = AmountToOffer(offer, amountToFill);
            BigInteger makerFeeRate = Storage.Get(Storage.CurrentContext, "makerFee").AsBigInteger();
            BigInteger takerFeeRate = Storage.Get(Storage.CurrentContext, "takerFee").AsBigInteger();
            BigInteger makerFee = (amountToOffer * makerFeeRate) / feeFactor;
            BigInteger takerFee = (amountToOffer * takerFeeRate) / feeFactor;

            // Move fees
            TransferAssetTo(Owner, offer.WantAssetID, offer.WantAssetCategory, makerFee);
            TransferAssetTo(Owner, offer.OfferAssetID, offer.OfferAssetCategory, takerFee);

            // Move asset to the maker balance
            TransferAssetTo(offer.MakerAddress, offer.WantAssetID, offer.WantAssetCategory, amountToFill - makerFee);
            Transferred(offer.MakerAddress, offer.WantAssetID, (byte) offer.WantAssetCategory, amountToFill - makerFee);

            // Move asset to the taker balance
            TransferAssetTo(fillerAddress, offer.OfferAssetID, offer.OfferAssetCategory, amountToOffer - takerFee);
            Transferred(fillerAddress, offer.OfferAssetID, (byte)offer.OfferAssetCategory, amountToOffer - takerFee);

            // Update available amount
            offer.AvailableAmount = offer.AvailableAmount - amountToFill;

            // Remove order if completely filled
            if (offer.AvailableAmount == 0)
            {
                var tradingPair = TradingPair(offer);
                RemoveOffer(tradingPair, offerHash);
            }
            // Store new available amount
            else
            {
                Storage.Put(Storage.CurrentContext, offerHash, ToBuffer(offer));
            }

            // Notify runtime
            //Filled(offerHash, amountToFill);
            return true;
        }

        private static bool CancelOffer(byte[] cancellerAddress, byte[] offerHash)
        {
            // Check that the offer exists
            byte[] offerData = Storage.Get(Storage.CurrentContext, offerHash);
            if (offerData.Length == 0) return false;
            Offer offer = FromBuffer(offerData);

            // Check that the canceller is also the offer maker
            if (offer.MakerAddress != cancellerAddress) return false;

            // Move funds to withdrawal address
            var storeKey = StoreKey(cancellerAddress, offer.OfferAssetID, offer.OfferAssetCategory);
            BigInteger balance = Storage.Get(Storage.CurrentContext, storeKey).AsBigInteger();
            Storage.Put(Storage.CurrentContext, storeKey, balance + offer.AvailableAmount);

            // Remove offer
            var tradingPair = TradingPair(offer);
            RemoveOffer(tradingPair, offerHash);

            // Notify runtime
            Cancelled(offerHash);
            return true;
        }

        private static bool VerifyWithdrawal(byte[] holderAddress, byte[] assetID, AssetCategory assetCategory, BigInteger amount, byte[] withdrawToThisAddress)
        {
            // Check that there are asset value > 0 in balance
            var key = StoreKey(holderAddress, assetID, assetCategory);
            var balance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            if (balance < amount) return false;

            // Check that the transaction outputs matches the specified amount
            if (assetCategory == AssetCategory.SystemAsset)
            {
                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                var outputs = currentTxn.GetReferences();
                BigInteger withdrawAmount = 0;
                foreach (var o in outputs)
                {
                    if (o.AssetId == assetID && o.ScriptHash == withdrawToThisAddress) withdrawAmount += o.Value;
                    else return false; // Only allow withdrawing the specified asset and destination
                }
                if (withdrawAmount != amount) return false;
            }

            return true;
        }

        private static bool WithdrawAssets(byte[] holderAddress, byte[] assetID, AssetCategory assetCategory, BigInteger amount, byte[] withdrawToThisAddress)
        {
            if (assetCategory == AssetCategory.NEP5)
            {
                // Transfer token
                bool transferSuccessful = (bool)CallRPXContract("transfer", ExecutionEngine.ExecutingScriptHash, withdrawToThisAddress, amount);
                if (!transferSuccessful) return false;
            }

            // Reduce balance
            var key = StoreKey(holderAddress, assetID, assetCategory);
            var currentBalance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            if (currentBalance - amount > 0 ) Storage.Put(Storage.CurrentContext, key, currentBalance - amount);
            else Storage.Delete(Storage.CurrentContext, key);

            // Notify runtime
            //Withdrawn(holderAddress, assetID, (byte)assetCategory, amount);
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

        private static bool VerifySentAmount(byte[] originator, byte[] assetID, AssetCategory assetCategory, BigInteger amount)
        {
            // Verify that the offer really has the indicated assets available
            if (assetCategory == AssetCategory.SystemAsset)
            {
                // Check the current transaction for the system assets
                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                var outputs = currentTxn.GetReferences();
                BigInteger sentAmount = 0;
                foreach (var o in outputs)
                {
                    if (o.AssetId == assetID && o.ScriptHash == ExecutionEngine.ExecutingScriptHash) sentAmount += o.Value;
                }
                if (sentAmount != amount) return false;
            }
            else if (assetCategory == AssetCategory.NEP5)
            {
                // Check allowance on smart contract
                BigInteger allowedAmount = (BigInteger)CallRPXContract("allowance", originator, ExecutionEngine.ExecutingScriptHash);
                if (allowedAmount < amount) return false;
            }

            // Unknown asset category
            return false;
        }

        private static void RemoveOffer(byte[] tradingPair, byte[] offerHash)
        {
            Storage.Delete(Storage.CurrentContext, offerHash);
            var list = Storage.Get(Storage.CurrentContext, tradingPair);
            var index = SearchBytes(list, offerHash);
            if (index >= 0)
            {
                var endIndex = index + offerHash.Length;
                var tailCount = list.Length - endIndex;
                list = list.Range(0, index).Concat(list.Range(endIndex, tailCount));
            }
        }

        private static void TransferAssetTo(byte[] address, byte[] assetID, AssetCategory assetCategory, BigInteger amount)
        {
            if (amount <= 0) return;

            byte[] key = StoreKey(address, assetID, assetCategory);
            BigInteger currentBalance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            Storage.Put(Storage.CurrentContext, key, currentBalance + amount);
        }

        private static byte[] ToBytes(BigInteger value)
        {
            byte[] buffer = value.ToByteArray();
            return buffer;
        }

        private static byte[] Int32ToBytes(int value)
        {
            return new byte[] {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)(value)
            };
        }

        private static int BytesToInt32(byte[] array)
        {
            return (array[0] << 24) + (array[1] << 16) + (array[2] << 8) + (array[3]);
        }

        private static int SearchBytes(byte[] haystack, byte[] needle)
        {
            var len = needle.Length;
            var limit = haystack.Length - len;
            for (var i = 0; i <= limit; i++)
            {
                var k = 0;
                for (; k < len; k++)
                {
                    if (needle[k] != haystack[i + k]) break;
                }
                if (k == len) return i;
            }
            return -1;
        }

        private static byte[] StoreKey(byte[] owner, byte[] assetID, AssetCategory assetCategory)
        {
            return owner.Concat(assetID).Concat(new byte[] { (byte)assetCategory });
        }

        private static BigInteger AmountToOffer(Offer o, BigInteger amount)
        {
            return (o.OfferAmount * amount) / o.WantAmount;
        }

        private static byte[] TradingPair(Offer o) // 42 bytes
        {
            return o.OfferAssetID.
                Concat(new byte[] { (byte)o.OfferAssetCategory }).
                Concat(o.WantAssetID).
                Concat(new byte[] { (byte)o.WantAssetCategory });
        }

        private static byte[] Hash(Offer o)
        {
            return Hash256(ToBuffer(o));
        }

        private static byte[] ToBuffer(Offer o)
        {
            byte[] offerAmountBuffer = ToBytes(o.OfferAmount);
            byte[] offerAmountBufferLength = Int32ToBytes(offerAmountBuffer.Length);
            byte[] wantAmountBuffer = ToBytes(o.WantAmount);
            byte[] wantAmountBufferLength = Int32ToBytes(wantAmountBuffer.Length);
            return o.MakerAddress
                .Concat(TradingPair(o))
                .Concat(offerAmountBufferLength)
                .Concat(offerAmountBuffer)
                .Concat(wantAmountBufferLength)
                .Concat(wantAmountBuffer)
                .Concat(o.Nonce);
        }

        // TODO: test this!
        private static Offer FromBuffer(byte[] buffer)
        {
            int offerAmountBufferLength = BytesToInt32(buffer.Range(62, 4));
            int wantAmountBufferLength = BytesToInt32(buffer.Range(66 + offerAmountBufferLength, 4));
            return NewOffer(
                buffer.Range(0, 20), // Maker Address
                buffer.Range(20, 20), buffer[40], buffer.Range(66, offerAmountBufferLength), // Offer AssetID, Category, Amount
                buffer.Range(41, 20), buffer[61], buffer.Range(70 + offerAmountBufferLength, wantAmountBufferLength), // Want AssetID, Category, Amount
                buffer.Range(70 + offerAmountBufferLength + wantAmountBufferLength, buffer.Length - (70 + offerAmountBufferLength + wantAmountBufferLength)) // Nonce - TODO: may overflow 32bits buffer.Length?
                );
        }
    }
}

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

        [DisplayName("filled")]
        public static event Action<byte[], byte[], BigInteger> Filled; // TODO: use me

        [DisplayName("refunded")]
        public static event Action<byte[], BigInteger> Refunded; // TODO: use me

        private static readonly byte[] Owner = { // public key or script hash
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        private static ulong feeFactor = 100000; // 1 => 0.001%
        private static BigInteger maxFee = 3000; // 3000/10000 = 0.3%

        // Contract States
        private static byte[] Pending = {};          // only can initialize
        private static byte[] Active = { 0x01 };     // all operations active
        private static byte[] Inactive = { 0x02 };   // trading halted - only can do cancel, withdrawl & owner actions

        // TODO: do we need an enum? just use `private static byte SystemAsset = 0x00`
        private enum AssetCategory : byte
        {
            SystemAsset = 0x00,
            SmartContract = 0x01
        }

        private class Offer
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

            public Offer(
                byte[] makerAddress,
                byte[] offerAssetID, byte offerAssetCategory, byte[] offerAmount,
                byte[] wantAssetID, byte wantAssetCategory, byte[] wantAmount,
                byte[] nonce)
            {
                MakerAddress = makerAddress.Take(20);
                OfferAssetID = offerAssetID.Take(20);
                OfferAssetCategory = (AssetCategory)offerAssetCategory;
                OfferAmount = BytesToInt(offerAmount);
                WantAssetID = wantAssetID.Take(20);
                WantAssetCategory = (AssetCategory)wantAssetCategory;
                WantAmount = BytesToInt(wantAmount);
                AvailableAmount = BytesToInt(wantAmount);
                Nonce = nonce.Take(32);
            }
        }

        /// <summary>
        ///   This is the Switcheo smart contract entrypoint.
        /// 
        ///   Parameter List: 0705
        ///   Return List: 05
        /// </summary>
        /// <param name="method">
        ///   The method to be invoked.
        /// </param>
        /// <param name="args">
        ///   Input parameters for the delegated method.
        /// </param>
        public static object Main(string operation, params object[] args)
        {
            if (Runtime.Trigger == TriggerType.Verification)
            {
                if (Owner.Length == 20)
                {
                    return Runtime.CheckWitness(Owner);
                }
                else if (Owner.Length == 33)
                {
                    byte[] signature = operation.AsByteArray();
                    return VerifySignature(signature, Owner);
                }
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
                // == Init ==
                if (operation == "initialize")
                {
                    // TODO: check owner
                    if (Storage.Get(Storage.CurrentContext, "state") != Pending) return false;

                    if (args.Length != 3) return false;
                    if (!SetFees((BigInteger)args[0], (BigInteger)args[1])) return false;
                    if (!SetFeeAddress((byte[])args[2])) return false;

                    Storage.Put(Storage.CurrentContext, "state", Active);
                    return true;
                }


                // TODO: check that contract has been initialized
                // == Query ==
                if (operation == "getOffers")
                    return new byte[] { };
                if (operation == "getOffer")
                    return new byte[] { };
                if (operation == "tradingStatus")
                {
                    var state = Storage.Get(Storage.CurrentContext, "state"); // TODO: should we just return the byte?
                    if (state == Pending) return "pending";
                    if (state == Active) return "active";
                    if (state == Inactive) return "inactive";
                    return "unknown";
                }
                if (operation == "getMakerFee")
                {
                    if (args.Length != 2) return false;
                    return Storage.Get(Storage.CurrentContext, "makerFee");
                }
                if (operation == "getTakerFee")
                {
                    if (args.Length != 2) return false;
                    return Storage.Get(Storage.CurrentContext, "takerFee");
                }

                // == Execute ==                
                if (operation == "makeOffer")
                {
                    // TODO: check that contract is not inactive
                    if (args.Length != 7) return false;
                    return MakeOffer((byte[])args[0], (byte[])args[1], (byte)args[2], (byte[])args[3], (byte[])args[4], (byte)args[5], (byte[])args[6], (byte[])args[7], (byte[])args[8]);
                }
                if (operation == "fillOffer")
                    // TODO: check that contract is not inactive
                    return false;
                if (operation == "cancelOffer")
                    return false;
                if (operation == "withdrawAssets")
                    return false;

                // == Owner ==
                if (args.Length < 1) return false; // TODO: how to verify?
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
                    return SetFeeAddress((byte[]) args[0]);
                }
            }

            return false;
        }

        private static byte[] GetOffers(byte[] offerAssetID, byte[] offerAssetCategory, byte[] wantAssetID, byte[] wantAssetCategory)
        {
            return Storage.Get(Storage.CurrentContext, offerAssetID.Concat(offerAssetCategory).Concat(wantAssetID).Concat(wantAssetCategory));
        }

        private static bool MakeOffer(
            byte[] makerAddress,
            byte[] offerAssetID, byte offerAssetCategory, byte[] offerAmount, 
            byte[] wantAssetID, byte wantAssetCategory, byte[] wantAmount, 
            byte[] nonce, byte[] signature
            )
        {
            // Check that the maker is honest
            if (!Runtime.CheckWitness(makerAddress)) return false;
                        
            // Initialize the offer
            var offer = new Offer(makerAddress, offerAssetID, offerAssetCategory, offerAmount, wantAssetID, wantAssetCategory, wantAmount, nonce);
            var tradingPair = TradingPair(offer);
            var offerHash = Hash(offer);

            // Check that nonce is not repeated
            if (Storage.Get(Storage.CurrentContext, offerHash).Length != 0) return false;

            // Check that the amounts > 0
            if (offer.OfferAmount <= 0 || offer.WantAmount <= 0) return false;

            // Check that the amounts < 2^(2^32)
            if (IntToBytes(offer.OfferAmount).Length > 32 || IntToBytes(offer.WantAmount).Length > 32) return false;

            // Check the trade is across different assets
            if (offer.OfferAssetID == offer.WantAssetID && offer.OfferAssetCategory == offer.WantAssetCategory) return false;

            // TODO: Check that asset IDs are valid

            // Get current transaction
            var currentTxn = (Transaction) ExecutionEngine.ScriptContainer;
            var outputs = currentTxn.GetReferences();
            
            // Verify that the offer really has the indicated assets available
            if (offer.OfferAssetCategory == AssetCategory.SystemAsset)
            {
                // Check the current transaction for the system assets
                BigInteger sentAmount = 0;
                foreach (var o in outputs)
                {
                    if (o.AssetId == offerAssetID && o.ScriptHash == ExecutionEngine.ExecutingScriptHash)
                    {
                        sentAmount += o.Value;
                    }
                }
                if (sentAmount != offer.OfferAmount) return false;
            }
            else if (offer.OfferAssetCategory == AssetCategory.SmartContract)
            {
                // Check that no assets were sent by mistake
                if (outputs.Length > 0) return false;

                // TODO: Do we need to prevent re-entrancy due to external call?

                // Check allowance on smart contract
                BigInteger allowedAmount = (BigInteger) CallRPXContract("allowance", makerAddress, ExecutionEngine.ExecutingScriptHash);
                if (allowedAmount < offer.OfferAmount) return false;

                // Transfer token
                bool transferSuccessful = (bool) CallRPXContract("transferFrom", ExecutionEngine.ExecutingScriptHash, makerAddress, ExecutionEngine.ExecutingScriptHash, offer.OfferAmount);
                if (!transferSuccessful) return false;
            }
            else 
            {
                // Unknown asset category
                return false;
            }

            // Store a mapping on the trading pair to the offer
            byte[] offers = Storage.Get(Storage.CurrentContext, tradingPair);
            Storage.Put(Storage.CurrentContext, tradingPair, offers.Concat(offerHash));

            // Store the maker address and filled amount under the offer hash
            Storage.Put(Storage.CurrentContext, offerHash, ToBuffer(offer));

            return true;
        }

        private static bool FillOffer(
            byte[] fillerAddress, byte[] offerHash,
            BigInteger amountToFill, byte[] signature)
        {
            // Check that the filler is honest
            if (!Runtime.CheckWitness(fillerAddress)) return false;
            
            // Check that the offer exists 
            byte[] offerData = Storage.Get(Storage.CurrentContext, offerHash);
            if (offerData.Length == 0) return false;
            Offer offer = FromBuffer(offerData);

            // Check that 0 < amount to fill <= available amount
            if (amountToFill <= 0 || amountToFill > offer.AvailableAmount) return false;

            // Check that the filler is different from the maker
            if (fillerAddress == offer.MakerAddress) return false;

            // Calculate amount that can be offered
            BigInteger amountToOffer = (offer.OfferAmount * amountToFill) / offer.WantAmount;
            if (amountToOffer == 0) return false;

            // TODO: Check that the required amounts are sent

            // Calculate fees
            BigInteger makerFeeRate = BytesToInt(Storage.Get(Storage.CurrentContext, "makerFee"));
            BigInteger takerFeeRate = BytesToInt(Storage.Get(Storage.CurrentContext, "takerFee"));
            BigInteger makerFee = (amountToOffer * makerFeeRate) / feeFactor;
            BigInteger takerFee = (amountToOffer * takerFeeRate) / feeFactor;

            // Move fees
            if (makerFee > 0)
            {
                var storeKey = StoreKey(Owner, offer.WantAssetID, offer.WantAssetCategory);
                BigInteger balance = BytesToInt(Storage.Get(Storage.CurrentContext, storeKey));
                Storage.Put(Storage.CurrentContext, storeKey, balance + amountToFill);
            }
            if (takerFee > 0)
            {
                var storeKey = StoreKey(Owner, offer.OfferAssetID, offer.OfferAssetCategory);
                BigInteger balance = BytesToInt(Storage.Get(Storage.CurrentContext, storeKey));
                Storage.Put(Storage.CurrentContext, storeKey, balance + amountToFill);
            }

            // Move asset to the maker balance 
            var makerKey = StoreKey(offer.MakerAddress, offer.WantAssetID, offer.WantAssetCategory);
            BigInteger makerBalance = BytesToInt(Storage.Get(Storage.CurrentContext, makerKey));
            Storage.Put(Storage.CurrentContext, makerKey, makerBalance + amountToFill - makerFee);

            // Move asset to the taker balance
            var fillerKey = StoreKey(fillerAddress, offer.OfferAssetID, offer.OfferAssetCategory);
            BigInteger fillerBalance = BytesToInt(Storage.Get(Storage.CurrentContext, fillerKey));
            Storage.Put(Storage.CurrentContext, fillerKey, fillerBalance + amountToOffer - takerFee);

            // Update filled amount
            offer.AvailableAmount -= amountToFill;

            // Remove order if completely filled
            if (offer.AvailableAmount == 0)
            {
                Storage.Delete(Storage.CurrentContext, offerHash);
                var tradingPair = TradingPair(offer);
                var list = Storage.Get(Storage.CurrentContext, tradingPair);
                var index = SearchBytes(list, offerHash);
                if (index >= 0)
                {
                    var endIndex = index + offerHash.Length;
                    var tailCount = list.Length - endIndex;
                    list = list.Range(0, index).Concat(list.Range(endIndex, tailCount));
                }
            }

            return true;
        }

        private static bool CancelOffer(byte[] cancellerAddress, byte[] offerHash, byte[] signature)
        {
            // Check that the canceller is honest
            if (!Runtime.CheckWitness(cancellerAddress)) return false;

            // Check that the offer exists
            byte[] offerData = Storage.Get(Storage.CurrentContext, offerHash);
            if (offerData.Length == 0) return false;
            Offer offer = FromBuffer(offerData);

            // Check that the canceller is also the offer maker
            if (offer.MakerAddress != cancellerAddress) return false;

            // Move funds to withdrawal address
            Storage.Put(Storage.CurrentContext, StoreKey(cancellerAddress, offer.OfferAssetID, offer.OfferAssetCategory), offer.AvailableAmount);

            // Remove offer
            Storage.Delete(Storage.CurrentContext, offerHash);
                        
            return true;
        }

        private static bool WithdrawAssets(byte[] holderAddress, byte[] assetID, AssetCategory assetCategory, string withdrawToThisAddress)
        {
            // Check that the holder is honest
            if (!Runtime.CheckWitness(holderAddress)) return false;

            // Check that there are asset value > 0 in balance
            BigInteger amount = BytesToInt(Storage.Get(Storage.CurrentContext, StoreKey(holderAddress, assetID, assetCategory)));
            if (amount <= 0) return false;

            // Transfer asset
            if (assetCategory == AssetCategory.SystemAsset)
            {
                // TODO: this should be allowed/rejected in the validation script instead?
            }
            else if (assetCategory == AssetCategory.SmartContract)
            {
                // TODO: Do we need to prevent re-entrancy due to external call?

                // Transfer token
                bool transferSuccessful = (bool) CallRPXContract("transfer", ExecutionEngine.ExecutingScriptHash, holderAddress, amount);
                if (!transferSuccessful) return false;
            }

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

        private static byte[] IntToBytes(BigInteger value)
        {
            byte[] buffer = value.ToByteArray();
            return buffer;
        }

        private static BigInteger BytesToInt(byte[] array)
        {
            var buffer = new BigInteger(array);
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
            return (array[0] << 24) + (array[1] << 16) + (array[2] << 8) + (array[3]) ;
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
            return owner.Concat(assetID).Concat(new byte[] { (byte) assetCategory });
        }

        private static byte[] TradingPair(Offer o) // 42 bytes
        {
            return o.OfferAssetID.
                Concat(new byte[] { (byte) o.OfferAssetCategory }).
                Concat(o.WantAssetID).
                Concat(new byte[] { (byte) o.WantAssetCategory });
        }

        private static byte[] Hash(Offer o)
        {
            return Hash256(ToBuffer(o));
        }

        private static byte[] ToBuffer(Offer o)
        {
            byte[] offerAmountBuffer = IntToBytes(o.OfferAmount);
            byte[] offerAmountBufferLength = Int32ToBytes(offerAmountBuffer.Length);
            byte[] wantAmountBuffer = IntToBytes(o.WantAmount);
            byte[] wantAmountBufferLength = Int32ToBytes(wantAmountBuffer.Length);
            return o.MakerAddress
                .Concat(TradingPair(o))
                .Concat(offerAmountBufferLength)
                .Concat(offerAmountBuffer)
                .Concat(wantAmountBufferLength)
                .Concat(wantAmountBuffer)
                .Concat(o.Nonce);
        }

        private static Offer FromBuffer(byte[] buffer)
        {
            int offerAmountBufferLength = BytesToInt32(buffer.Range(62, 4));
            int wantAmountBufferLength = BytesToInt32(buffer.Range(66 + offerAmountBufferLength, 4));
            return new Offer(
                buffer.Range(0, 20), // Maker Address
                buffer.Range(20, 20), buffer[40], buffer.Range(66, offerAmountBufferLength), // Offer AssetID, Category, Amount
                buffer.Range(41, 20), buffer[61], buffer.Range(70 + offerAmountBufferLength, wantAmountBufferLength), // Want AssetID, Category, Amount
                buffer.Range(70 + offerAmountBufferLength + wantAmountBufferLength, buffer.Length - (70 + offerAmountBufferLength + wantAmountBufferLength)) // Nonce - TODO: may overflow 32bits buffer.Length?
                );
        }
    }
}
